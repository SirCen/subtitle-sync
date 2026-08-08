using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleSync.SignalCache;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests.SignalCache;

/// <summary>
/// Covers <see cref="SignalCacheStore"/>: the on-disk half of the cache.
/// </summary>
/// <remarks>
/// These tests use a real temporary directory rather than an abstraction,
/// because most of what is being asserted is filesystem behaviour: that a key
/// cannot name a file outside the cache directory, that a half-written entry is
/// never visible, and that a corrupt file is detected on read instead of being
/// handed back as a signal.
/// </remarks>
public sealed class SignalCacheStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _cacheDirectory;

    public SignalCacheStoreTests()
    {
        _root = Path.Join(Path.GetTempPath(), "subtitlesync-tests-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        _cacheDirectory = Path.Join(_root, "subtitlesync");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test run over.
        }
    }

    private SignalCacheStore CreateStore(long maxBytes = 0) =>
        new(_cacheDirectory, () => maxBytes, NullLogger<SignalCacheStore>.Instance);

    private static string Key(char fill) => new(fill, 64);

    private static byte[] Signal(int length, int seed)
    {
        var random = new Random(seed);
        var samples = new byte[length];
        for (var i = 0; i < length; i++)
        {
            samples[i] = (byte)random.Next(2);
        }

        return samples;
    }

    // ------------------------------------------------------------------
    // The happy path
    // ------------------------------------------------------------------

    /// <summary>
    /// What was posted is what comes back.
    /// </summary>
    [Fact]
    public async Task RoundTripsAnEnvelopeThroughDisk()
    {
        var store = CreateStore();
        var samples = Signal(1_003, seed: 1);
        var envelope = SpeechSignalCodec.Encode(samples);

        await store.WriteAsync(Key('a'), envelope, CancellationToken.None);
        var read = await store.ReadAsync(Key('a'), CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(envelope, read);
        Assert.Equal(samples, SpeechSignalCodec.Decode(read));
    }

    /// <summary>
    /// A miss is a miss, not an exception and not an empty signal.
    /// </summary>
    [Fact]
    public async Task ReturnsNullForAKeyThatWasNeverWritten()
    {
        var store = CreateStore();

        Assert.Null(await store.ReadAsync(Key('b'), CancellationToken.None));
    }

    /// <summary>
    /// The stored file is gzipped, and a real signal costs what the issue says
    /// it costs. Anything much larger means the packing silently regressed to a
    /// byte per sample.
    /// </summary>
    [Fact]
    public async Task StoresGzippedBitPackedBytes()
    {
        var store = CreateStore();
        var envelope = SpeechSignalCodec.Encode(Signal(360_000, seed: 2));

        await store.WriteAsync(Key('c'), envelope, CancellationToken.None);

        var file = Assert.Single(Directory.GetFiles(_cacheDirectory));
        Assert.Equal(Key('c') + ".sscz", Path.GetFileName(file));

        var raw = await File.ReadAllBytesAsync(file);
        Assert.Equal(0x1F, raw[0]);
        Assert.Equal(0x8B, raw[1]);

        // Incompressible worst case: an hour of coin flips. A real speech signal
        // is long runs and compresses far better, so this is a ceiling.
        Assert.True(raw.Length <= 50_000, "an hour of signal took " + raw.Length + " bytes on disk");

        using var gzip = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        await gzip.CopyToAsync(buffer);
        Assert.Equal(envelope, buffer.ToArray());
    }

    /// <summary>
    /// Re-posting the same key replaces the entry rather than appending or
    /// failing, and leaves exactly one file behind.
    /// </summary>
    [Fact]
    public async Task OverwritesAnExistingEntry()
    {
        var store = CreateStore();
        await store.WriteAsync(Key('d'), SpeechSignalCodec.Encode(Signal(100, seed: 3)), CancellationToken.None);

        var replacement = SpeechSignalCodec.Encode(Signal(200, seed: 4));
        await store.WriteAsync(Key('d'), replacement, CancellationToken.None);

        Assert.Single(Directory.GetFiles(_cacheDirectory));
        Assert.Equal(replacement, await store.ReadAsync(Key('d'), CancellationToken.None));
    }

    /// <summary>
    /// The store creates its own directory. Nothing else in the plugin does,
    /// and on a fresh install it will not exist.
    /// </summary>
    [Fact]
    public async Task CreatesTheCacheDirectoryOnDemand()
    {
        Directory.Delete(_cacheDirectory, recursive: true);
        var store = CreateStore();

        await store.WriteAsync(Key('e'), SpeechSignalCodec.Encode(Signal(10, seed: 5)), CancellationToken.None);

        Assert.True(Directory.Exists(_cacheDirectory));
        Assert.NotNull(await store.ReadAsync(Key('e'), CancellationToken.None));
    }

    // ------------------------------------------------------------------
    // The key is a file name, so it is a security boundary
    // ------------------------------------------------------------------

    /// <summary>
    /// A hostile key is refused before it reaches any file API. The canary file
    /// sits one directory above the cache with the exact name the store would
    /// give it, so a store that joined the key without validating would read it
    /// and this test would fail loudly instead of passing vacuously.
    /// </summary>
    [Theory]
    [InlineData("../canary")]
    [InlineData("..\\canary")]
    [InlineData("../canary.sscz")]
    [InlineData("%2e%2e%2fcanary")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("CON")]
    [InlineData("NUL")]
    [InlineData("LPT1")]
    [InlineData("")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000\0../canary")]
    public async Task RefusesToReadThroughAHostileKey(string key)
    {
        await File.WriteAllTextAsync(Path.Join(_root, "canary.sscz"), "not a signal");
        await File.WriteAllTextAsync(Path.Join(_root, "canary"), "not a signal");
        var store = CreateStore();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReadAsync(key, CancellationToken.None));
    }

    /// <summary>
    /// The same on the write side, which is the one that can destroy something.
    /// After the attempt, nothing outside the cache directory has changed and
    /// nothing inside it has appeared.
    /// </summary>
    [Theory]
    [InlineData("../canary")]
    [InlineData("..\\..\\canary")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("NUL")]
    [InlineData("aaaaaaaa")]
    public async Task RefusesToWriteThroughAHostileKey(string key)
    {
        var canary = Path.Join(_root, "canary.sscz");
        await File.WriteAllTextAsync(canary, "untouched");
        var store = CreateStore();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.WriteAsync(key, SpeechSignalCodec.Encode(Signal(10, seed: 6)), CancellationToken.None));

        Assert.Equal("untouched", await File.ReadAllTextAsync(canary));
        Assert.Empty(Directory.GetFiles(_cacheDirectory));
    }

    /// <summary>
    /// The positive half of the proof: for every accepted key the resolved path
    /// is a direct child of the cache directory. Exhaustive over the alphabet
    /// the validator permits, which is the whole input space.
    /// </summary>
    [Fact]
    public void EveryAcceptedKeyResolvesInsideTheCacheDirectory()
    {
        var store = CreateStore();
        var expectedParent = Path.GetFullPath(_cacheDirectory);

        foreach (var c in "0123456789abcdef")
        {
            var path = Path.GetFullPath(store.ResolvePath(new string(c, 64)));

            Assert.Equal(expectedParent, Path.GetDirectoryName(path));
            Assert.StartsWith(expectedParent + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Clearing only ever touches the cache directory's own entries.
    /// </summary>
    [Fact]
    public async Task ClearRemovesEveryEntryAndNothingElse()
    {
        var outsider = Path.Join(_root, "keep-me.sscz");
        await File.WriteAllTextAsync(outsider, "keep");
        var store = CreateStore();
        await store.WriteAsync(Key('a'), SpeechSignalCodec.Encode(Signal(10, seed: 7)), CancellationToken.None);
        await store.WriteAsync(Key('b'), SpeechSignalCodec.Encode(Signal(10, seed: 8)), CancellationToken.None);

        Assert.Equal(2, store.Clear());

        Assert.Empty(Directory.GetFiles(_cacheDirectory));
        Assert.True(Directory.Exists(_cacheDirectory));
        Assert.True(File.Exists(outsider));
    }

    // ------------------------------------------------------------------
    // Untrusted payloads and damaged files
    // ------------------------------------------------------------------

    /// <summary>
    /// The store re-validates what it is given. The controller checks too, but a
    /// store that trusts its caller is one refactor away from writing garbage
    /// that only fails on some future read.
    /// </summary>
    [Fact]
    public async Task RefusesToStoreAPayloadThatIsNotAValidEnvelope()
    {
        var store = CreateStore();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.WriteAsync(Key('a'), new byte[] { 1, 2, 3 }, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_cacheDirectory));
    }

    /// <summary>
    /// A file that is not gzip at all, for instance a zero-byte file left by a
    /// full disk, reads as a miss rather than throwing into the request.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("this is not gzip")]
    public async Task TreatsANonGzipCacheFileAsAMiss(string contents)
    {
        var store = CreateStore();
        await File.WriteAllTextAsync(store.ResolvePath(Key('a')), contents);

        Assert.Null(await store.ReadAsync(Key('a'), CancellationToken.None));
    }

    /// <summary>
    /// A truncated gzip stream is the shape a crash mid-write leaves behind.
    /// Returning its decodable prefix would hand back a signal that is missing
    /// its tail, which produces a confidently wrong offset rather than an error.
    /// </summary>
    [Fact]
    public async Task TreatsATruncatedCacheFileAsAMiss()
    {
        var store = CreateStore();
        await store.WriteAsync(Key('a'), SpeechSignalCodec.Encode(Signal(5_000, seed: 9)), CancellationToken.None);

        var path = store.ResolvePath(Key('a'));
        var whole = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, whole[..(whole.Length / 2)]);

        Assert.Null(await store.ReadAsync(Key('a'), CancellationToken.None));
    }

    /// <summary>
    /// A bit flipped inside the compressed body. gzip's own trailer would catch
    /// most of these, but the envelope checksum is what guarantees it.
    /// </summary>
    [Fact]
    public async Task TreatsACorruptCacheFileAsAMiss()
    {
        var store = CreateStore();
        var envelope = SpeechSignalCodec.Encode(Signal(5_000, seed: 10));
        var path = store.ResolvePath(Key('a'));

        // Written by hand so the gzip trailer is self-consistent and only the
        // envelope's own checksum can catch the damage.
        var damaged = envelope.ToArray();
        damaged[^1] ^= 0b0000_0001;
        await using (var file = File.Create(path))
        await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        {
            await gzip.WriteAsync(damaged);
        }

        Assert.Null(await store.ReadAsync(Key('a'), CancellationToken.None));
    }

    /// <summary>
    /// A damaged entry is deleted on discovery, so the next POST can replace it
    /// and the cache does not accumulate permanent holes.
    /// </summary>
    [Fact]
    public async Task DropsADamagedEntryOnRead()
    {
        var store = CreateStore();
        await File.WriteAllTextAsync(store.ResolvePath(Key('a')), "not gzip");

        await store.ReadAsync(Key('a'), CancellationToken.None);

        Assert.False(File.Exists(store.ResolvePath(Key('a'))));
    }

    /// <summary>
    /// Nothing partially written is ever visible under the entry's own name: the
    /// store writes to a scratch file and moves it into place. Asserted by
    /// checking that no scratch file survives a successful write, and that the
    /// scratch name is not one a reader would pick up.
    /// </summary>
    [Fact]
    public async Task LeavesNoScratchFilesBehind()
    {
        var store = CreateStore();

        for (var i = 0; i < 5; i++)
        {
            await store.WriteAsync(Key('a'), SpeechSignalCodec.Encode(Signal(100, seed: i)), CancellationToken.None);
        }

        var files = Directory.GetFiles(_cacheDirectory).Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { Key('a') + ".sscz" }, files);
    }

    /// <summary>
    /// Concurrent writes of the same key settle on one complete entry. Whichever
    /// wins, the file is never a blend of the two.
    /// </summary>
    [Fact]
    public async Task ConcurrentWritesOfTheSameKeyLeaveACompleteEntry()
    {
        var store = CreateStore();
        var a = SpeechSignalCodec.Encode(Signal(4_000, seed: 21));
        var b = SpeechSignalCodec.Encode(Signal(4_001, seed: 22));

        await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i =>
                store.WriteAsync(Key('a'), i % 2 == 0 ? a : b, CancellationToken.None)));

        var read = await store.ReadAsync(Key('a'), CancellationToken.None);
        Assert.NotNull(read);
        Assert.True(read.SequenceEqual(a) || read.SequenceEqual(b));
        Assert.Single(Directory.GetFiles(_cacheDirectory));
    }

    // ------------------------------------------------------------------
    // Size accounting and eviction
    // ------------------------------------------------------------------

    /// <summary>
    /// The readout the config page shows.
    /// </summary>
    [Fact]
    public async Task ReportsEntryCountAndTotalBytes()
    {
        var store = CreateStore(maxBytes: 1_000_000);
        await store.WriteAsync(Key('a'), SpeechSignalCodec.Encode(Signal(1_000, seed: 30)), CancellationToken.None);
        await store.WriteAsync(Key('b'), SpeechSignalCodec.Encode(Signal(1_000, seed: 31)), CancellationToken.None);

        var stats = store.GetStats();

        Assert.Equal(2, stats.EntryCount);
        Assert.Equal(
            Directory.GetFiles(_cacheDirectory).Sum(f => new FileInfo(f).Length),
            stats.TotalBytes);
        Assert.Equal(1_000_000, stats.SizeLimitBytes);
    }

    /// <summary>
    /// The stated policy: over the cap, the least recently used entry goes
    /// first. Access times are set explicitly because the test writes all three
    /// entries within the same filesystem timestamp tick.
    /// </summary>
    [Fact]
    public async Task EvictsTheLeastRecentlyUsedEntryOnceTheCapIsExceeded()
    {
        var oneEntry = await MeasureEntrySizeAsync();
        var store = CreateStore(maxBytes: (oneEntry * 2) + (oneEntry / 2));

        await store.WriteAsync(Key('a'), SpeechSignalCodec.Encode(Signal(2_000, seed: 40)), CancellationToken.None);
        await store.WriteAsync(Key('b'), SpeechSignalCodec.Encode(Signal(2_000, seed: 41)), CancellationToken.None);
        Touch(store.ResolvePath(Key('a')), minutesAgo: 30);
        Touch(store.ResolvePath(Key('b')), minutesAgo: 5);

        await store.WriteAsync(Key('c'), SpeechSignalCodec.Encode(Signal(2_000, seed: 42)), CancellationToken.None);

        Assert.False(File.Exists(store.ResolvePath(Key('a'))), "the oldest entry should have been evicted");
        Assert.True(File.Exists(store.ResolvePath(Key('b'))));
        Assert.True(File.Exists(store.ResolvePath(Key('c'))));
        Assert.True(store.GetStats().TotalBytes <= (oneEntry * 2) + (oneEntry / 2));
    }

    /// <summary>
    /// Reading an entry makes it recently used. Without this the policy is
    /// least-recently-<em>written</em>, which would evict exactly the entries
    /// that are earning their keep.
    /// </summary>
    [Fact]
    public async Task AReadRefreshesAnEntrysPlaceInTheQueue()
    {
        var oneEntry = await MeasureEntrySizeAsync();
        var store = CreateStore(maxBytes: (oneEntry * 2) + (oneEntry / 2));

        await store.WriteAsync(Key('a'), SpeechSignalCodec.Encode(Signal(2_000, seed: 50)), CancellationToken.None);
        await store.WriteAsync(Key('b'), SpeechSignalCodec.Encode(Signal(2_000, seed: 51)), CancellationToken.None);
        Touch(store.ResolvePath(Key('a')), minutesAgo: 30);
        Touch(store.ResolvePath(Key('b')), minutesAgo: 20);

        Assert.NotNull(await store.ReadAsync(Key('a'), CancellationToken.None));

        await store.WriteAsync(Key('c'), SpeechSignalCodec.Encode(Signal(2_000, seed: 52)), CancellationToken.None);

        Assert.True(File.Exists(store.ResolvePath(Key('a'))), "the entry that was just read should have survived");
        Assert.False(File.Exists(store.ResolvePath(Key('b'))));
    }

    /// <summary>
    /// Eviction keeps going until the total is under the cap, however many
    /// entries that takes.
    /// </summary>
    [Fact]
    public async Task EvictsAsManyEntriesAsItTakesToGetUnderTheCap()
    {
        var oneEntry = await MeasureEntrySizeAsync();

        // Fill up while unbounded, then age each entry distinctly. This is the
        // shape of a cache whose limit was lowered on the configuration page.
        var unbounded = CreateStore(maxBytes: 0);
        var age = 50;
        foreach (var c in "abcde")
        {
            await unbounded.WriteAsync(new string(c, 64), SpeechSignalCodec.Encode(Signal(2_000, seed: c)), CancellationToken.None);
            Touch(unbounded.ResolvePath(new string(c, 64)), minutesAgo: age);
            age -= 10;
        }

        var store = CreateStore(maxBytes: (oneEntry * 2) + (oneEntry / 2));
        await store.WriteAsync(Key('f'), SpeechSignalCodec.Encode(Signal(2_000, seed: 99)), CancellationToken.None);

        Assert.Equal(2, Directory.GetFiles(_cacheDirectory).Length);
        Assert.True(File.Exists(store.ResolvePath(Key('e'))), "the most recently used survivor");
        Assert.True(File.Exists(store.ResolvePath(Key('f'))), "the entry just written");
    }

    /// <summary>
    /// Zero means unbounded, which is what the configuration page documents.
    /// </summary>
    [Fact]
    public async Task DoesNotEvictWhenTheCapIsZero()
    {
        var store = CreateStore(maxBytes: 0);

        foreach (var c in "abcdef")
        {
            await store.WriteAsync(new string(c, 64), SpeechSignalCodec.Encode(Signal(2_000, seed: c)), CancellationToken.None);
        }

        Assert.Equal(6, Directory.GetFiles(_cacheDirectory).Length);
        Assert.Equal(0, store.GetStats().SizeLimitBytes);
    }

    /// <summary>
    /// An entry larger than the whole cap is not kept. Storing it would leave
    /// the cache permanently over its limit, and evicting everything else to
    /// make room for something that still does not fit is worse than a miss.
    /// </summary>
    [Fact]
    public async Task DoesNotKeepAnEntryLargerThanTheEntireCap()
    {
        var store = CreateStore(maxBytes: 64);

        await store.WriteAsync(Key('a'), SpeechSignalCodec.Encode(Signal(20_000, seed: 60)), CancellationToken.None);

        Assert.Empty(Directory.GetFiles(_cacheDirectory));
        Assert.Null(await store.ReadAsync(Key('a'), CancellationToken.None));
    }

    /// <summary>
    /// A stray file that is not one of ours is left alone by eviction. The cache
    /// directory is under the server's data path, so being a bad neighbour there
    /// is a real risk.
    /// </summary>
    [Fact]
    public async Task IgnoresFilesThatAreNotCacheEntries()
    {
        var stray = Path.Join(_cacheDirectory, "notes.txt");
        await File.WriteAllTextAsync(stray, "hello");
        var store = CreateStore(maxBytes: 1);

        await store.WriteAsync(Key('a'), SpeechSignalCodec.Encode(Signal(2_000, seed: 70)), CancellationToken.None);

        Assert.True(File.Exists(stray));
        Assert.Equal(0, store.GetStats().EntryCount);
    }

    private async Task<long> MeasureEntrySizeAsync()
    {
        var probe = Path.Join(_root, "probe");
        Directory.CreateDirectory(probe);
        var store = new SignalCacheStore(probe, () => 0, NullLogger<SignalCacheStore>.Instance);
        await store.WriteAsync(Key('a'), SpeechSignalCodec.Encode(Signal(2_000, seed: 40)), CancellationToken.None);
        var size = new FileInfo(store.ResolvePath(Key('a'))).Length;
        Directory.Delete(probe, recursive: true);
        return size;
    }

    private static void Touch(string path, int minutesAgo)
    {
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
    }
}
