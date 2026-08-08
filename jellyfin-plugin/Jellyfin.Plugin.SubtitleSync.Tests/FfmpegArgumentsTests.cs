using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.SubtitleSync.MediaEncoding;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests;

/// <summary>
/// Covers <see cref="FfmpegArguments"/>, the pure argv builder for the
/// server-side audio extraction.
/// </summary>
/// <remarks>
/// Two things are being guarded here. First the wire contract: the client
/// adapter (<c>jellyfin-plugin/web/src/pcmStream.ts</c>) decodes 16 kHz mono
/// s16le from byte zero, so any flag change that reintroduces a container or
/// alters the rate silently corrupts every sync. Second the argument boundary:
/// the input path comes from a library item, and a value that ffmpeg
/// reinterprets as an option or a protocol is a server-side request forgery or
/// arbitrary-read primitive.
/// </remarks>
public class FfmpegArgumentsTests
{
    private const string PlainPath = "/media/Movies/Arrival (2016)/Arrival.mkv";

    // ------------------------------------------------------------------
    // The flag set and its order
    // ------------------------------------------------------------------

    /// <summary>
    /// Pins the whole argv. ffmpeg is positional - an option means something
    /// different either side of <c>-i</c> - so order is part of the contract,
    /// not incidental formatting.
    /// </summary>
    [Fact]
    public void BuildsTheAgreedArgumentVectorInOrder()
    {
        var args = FfmpegArguments.BuildPcmExtraction(new FfmpegPcmRequest
        {
            InputPath = PlainPath,
        });

        Assert.Equal(
            new[]
            {
                // Global options.
                "-hide_banner",
                "-nostdin",
                "-loglevel", "warning",
                "-nostats",
                "-progress", "pipe:2",

                // Input options, which must precede the input they apply to.
                "-protocol_whitelist", "file",
                "-i", "file:" + PlainPath,

                // Output options.
                "-vn",
                "-sn",
                "-dn",
                "-ac", "1",
                "-ar", "16000",
                "-c:a", "pcm_s16le",
                "-f", "s16le",

                // Output URL: stdout.
                "-",
            },
            args);
    }

    /// <summary>
    /// The rate and channel count are copied from the Python oracle
    /// (<c>reference/sync_srt.py</c>, <c>SR = 16000</c> and <c>-ac 1</c>). The
    /// VAD only accepts 16 kHz mono, so these are not tunable.
    /// </summary>
    [Fact]
    public void ResamplesToSixteenKilohertzMonoToMatchThePythonReference()
    {
        var args = FfmpegArguments.BuildPcmExtraction(Request(PlainPath));

        AssertAdjacentPair(args, "-ac", "1");
        AssertAdjacentPair(args, "-ar", "16000");
        Assert.Equal(16000, FfmpegArguments.SampleRate);
        Assert.Equal(1, FfmpegArguments.Channels);
        Assert.Equal(2, FfmpegArguments.BytesPerSample);
    }

    /// <summary>
    /// Headerless raw s16le from byte zero. The <c>s16le</c> muxer emits no
    /// container at all, unlike <c>wav</c>, which prepends a 44-byte RIFF header
    /// the client adapter would decode as audio.
    /// </summary>
    [Fact]
    public void MuxesHeaderlessRawSignedSixteenBitLittleEndianPcm()
    {
        var args = FfmpegArguments.BuildPcmExtraction(Request(PlainPath));

        AssertAdjacentPair(args, "-f", "s16le");
        AssertAdjacentPair(args, "-c:a", "pcm_s16le");
        Assert.DoesNotContain("wav", args);
    }

    /// <summary>
    /// The PCM leaves on stdout, so the output URL is the last argument and is
    /// the stdout specifier.
    /// </summary>
    [Fact]
    public void WritesThePcmToStandardOutput()
    {
        var args = FfmpegArguments.BuildPcmExtraction(Request(PlainPath));

        Assert.Equal("-", args[^1]);
    }

    /// <summary>
    /// Video, subtitle and data streams are dropped explicitly. Without this a
    /// file whose default stream selection picks up an attached cover image
    /// makes the s16le muxer fail rather than silently producing audio.
    /// </summary>
    [Fact]
    public void DropsVideoSubtitleAndDataStreams()
    {
        var args = FfmpegArguments.BuildPcmExtraction(Request(PlainPath));

        Assert.Contains("-vn", args);
        Assert.Contains("-sn", args);
        Assert.Contains("-dn", args);
    }

    // ------------------------------------------------------------------
    // Progress reporting
    // ------------------------------------------------------------------

    /// <summary>
    /// Progress must go to stderr. stdout is the PCM payload, so a progress
    /// block written there would be decoded as audio samples.
    /// </summary>
    [Fact]
    public void ReportsProgressOnStderrSoItCannotCorruptThePcm()
    {
        var args = FfmpegArguments.BuildPcmExtraction(Request(PlainPath));

        AssertAdjacentPair(args, "-progress", "pipe:2");
        Assert.DoesNotContain("pipe:1", args);
    }

    /// <summary>
    /// <c>-nostats</c> suppresses ffmpeg's own interleaved status line, leaving
    /// stderr as a clean stream of <c>key=value</c> progress blocks plus real
    /// warnings, which is far cheaper to parse.
    /// </summary>
    [Fact]
    public void SuppressesTheInterleavedStatusLineSoProgressBlocksParseCleanly()
    {
        var args = FfmpegArguments.BuildPcmExtraction(Request(PlainPath));

        Assert.Contains("-nostats", args);
        AssertAdjacentPair(args, "-loglevel", "warning");
        Assert.Contains("-hide_banner", args);
    }

    /// <summary>
    /// Progress reporting can be turned off for callers that do not read stderr
    /// for anything but errors. Everything else about the vector is unchanged.
    /// </summary>
    [Fact]
    public void ProgressReportingCanBeDisabled()
    {
        var args = FfmpegArguments.BuildPcmExtraction(new FfmpegPcmRequest
        {
            InputPath = PlainPath,
            ReportProgress = false,
        });

        Assert.DoesNotContain("-progress", args);
        Assert.DoesNotContain("pipe:2", args);
        AssertAdjacentPair(args, "-f", "s16le");
    }

    // ------------------------------------------------------------------
    // Stream selection
    // ------------------------------------------------------------------

    /// <summary>
    /// Jellyfin's <c>MediaStream.Index</c> is the absolute stream index within
    /// the container, which is what <c>-map 0:N</c> takes. It is deliberately
    /// not <c>-map 0:a:N</c>, which counts only audio streams and would select
    /// the wrong track on any file with video first.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void SelectsTheRequestedAudioStreamByAbsoluteContainerIndex(int index)
    {
        var args = FfmpegArguments.BuildPcmExtraction(new FfmpegPcmRequest
        {
            InputPath = PlainPath,
            AudioStreamIndex = index,
        });

        AssertAdjacentPair(args, "-map", "0:" + index.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// <c>-map</c> is an output option, so it has to land after <c>-i</c>.
    /// Before it, ffmpeg rejects it outright.
    /// </summary>
    [Fact]
    public void PlacesMapAfterTheInputBecauseItIsAnOutputOption()
    {
        var args = FfmpegArguments.BuildPcmExtraction(new FfmpegPcmRequest
        {
            InputPath = PlainPath,
            AudioStreamIndex = 1,
        });

        Assert.True(args.IndexOf("-map") > args.IndexOf("-i"));
    }

    /// <summary>
    /// With no index requested, ffmpeg's own best-stream selection picks the
    /// default audio track, so no <c>-map</c> is emitted at all.
    /// </summary>
    [Fact]
    public void OmitsMapWhenNoStreamIndexIsRequested()
    {
        var args = FfmpegArguments.BuildPcmExtraction(Request(PlainPath));

        Assert.DoesNotContain("-map", args);
    }

    /// <summary>
    /// A negative index is a caller bug, and left unchecked it would be emitted
    /// as <c>0:-1</c>.
    /// </summary>
    [Fact]
    public void RejectsANegativeStreamIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FfmpegArguments.BuildPcmExtraction(new FfmpegPcmRequest
            {
                InputPath = PlainPath,
                AudioStreamIndex = -1,
            }));
    }

    // ------------------------------------------------------------------
    // Injection: options
    // ------------------------------------------------------------------

    /// <summary>
    /// A path beginning with <c>-</c> is the classic argv injection. The
    /// <c>file:</c> prefix moves the dash out of position zero so no ffmpeg
    /// argument parser can ever see it as an option introducer.
    /// </summary>
    [Theory]
    [InlineData("-f")]
    [InlineData("-loglevel")]
    [InlineData("--")]
    [InlineData("-i /etc/shadow")]
    [InlineData("-y")]
    public void APathBeginningWithADashIsNeverSeenAsAnOption(string hostile)
    {
        var args = FfmpegArguments.BuildPcmExtraction(Request(hostile));

        var value = ArgumentAfter(args, "-i");
        Assert.Equal("file:" + hostile, value);
        Assert.StartsWith("file:", value, StringComparison.Ordinal);
        Assert.False(value.StartsWith('-'));
    }

    // ------------------------------------------------------------------
    // Injection: protocols
    // ------------------------------------------------------------------

    /// <summary>
    /// ffmpeg resolves a <c>scheme:</c> prefix on the input URL to a protocol
    /// handler. Unprefixed, any of these turns a library path into an
    /// arbitrary read or an outbound request from the server. Prefixing with
    /// <c>file:</c> forces the local-file handler and demotes the rest of the
    /// string to a literal filename.
    /// </summary>
    [Theory]
    [InlineData("concat:/etc/passwd|/etc/shadow")]
    [InlineData("http://attacker.example/x.mkv")]
    [InlineData("https://attacker.example/x.mkv")]
    [InlineData("tcp://127.0.0.1:8096")]
    [InlineData("rtmp://attacker.example/live")]
    [InlineData("data:audio/wav;base64,AAAA")]
    [InlineData("subfile,,start,0,end,100,,:/etc/passwd")]
    [InlineData("async:http://attacker.example/x.mkv")]
    [InlineData("cache:http://attacker.example/x.mkv")]
    [InlineData("crypto:/etc/shadow")]
    public void AProtocolSpecifierInThePathIsDemotedToALiteralFilename(string hostile)
    {
        var args = FfmpegArguments.BuildPcmExtraction(Request(hostile));

        Assert.Equal("file:" + hostile, ArgumentAfter(args, "-i"));
    }

    /// <summary>
    /// The pipe specifiers would make ffmpeg read the plugin's own stdin, or
    /// re-enter its stdout. <c>-nostdin</c> covers the process, the prefix
    /// covers the URL.
    /// </summary>
    [Theory]
    [InlineData("pipe:")]
    [InlineData("pipe:0")]
    [InlineData("pipe:1")]
    [InlineData("-")]
    [InlineData("/dev/stdin")]
    public void APipeSpecifierInThePathIsDemotedToALiteralFilename(string hostile)
    {
        var args = FfmpegArguments.BuildPcmExtraction(Request(hostile));

        Assert.Equal("file:" + hostile, ArgumentAfter(args, "-i"));
    }

    /// <summary>
    /// The prefix is applied unconditionally rather than "only if absent".
    /// Stripping or skipping an existing <c>file:</c> would hand an attacker a
    /// one-token bypass: <c>file:concat:...</c> would pass the check and then be
    /// re-parsed with <c>concat:</c> in the leading position.
    /// </summary>
    [Theory]
    [InlineData("file:/media/x.mkv")]
    [InlineData("file:concat:/etc/passwd|/etc/shadow")]
    [InlineData("FILE:http://attacker.example/x.mkv")]
    public void AnAlreadyPrefixedPathIsPrefixedAgainRatherThanTrusted(string hostile)
    {
        var args = FfmpegArguments.BuildPcmExtraction(Request(hostile));

        Assert.Equal("file:" + hostile, ArgumentAfter(args, "-i"));
    }

    /// <summary>
    /// Defence in depth behind the prefix. Some demuxers - concat, hls, image2
    /// sequences - follow references out of the file they were handed, and the
    /// prefix does not constrain those nested opens. The whitelist does, and it
    /// must sit before <c>-i</c> to apply to it.
    /// </summary>
    [Fact]
    public void RestrictsNestedDemuxerOpensToTheFileProtocol()
    {
        var args = FfmpegArguments.BuildPcmExtraction(Request(PlainPath));

        AssertAdjacentPair(args, "-protocol_whitelist", "file");
        Assert.True(args.IndexOf("-protocol_whitelist") < args.IndexOf("-i"));
    }

    /// <summary>
    /// ffmpeg reads stdin by default and will consume bytes from whatever the
    /// parent handed it. In a server process that is both a hang and a data
    /// leak.
    /// </summary>
    [Fact]
    public void DisablesStdin()
    {
        Assert.Contains("-nostdin", FfmpegArguments.BuildPcmExtraction(Request(PlainPath)));
    }

    // ------------------------------------------------------------------
    // Injection: shell metacharacters and exotic text
    // ------------------------------------------------------------------

    /// <summary>
    /// The vector is handed to <c>ProcessStartInfo.ArgumentList</c>, never to a
    /// shell, so the builder must not add quoting of its own. Quoting here
    /// would be doubly wrong: harmless characters would end up inside the
    /// filename ffmpeg actually opens.
    /// </summary>
    [Theory]
    [InlineData("/media/Amélie (2001)/Amélie.mkv")]
    [InlineData("/media/Movies/A Film; rm -rf ~/x.mkv")]
    [InlineData("/media/Movies/It's \"Quoted\".mkv")]
    [InlineData("/media/Movies/$(id).mkv")]
    [InlineData("/media/Movies/`id`.mkv")]
    [InlineData("/media/Movies/a&b|c>d<e.mkv")]
    [InlineData("/media/日本語/字幕テスト.mkv")]
    [InlineData("/media/Movies/emoji \U0001F3AC.mkv")]
    [InlineData("C:\\Media\\Movies\\Arrival (2016)\\Arrival.mkv")]
    [InlineData("\\\\nas\\media\\Arrival.mkv")]
    [InlineData("/media/Movies/percent %s %d.mkv")]
    [InlineData("/media/Movies/newline\nsecond.mkv")]
    [InlineData("/media/Movies/tab\there.mkv")]
    public void PathsArePassedThroughVerbatimWithNoShellEscaping(string path)
    {
        var args = FfmpegArguments.BuildPcmExtraction(Request(path));

        Assert.Equal("file:" + path, ArgumentAfter(args, "-i"));
    }

    /// <summary>
    /// The strongest single guarantee available: no path content can add,
    /// remove or split a token. If this holds, nothing in a filename can become
    /// a separate ffmpeg argument.
    /// </summary>
    [Theory]
    [InlineData("/media/x.mkv")]
    [InlineData("-f concat -safe 0 -i /etc/passwd")]
    [InlineData("x.mkv\" -f wav \"y")]
    [InlineData("x.mkv' -f wav 'y")]
    [InlineData("x.mkv -y /etc/cron.d/pwn")]
    [InlineData("concat:a|b")]
    [InlineData("  -i  /etc/shadow  ")]
    public void NoPathContentCanAlterTheArgumentCount(string path)
    {
        var baseline = FfmpegArguments.BuildPcmExtraction(Request("/media/x.mkv")).Count;

        Assert.Equal(baseline, FfmpegArguments.BuildPcmExtraction(Request(path)).Count);
    }

    /// <summary>
    /// The path appears exactly once, immediately after <c>-i</c>. A second
    /// occurrence would mean it had been spliced somewhere it is not being
    /// treated as a filename.
    /// </summary>
    [Fact]
    public void ThePathAppearsOnlyOnceAndOnlyAsTheInputUrl()
    {
        const string Path = "/media/Movies/Arrival.mkv";
        var args = FfmpegArguments.BuildPcmExtraction(Request(Path));

        Assert.Single(args, a => a.Contains(Path, StringComparison.Ordinal));
        Assert.Equal("file:" + Path, ArgumentAfter(args, "-i"));
    }

    // ------------------------------------------------------------------
    // Rejected input
    // ------------------------------------------------------------------

    /// <summary>
    /// An empty or blank path would make ffmpeg fall back to reading stdin or
    /// fail with an opaque error a long way from the cause.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void RejectsAnEmptyOrBlankPath(string path)
    {
        Assert.Throws<ArgumentException>(() =>
            FfmpegArguments.BuildPcmExtraction(Request(path)));
    }

    /// <summary>
    /// .NET strings can hold an embedded NUL; the native argv they are marshalled
    /// into cannot. Left through, everything after the NUL is silently dropped,
    /// so <c>/media/x.mkv\0anything</c> and the truncation it produces stop
    /// agreeing about which file is open.
    /// </summary>
    [Fact]
    public void RejectsAPathContainingAnEmbeddedNul()
    {
        Assert.Throws<ArgumentException>(() =>
            FfmpegArguments.BuildPcmExtraction(Request("/media/x.mkv\0-f wav")));
    }

    /// <summary>
    /// Guards <c>CA1062</c>-style caller error at the one public entry point.
    /// </summary>
    [Fact]
    public void RejectsANullRequest()
    {
        Assert.Throws<ArgumentNullException>(() => FfmpegArguments.BuildPcmExtraction(null!));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static FfmpegPcmRequest Request(string path) => new() { InputPath = path };

    private static string ArgumentAfter(IReadOnlyList<string> args, string flag)
    {
        var i = args.IndexOf(flag);
        Assert.True(i >= 0, $"expected {flag} in [{string.Join(' ', args)}]");
        Assert.True(i + 1 < args.Count, $"{flag} has no value");
        return args[i + 1];
    }

    private static void AssertAdjacentPair(IReadOnlyList<string> args, string flag, string value)
        => Assert.Equal(value, ArgumentAfter(args, flag));
}

/// <summary>
/// <see cref="IReadOnlyList{T}"/> has no <c>IndexOf</c>, and the assertions above
/// read far worse without one.
/// </summary>
internal static class ReadOnlyListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
