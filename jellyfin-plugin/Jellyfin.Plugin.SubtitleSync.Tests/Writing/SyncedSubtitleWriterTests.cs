using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleSync.Paths;
using Jellyfin.Plugin.SubtitleSync.Writing;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests.Writing;

/// <summary>
/// The destructive half of the save endpoint, exercised against a real temporary
/// directory rather than a mock.
/// </summary>
/// <remarks>
/// Atomicity, collision handling, permissions and concurrency are all behaviour
/// of the filesystem. A fake would only prove that the writer calls the methods
/// these tests were written against, which is precisely the assurance that is
/// worthless here.
/// </remarks>
public sealed class SyncedSubtitleWriterTests : IDisposable
{
    private const string Srt = "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n";

    private readonly string _root;
    private readonly string _mediaPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncedSubtitleWriterTests"/>
    /// class, creating a throwaway library folder with one media file in it.
    /// </summary>
    public SyncedSubtitleWriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "subtitlesync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _mediaPath = Path.Combine(_root, "Movie.mkv");
        File.WriteAllText(_mediaPath, "not really a video");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test run over.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    // -----------------------------------------------------------------------
    // The happy path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WritesASiblingNamedAfterTheMediaFile()
    {
        var result = await Write();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(Path.Combine(_root, "Movie.en.synced.srt"), result.OutputPath);
        Assert.Equal(Srt, File.ReadAllText(result.OutputPath));
        Assert.Equal(Encoding.UTF8.GetByteCount(Srt), result.BytesWritten);
    }

    /// <summary>
    /// UTF-8 with no byte order mark. Jellyfin's own encoder emits the same, and
    /// a BOM would be read as part of the first cue index by some players.
    /// </summary>
    [Fact]
    public async Task WritesUtf8WithNoByteOrderMark()
    {
        var result = await Write("1\r\n00:00:01,000 --> 00:00:02,000\r\nÜnïcödé\r\n");

        var bytes = File.ReadAllBytes(result.OutputPath);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("Ünïcödé", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavesNoTemporaryFilesBehind()
    {
        await Write();

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.Empty(Directory.GetFiles(_root, ".subtitlesync-*"));
    }

    /// <summary>
    /// The headline promise of the whole issue: the track the sync was derived
    /// from is not touched.
    /// </summary>
    [Fact]
    public async Task DoesNotTouchTheSourceTrackOrTheMediaFile()
    {
        var sourcePath = Path.Combine(_root, "Movie.en.srt");
        File.WriteAllText(sourcePath, "the original");
        var mediaWrittenAt = File.GetLastWriteTimeUtc(_mediaPath);

        var result = await Write(source: SubtitleSource.External(sourcePath));

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotEqual(sourcePath, result.OutputPath);
        Assert.Equal("the original", File.ReadAllText(sourcePath));
        Assert.Equal("not really a video", File.ReadAllText(_mediaPath));
        Assert.Equal(mediaWrittenAt, File.GetLastWriteTimeUtc(_mediaPath));
    }

    [Fact]
    public async Task ASecondSaveTakesTheNextCollisionSuffix()
    {
        var first = await Write();
        var second = await Write();

        Assert.Equal(Path.Combine(_root, "Movie.en.synced.srt"), first.OutputPath);
        Assert.Equal(Path.Combine(_root, "Movie.en.synced.2.srt"), second.OutputPath);
        Assert.Equal(Srt, File.ReadAllText(first.OutputPath));
    }

    [Fact]
    public async Task OverwriteReplacesTheSourceInPlaceAndReportsIt()
    {
        var sourcePath = Path.Combine(_root, "Movie.en.srt");
        File.WriteAllText(sourcePath, "the original");

        var result = await Write(source: SubtitleSource.External(sourcePath), overwrite: true);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(result.OverwroteSource);
        Assert.Equal(sourcePath, result.OutputPath);
        Assert.Equal(Srt, File.ReadAllText(sourcePath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    /// <summary>
    /// An embedded track has no file to replace, so the overwrite setting must
    /// not turn into "replace something else".
    /// </summary>
    [Fact]
    public async Task OverwriteOfAnEmbeddedTrackStillWritesANewSibling()
    {
        var result = await Write(source: SubtitleSource.Embedded(), overwrite: true);

        Assert.False(result.OverwroteSource);
        Assert.Equal(Path.Combine(_root, "Movie.en.synced.srt"), result.OutputPath);
    }

    // -----------------------------------------------------------------------
    // Atomicity
    // -----------------------------------------------------------------------

    /// <summary>
    /// Proves the publish is a rename of an already-complete file, not a
    /// truncate-then-write. A reader hammering the file while it is replaced
    /// twenty-five times, alternating between a 200 KB document and a tiny one,
    /// must only ever see one of the three complete states. A truncating writer
    /// would show it a short prefix of the large document almost immediately.
    /// </summary>
    [Fact]
    public async Task AnOverwriteIsNeverVisibleAsATruncatedFile()
    {
        var sourcePath = Path.Combine(_root, "Movie.en.srt");
        const string Seed = "the original";
        File.WriteAllText(sourcePath, Seed);

        var large = LargeSrt(4_000);
        var lengths = new[] { Seed.Length, large.Length, Srt.Length };

        using var stop = new CancellationTokenSource();
        var observations = new ConcurrentBag<int>();

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    observations.Add(File.ReadAllText(sourcePath).Length);
                }
                catch (IOException)
                {
                    // A sharing violation on Windows is the rename in flight,
                    // not a partial read. Nothing observed, nothing recorded.
                }

                // Windows will not let a file be replaced while another handle
                // is open on it, so a reader at a 100% duty cycle would simply
                // starve the writer. This is about what a reader can see, not
                // about who wins.
                Thread.Sleep(1);
            }
        });

        var published = 0;
        for (var i = 0; i < 25; i++)
        {
            var result = await Write(
                i % 2 == 0 ? large : Srt,
                SubtitleSource.External(sourcePath),
                overwrite: true);

            if (result.Succeeded)
            {
                published++;
            }
        }

        await stop.CancelAsync();
        await reader;

        Assert.True(published > 0, "no save completed while a reader held the file");
        Assert.NotEmpty(observations);
        Assert.All(observations, length => Assert.True(
            Array.IndexOf(lengths, length) >= 0,
            "a reader saw a file of " + length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " characters, which is none of the three complete states"));
    }

    /// <summary>
    /// The concurrency requirement from the issue. Twenty simultaneous saves of
    /// the same item must produce twenty complete files, not one corrupt one.
    /// </summary>
    [Fact]
    public async Task ConcurrentSavesOfTheSameItemNeverInterleave()
    {
        const int Writers = 20;

        var results = await Task.WhenAll(
            Enumerable.Range(0, Writers).Select(_ => Task.Run(() => Write())));

        Assert.All(results, r => Assert.True(r.Succeeded, r.ErrorMessage));

        var paths = results.Select(r => r.OutputPath).ToArray();
        Assert.Equal(Writers, paths.Distinct(StringComparer.Ordinal).Count());

        foreach (var path in paths)
        {
            Assert.Equal(Srt, File.ReadAllText(path));
        }

        Assert.Equal(Writers, Directory.GetFiles(_root, "*.srt").Length);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    /// <summary>
    /// The same race with the overwrite setting on. Every writer targets the one
    /// file, so the only acceptable outcome is that it holds exactly one
    /// writer's complete output.
    /// </summary>
    [Fact]
    public async Task ConcurrentOverwritesLeaveOneCompleteFile()
    {
        var sourcePath = Path.Combine(_root, "Movie.en.srt");
        File.WriteAllText(sourcePath, "the original");

        var candidates = Enumerable.Range(0, 10)
            .Select(i => Srt.Replace(
                "Hello",
                "Hello " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
            .ToArray();

        var results = await Task.WhenAll(
            candidates.Select(text => Task.Run(() => Write(
                text,
                SubtitleSource.External(sourcePath),
                overwrite: true))));

        Assert.All(results, r => Assert.True(r.Succeeded, r.ErrorMessage));

        // Exactly one writer's document, whole. Not a blend, not a truncation.
        var written = File.ReadAllText(sourcePath);
        Assert.Contains(written, candidates, StringComparer.Ordinal);
        Assert.Single(Directory.GetFiles(_root, "*.srt"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    // -----------------------------------------------------------------------
    // Refusals
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RefusesWhenTheMediaFolderIsNotOnDisk()
    {
        var result = await SyncedSubtitleWriter.WriteAsync(
            Request(Path.Combine(_root, "gone", "Movie.mkv"), SubtitleSource.Embedded(), overwrite: false),
            Srt,
            CancellationToken.None);

        Assert.Equal(SubtitleWriteFailure.MediaFolderMissing, result.Failure);
        Assert.Contains("mounted", result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesWhenTheMediaFileIsNotOnDisk()
    {
        File.Delete(_mediaPath);

        var result = await Write();

        Assert.Equal(SubtitleWriteFailure.MediaFileMissing, result.Failure);
        Assert.Contains("Rescan", result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAMediaPathWithNoContainingFolder()
    {
        var result = await SyncedSubtitleWriter.WriteAsync(
            Request("Movie.mkv", SubtitleSource.Embedded(), overwrite: false),
            Srt,
            CancellationToken.None);

        Assert.Equal(SubtitleWriteFailure.InvalidMediaPath, result.Failure);
    }

    /// <summary>
    /// A directory sitting where the subtitle should go. The collision check does
    /// not see it - it asks about files - so the rename is what fails, and it has
    /// to fail loudly and change nothing.
    /// </summary>
    [Fact]
    public async Task RefusesWhenTheDestinationNameIsTakenByADirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Movie.en.synced.srt"));

        var result = await Write();

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    // NOT COVERED HERE: a read-only destination folder. Denying write access
    // portably from a test needs Windows ACL APIs that are not in the shared
    // framework, and a POSIX chmod does not bind a process running as root,
    // which is how Jellyfin's own container runs. That branch is verified
    // against the live server instead, by remounting the library read-only.

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a valid SRT document of roughly the requested number of cues.
    /// </summary>
    /// <param name="cues">How many cues.</param>
    /// <returns>The document.</returns>
    private static string LargeSrt(int cues)
    {
        var builder = new StringBuilder(cues * 48);

        for (var i = 1; i <= cues; i++)
        {
            builder.Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n")
                .Append("00:00:01,000 --> 00:00:02,000\r\n")
                .Append("line ").Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");

            if (i < cues)
            {
                builder.Append("\r\n");
            }
        }

        return builder.ToString();
    }

    private Task<SubtitleWriteResult> Write(
        string srt = Srt,
        SubtitleSource? source = null,
        bool overwrite = false)
        => SyncedSubtitleWriter.WriteAsync(
            Request(_mediaPath, source ?? SubtitleSource.Embedded(), overwrite),
            srt,
            CancellationToken.None);

    private static SubtitleOutputRequest Request(string mediaPath, SubtitleSource source, bool overwrite)
        => new()
        {
            MediaPath = mediaPath,
            Language = "en",
            Source = source,
            OverwriteOriginal = overwrite,
        };
}
