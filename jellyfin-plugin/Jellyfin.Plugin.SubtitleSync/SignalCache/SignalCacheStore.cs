using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleSync.SignalCache;

/// <summary>
/// Stores speech signals as gzipped, bit-packed files under the server's data
/// path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it lives.</b> <c>Path.Join(IApplicationPaths.DataPath, "subtitlesync")</c>,
/// which is what Intro Skipper does with its chromaprints and for the same
/// reason. Deliberately <em>not</em> <c>BasePlugin.DataFolderPath</c>: that
/// resolves to the plugin's install directory under <c>PluginsPath</c> and is
/// wiped on every plugin update, so the cache would silently vanish on upgrade
/// and every user would pay the 115 MB re-download once per release. See
/// <c>research/jellyfin-10.11-plugin-api.md</c> section 10. Not
/// <c>CachePath</c> either: an entry costs an audio decode and a full PCM
/// transfer to regenerate, which is more than a cache directory promises to
/// protect.
/// </para>
/// <para>
/// <b>Layout.</b> One file per entry, named <c>{key}.sscz</c>, flat. Sixty-four
/// hex characters plus an extension is well inside every filesystem's name
/// limit, and a flat directory is fine at this scale: the default 512 MB cap
/// holds on the order of tens of thousands of entries, and nothing ever lists
/// the directory on the request path except eviction and the size readout.
/// </para>
/// <para>
/// <b>Writes are atomic.</b> Content goes to a uniquely named scratch file in
/// the same directory and is then moved into place, so a crash, a full disk or
/// two simultaneous POSTs of the same key can never leave a half-written entry
/// visible under its real name. The move is the only operation that publishes
/// anything.
/// </para>
/// <para>
/// <b>Eviction is least-recently-used</b>, driven by the filesystem's access
/// time, which this class maintains itself on every read and write rather than
/// trusting the mount to (<c>relatime</c> and <c>noatime</c> are common, and a
/// Docker volume is anyone's guess). Checked after each write, so the cap is
/// respected without a background timer. An entry larger than the entire cap is
/// dropped rather than kept, because keeping it would mean permanently
/// exceeding the limit.
/// </para>
/// </remarks>
public sealed partial class SignalCacheStore : ISignalCacheStore, IDisposable
{
    /// <summary>
    /// The directory created under <c>DataPath</c>.
    /// </summary>
    public const string DirectoryName = "subtitlesync";

    /// <summary>
    /// The extension every entry carries. Distinct from the scratch suffix so a
    /// directory listing can never confuse the two.
    /// </summary>
    private const string EntryExtension = ".sscz";

    private const string ScratchExtension = ".tmp";

    private readonly string _cacheDirectory;
    private readonly Func<long> _sizeLimitBytes;
    private readonly ILogger<SignalCacheStore> _logger;

    /// <summary>
    /// Serialises writes, replacement and eviction. Reads are not held up by it.
    /// </summary>
    /// <remarks>
    /// The atomic move already guarantees readers see a whole entry. This exists
    /// so that two writers do not race the same publish, and so that eviction
    /// cannot delete an entry another writer is in the middle of replacing.
    /// </remarks>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalCacheStore"/> class,
    /// rooted at the server's data path.
    /// </summary>
    /// <param name="applicationPaths">The server's paths.</param>
    /// <param name="logger">Logger.</param>
    public SignalCacheStore(IApplicationPaths applicationPaths, ILogger<SignalCacheStore> logger)
        : this(
            Path.Join(
                (applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths))).DataPath,
                DirectoryName),
            () => (Plugin.Instance?.Configuration.SignalCacheSizeLimitMb ?? 0L) * 1024L * 1024L,
            logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalCacheStore"/> class at
    /// an explicit directory.
    /// </summary>
    /// <param name="cacheDirectory">Where entries are stored.</param>
    /// <param name="sizeLimitBytes">
    /// The cap in bytes, read fresh on every write so a change on the
    /// configuration page takes effect without a restart. Zero is unbounded.
    /// </param>
    /// <param name="logger">Logger.</param>
    public SignalCacheStore(string cacheDirectory, Func<long> sizeLimitBytes, ILogger<SignalCacheStore> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDirectory);
        ArgumentNullException.ThrowIfNull(sizeLimitBytes);
        ArgumentNullException.ThrowIfNull(logger);

        _cacheDirectory = cacheDirectory;
        _sizeLimitBytes = sizeLimitBytes;
        _logger = logger;
    }

    /// <inheritdoc />
    public string CacheDirectory => _cacheDirectory;

    /// <summary>
    /// The file a key maps to.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <returns>An absolute-or-relative path inside <see cref="CacheDirectory"/>.</returns>
    /// <exception cref="ArgumentException">The key is not well formed.</exception>
    /// <remarks>
    /// The only place a key ever becomes a path, so it is the only place the
    /// validation has to hold. Because <see cref="SignalCacheKey.IsValid"/>
    /// admits nothing but sixty-four characters of lowercase hex, the joined
    /// name has no separator, no dot, no drive letter and no device name in it,
    /// and the result is always a direct child of the cache directory.
    /// </remarks>
    public string ResolvePath(string key)
    {
        SignalCacheKey.ThrowIfInvalid(key, nameof(key));

        return Path.Join(_cacheDirectory, key + EntryExtension);
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);

        byte[] envelope;
        try
        {
            envelope = await ReadEnvelopeAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            // Not gzip, or gzip whose own trailer does not check out.
            return DiscardDamaged(path, key, SignalPayloadError.ChecksumMismatch);
        }
        catch (IOException)
        {
            // Includes EndOfStreamException: a stream that stops mid-member,
            // which is exactly what a crash during a write used to leave behind.
            return DiscardDamaged(path, key, SignalPayloadError.LengthMismatch);
        }

        var error = SpeechSignalCodec.Validate(envelope);
        if (error != SignalPayloadError.None)
        {
            return DiscardDamaged(path, key, error);
        }

        TouchQuietly(path);
        return envelope;
    }

    /// <inheritdoc />
    public async Task WriteAsync(string key, ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken)
    {
        var path = ResolvePath(key);

        var error = SpeechSignalCodec.Validate(envelope.Span);
        if (error != SignalPayloadError.None)
        {
            throw new InvalidDataException("Refusing to cache an invalid speech signal payload: " + error + ".");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_cacheDirectory);

            var scratch = Path.Join(
                _cacheDirectory,
                key + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ScratchExtension);

            try
            {
                var file = new FileStream(
                    scratch,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous);
                await using (file.ConfigureAwait(false))
                {
                    var gzip = new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: true);
                    await using (gzip.ConfigureAwait(false))
                    {
                        await gzip.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
                    }

                    await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(scratch, path, overwrite: true);
            }
            finally
            {
                DeleteQuietly(scratch);
            }

            TouchQuietly(path);
            LogStored(key, envelope.Length, new FileInfo(path).Length);

            Evict();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1024:Use properties where appropriate",
        Justification = "This enumerates a directory and stats every file in it. A property would invite it into a loop.")]
    public SignalCacheStats GetStats()
    {
        var entries = Entries();

        return new SignalCacheStats
        {
            EntryCount = entries.Count,
            TotalBytes = entries.Sum(e => e.Length),
            SizeLimitBytes = Math.Max(0, _sizeLimitBytes()),
            Directory = _cacheDirectory,
        };
    }

    /// <inheritdoc />
    public int Clear()
    {
        _writeLock.Wait();
        try
        {
            var removed = 0;
            foreach (var entry in Entries())
            {
                if (DeleteQuietly(entry.FullName))
                {
                    removed++;
                }
            }

            LogCleared(removed);
            return removed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Releases the write lock's handle.
    /// </summary>
    /// <remarks>
    /// The container holds this as a singleton for the life of the process, so
    /// this only ever runs at shutdown. It exists because owning a
    /// <see cref="SemaphoreSlim"/> without disposing it is a leak on paper even
    /// when it never matters in practice.
    /// </remarks>
    public void Dispose()
    {
        _writeLock.Dispose();
    }

    private static async Task<byte[]> ReadEnvelopeAsync(string path, CancellationToken cancellationToken)
    {
        // FileShare.Delete so a concurrent replacement's File.Move is not blocked
        // by this read on Windows.
        var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous);
        await using var configuredFile = file.ConfigureAwait(false);
        var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var configuredGzip = gzip.ConfigureAwait(false);

        // Bounded, so a hand-crafted cache file cannot decompress into an
        // out-of-memory condition. One byte over the cap is enough to know.
        var limit = SpeechSignalCodec.MaxEnvelopeLength + 1;
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await gzip.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk.AsSpan(0, read));
            if (buffer.Length > limit)
            {
                throw new InvalidDataException("Cache entry decompresses to more than the maximum envelope size.");
            }
        }

        return buffer.ToArray();
    }

    private byte[]? DiscardDamaged(string path, string key, SignalPayloadError error)
    {
        LogDamagedEntry(key, error);
        DeleteQuietly(path);
        return null;
    }

    /// <summary>
    /// Lists the entries this store owns, ignoring anything else in the
    /// directory. The name has to validate as a key, so a scratch file, a stray
    /// note or another tool's data is never counted and never deleted.
    /// </summary>
    /// <returns>The entries, unordered.</returns>
    private List<FileInfo> Entries()
    {
        var directory = new DirectoryInfo(_cacheDirectory);
        if (!directory.Exists)
        {
            return [];
        }

        var found = new List<FileInfo>();
        foreach (var file in directory.EnumerateFiles("*" + EntryExtension, SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(file.Extension, EntryExtension, StringComparison.Ordinal))
            {
                continue;
            }

            if (SignalCacheKey.IsValid(Path.GetFileNameWithoutExtension(file.Name)))
            {
                found.Add(file);
            }
        }

        return found;
    }

    /// <summary>
    /// Brings the cache back under its cap, oldest access first.
    /// </summary>
    /// <remarks>
    /// Called with the write lock held. Last write time breaks ties, because a
    /// burst of writes can share a filesystem timestamp tick and an arbitrary
    /// order there would make the policy untestable.
    /// </remarks>
    private void Evict()
    {
        var limit = _sizeLimitBytes();
        if (limit <= 0)
        {
            return;
        }

        var entries = Entries();
        var total = entries.Sum(e => e.Length);
        if (total <= limit)
        {
            return;
        }

        foreach (var entry in entries.OrderBy(e => e.LastAccessTimeUtc).ThenBy(e => e.LastWriteTimeUtc))
        {
            if (total <= limit)
            {
                break;
            }

            if (DeleteQuietly(entry.FullName))
            {
                total -= entry.Length;
                LogEvicted(Path.GetFileNameWithoutExtension(entry.Name), entry.Length);
            }
        }
    }

    /// <summary>
    /// Marks an entry as just used, which is the only input the eviction order
    /// has.
    /// </summary>
    /// <param name="path">The entry.</param>
    private static void TouchQuietly(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
            // A read-only mount or a racing delete. The consequence is a worse
            // eviction order, not a failed request.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool DeleteQuietly(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Logs a stored entry.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="envelopeBytes">The uncompressed envelope size.</param>
    /// <param name="storedBytes">What it cost on disk.</param>
    [LoggerMessage(
        EventId = 9001,
        Level = LogLevel.Debug,
        Message = "Cached speech signal {Key}: {EnvelopeBytes} bytes packed, {StoredBytes} bytes on disk")]
    private partial void LogStored(string key, int envelopeBytes, long storedBytes);

    /// <summary>
    /// Logs an entry that failed validation on read and was dropped.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="error">What was wrong with it.</param>
    [LoggerMessage(
        EventId = 9002,
        Level = LogLevel.Warning,
        Message = "Discarding damaged speech signal cache entry {Key}: {Error}")]
    private partial void LogDamagedEntry(string key, SignalPayloadError error);

    /// <summary>
    /// Logs an eviction.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="bytes">How much was reclaimed.</param>
    [LoggerMessage(
        EventId = 9003,
        Level = LogLevel.Debug,
        Message = "Evicted least recently used speech signal {Key}, reclaiming {Bytes} bytes")]
    private partial void LogEvicted(string key, long bytes);

    /// <summary>
    /// Logs a manual clear from the configuration page.
    /// </summary>
    /// <param name="removed">How many entries went.</param>
    [LoggerMessage(
        EventId = 9004,
        Level = LogLevel.Information,
        Message = "Cleared the speech signal cache: {Removed} entries removed")]
    private partial void LogCleared(int removed);
}
