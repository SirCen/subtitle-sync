using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.SubtitleSync.MediaEncoding;

/// <summary>
/// Builds the ffmpeg argument vector that turns a library media file into the
/// raw PCM the sync algorithm consumes.
/// </summary>
/// <remarks>
/// <para>
/// Pure by design. Nothing here launches a process, touches the filesystem or
/// reads configuration, so the whole ffmpeg contract - which is otherwise only
/// observable by decoding an hour of audio - is covered by fast unit tests.
/// </para>
/// <para>
/// <b>Output contract.</b> 16 kHz, mono, signed 16-bit little-endian PCM,
/// headerless from byte zero. No RIFF, no container. This is fixed on three
/// sides: <c>reference/sync_srt.py</c> (<c>SR = 16000</c>) is the oracle the
/// golden parity test compares against, the WebRTC VAD only accepts 16 kHz
/// mono, and <c>jellyfin-plugin/web/src/pcmStream.ts</c> starts decoding
/// samples at offset zero. Changing any of it silently corrupts every sync
/// rather than failing.
/// </para>
/// <para>
/// <b>How the caller must run this (issue #6).</b> Feed the vector to
/// <c>ProcessStartInfo.ArgumentList</c> one element at a time, never to
/// <c>ProcessStartInfo.Arguments</c> and never through a shell - the safety
/// argument below rests entirely on each element staying one argv slot. Take
/// the binary from <c>IMediaEncoder.EncoderPath</c> rather than hardcoding
/// <c>ffmpeg</c>, so the server's configured build is used.
/// </para>
/// <para>
/// <b>Deadlock hazard, do not lose this.</b> The runner must drain
/// <c>StandardOutput</c> and <c>StandardError</c> <i>concurrently</i>, and only
/// then await exit. ffmpeg writes the PCM to stdout and progress plus warnings
/// to stderr. Reading one to completion before touching the other blocks ffmpeg
/// forever as soon as the unread pipe's OS buffer fills, typically at 64 KB. A
/// few seconds of test fixture never fills it, so this passes locally and hangs
/// on the first feature-length film. Start both drains, then
/// <c>await Task.WhenAll(...)</c>, then <c>WaitForExitAsync</c>.
/// </para>
/// </remarks>
public static class FfmpegArguments
{
    /// <summary>
    /// Sample rate of the emitted PCM, in hertz. Matches <c>SR</c> in
    /// <c>reference/sync_srt.py</c> and the only rate the VAD accepts.
    /// </summary>
    public const int SampleRate = 16000;

    /// <summary>Channel count of the emitted PCM. Mono.</summary>
    public const int Channels = 1;

    /// <summary>Bytes per sample of the emitted PCM. Signed 16-bit.</summary>
    public const int BytesPerSample = 2;

    /// <summary>
    /// The ffmpeg muxer name for the emitted format: raw signed 16-bit
    /// little-endian samples with no container or header of any kind.
    /// </summary>
    public const string SampleFormat = "s16le";

    /// <summary>
    /// The protocol prefix forced onto every input path.
    /// </summary>
    /// <remarks>
    /// Load-bearing. See <see cref="BuildPcmExtraction"/>.
    /// </remarks>
    private const string FileProtocolPrefix = "file:";

    /// <summary>
    /// Builds the complete argument vector, excluding the executable itself.
    /// </summary>
    /// <param name="request">What to extract.</param>
    /// <returns>
    /// The arguments in order, each one a single argv element to be appended to
    /// <c>ProcessStartInfo.ArgumentList</c> unmodified.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The input path is blank, or contains a NUL that native argv marshalling
    /// would truncate at.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The requested audio stream index is negative.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Why the order is what it is.</b> ffmpeg's command line is positional:
    /// an option binds to the next file named after it, so the same flag means
    /// different things either side of <c>-i</c>. The vector is therefore three
    /// blocks - global options, then input options and the input, then output
    /// options and the output URL - and the blocks cannot be reordered.
    /// </para>
    /// <para>
    /// <b>Why the input path is prefixed with <c>file:</c>.</b> ffmpeg resolves
    /// a leading <c>scheme:</c> on an input URL to a protocol handler, so an
    /// unprefixed path is not a filename, it is a URL that usually happens to
    /// look like one. A library entry named <c>concat:/etc/passwd|/etc/shadow</c>
    /// reads two arbitrary files; <c>http://...</c> or <c>tcp://...</c> makes the
    /// server issue an outbound request on the attacker's behalf; <c>pipe:0</c>
    /// reads the plugin's own stdin. Prefixing pins the local-file handler and
    /// demotes everything after it to a literal filename, and it incidentally
    /// moves a leading <c>-</c> out of position zero so no path can present as
    /// an option. This is also what Jellyfin core does in
    /// <c>MediaEncoder.GetInputPathArgument</c>.
    /// </para>
    /// <para>
    /// The prefix is applied unconditionally, never "only if absent". Skipping
    /// an existing prefix would be a one-token bypass: <c>file:concat:...</c>
    /// would pass the check and then be re-parsed with <c>concat:</c> leading.
    /// Double-prefixing is correct - ffmpeg strips one scheme and opens a file
    /// literally named <c>file:concat:...</c>, which does not exist, which is
    /// the desired outcome.
    /// </para>
    /// <para>
    /// <b>Why quoting is absent.</b> Every element is a separate argv slot, so
    /// spaces, quotes, semicolons, backticks, <c>$(...)</c> and non-ASCII in a
    /// filename are inert. Adding quotes here would be actively wrong: they
    /// would become part of the filename ffmpeg tries to open.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> BuildPcmExtraction(FfmpegPcmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var inputPath = request.InputPath;

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException(
                "The input path must not be blank; ffmpeg would fall back to stdin.",
                nameof(request));
        }

        // A .NET string can carry an embedded NUL, native argv cannot. Passed
        // through, the OS truncates at the NUL and the path we validated stops
        // being the path ffmpeg opens.
        if (inputPath.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The input path must not contain a NUL character.",
                nameof(request));
        }

        if (request.AudioStreamIndex is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.AudioStreamIndex,
                "The audio stream index must not be negative.");
        }

        var args = new List<string>(24);

        // --- Global options ------------------------------------------------

        // Keep stderr to progress blocks and real warnings only: it is parsed.
        args.Add("-hide_banner");

        // ffmpeg consumes stdin by default. In a server process that is a hang
        // and a leak of whatever the parent had on that handle.
        args.Add("-nostdin");

        args.Add("-loglevel");
        args.Add("warning");

        // Suppress the interleaved rolling status line, which would otherwise
        // sit in the middle of the progress blocks.
        args.Add("-nostats");

        if (request.ReportProgress)
        {
            // pipe:2 is stderr, and it must be stderr: stdout carries the PCM,
            // so a progress block written there would be decoded as samples.
            args.Add("-progress");
            args.Add("pipe:2");
        }

        // --- Input options, which bind to the input that follows ------------

        // Defence in depth behind the file: prefix. That prefix constrains the
        // URL we hand ffmpeg, but some demuxers (concat, hls, image2) follow
        // references found inside the opened file. The whitelist constrains
        // those nested opens too.
        args.Add("-protocol_whitelist");
        args.Add("file");

        args.Add("-i");
        args.Add(FileProtocolPrefix + inputPath);

        // --- Output options -------------------------------------------------

        if (request.AudioStreamIndex is int index)
        {
            // Absolute container index, matching Jellyfin's MediaStream.Index.
            // Deliberately not "0:a:N", which counts audio streams only and
            // would select the wrong track on any file with video first.
            args.Add("-map");
            args.Add("0:" + index.ToString(CultureInfo.InvariantCulture));
        }

        // Drop everything that is not audio. Without this, a file whose default
        // stream selection picks up an attached cover image makes the s16le
        // muxer fail instead of quietly producing audio.
        args.Add("-vn");
        args.Add("-sn");
        args.Add("-dn");

        args.Add("-ac");
        args.Add(Channels.ToString(CultureInfo.InvariantCulture));
        args.Add("-ar");
        args.Add(SampleRate.ToString(CultureInfo.InvariantCulture));

        // The s16le muxer already implies this codec; stating it means a future
        // format change cannot silently leave the codec behind.
        args.Add("-c:a");
        args.Add("pcm_s16le");

        // s16le, not wav: wav would prepend a 44-byte RIFF header that the
        // client adapter decodes as audio samples.
        args.Add("-f");
        args.Add(SampleFormat);

        // Output URL. "-" is ffmpeg's stdout specifier, equivalent to pipe:1.
        args.Add("-");

        return args;
    }
}
