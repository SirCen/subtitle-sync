using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleSync.MediaEncoding;

/// <summary>
/// Runs the server's ffmpeg and streams its raw PCM output to a destination
/// stream as it is produced.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="FfmpegArguments"/>, which decides what to run;
/// this decides how. It honours the three requirements that class's doc sets
/// out: the binary comes from <see cref="IMediaEncoder.EncoderPath"/>, arguments
/// go in one at a time through <see cref="ProcessStartInfo.ArgumentList"/>, and
/// both pipes are drained concurrently before exit is awaited (see
/// <see cref="ProcessOutputPump"/> for why that last one is not optional).
/// </para>
/// <para>
/// <b>Nothing is buffered.</b> An hour of 16 kHz mono s16le is roughly 115 MB
/// and a long film is several hundred; the payload goes straight from the pipe
/// to the destination in 64 KB hops and is never held whole.
/// </para>
/// <para>
/// <b>Cancellation kills the process, always.</b> A browser tab closed
/// mid-extraction must not leave an ffmpeg behind - do that a few times and the
/// server is unusable. Both routes to a teardown end in
/// <see cref="Process.Kill(bool)"/> with the whole tree: an explicitly cancelled
/// token, and a destination write that throws because the HTTP client
/// disconnected.
/// </para>
/// </remarks>
public sealed class FfmpegPcmStreamer
{
    private static readonly Action<ILogger, string, int, Exception?> _logStarting =
        LoggerMessage.Define<string, int>(
            LogLevel.Debug,
            new EventId(1, nameof(StreamAsync)),
            "Starting PCM extraction: {Executable} with {ArgumentCount} arguments");

    private static readonly Action<ILogger, long, long, Exception?> _logCompleted =
        LoggerMessage.Define<long, long>(
            LogLevel.Debug,
            new EventId(2, nameof(StreamAsync)),
            "PCM extraction produced {Bytes} bytes in {ElapsedMs} ms");

    private static readonly Action<ILogger, int, string, Exception?> _logFailed =
        LoggerMessage.Define<int, string>(
            LogLevel.Error,
            new EventId(3, nameof(StreamAsync)),
            "ffmpeg exited with code {ExitCode}. Tail of stderr: {DiagnosticTail}");

    private static readonly Action<ILogger, Exception?> _logKilled =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(4, nameof(StreamAsync)),
            "PCM extraction aborted; ffmpeg process tree killed");

    private static readonly Action<ILogger, Exception?> _logKillFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(5, nameof(StreamAsync)),
            "Failed to kill the ffmpeg process tree after an aborted PCM extraction");

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<FfmpegPcmStreamer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegPcmStreamer"/> class.
    /// </summary>
    /// <param name="mediaEncoder">The server's media encoder, for its ffmpeg path.</param>
    /// <param name="logger">Logger.</param>
    public FfmpegPcmStreamer(IMediaEncoder mediaEncoder, ILogger<FfmpegPcmStreamer> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Extracts <paramref name="request"/> as 16 kHz mono s16le and writes it to
    /// <paramref name="destination"/> as it is decoded.
    /// </summary>
    /// <param name="request">What to extract.</param>
    /// <param name="destination">Where the PCM goes. Written incrementally.</param>
    /// <param name="cancellationToken">Cancels the extraction and kills ffmpeg.</param>
    /// <returns>The number of PCM bytes written.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="FfmpegExecutionException">ffmpeg exited non-zero.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public Task<long> StreamAsync(FfmpegPcmRequest request, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);

        var encoderPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(encoderPath))
        {
            throw new FfmpegExecutionException(
                "The server has no ffmpeg configured; set one under Dashboard > Playback > Transcoding.");
        }

        return RunAsync(
            encoderPath,
            FfmpegArguments.BuildPcmExtraction(request),
            destination,
            _logger,
            cancellationToken);
    }

    /// <summary>
    /// Runs an arbitrary child process, streaming its stdout to
    /// <paramref name="destination"/>.
    /// </summary>
    /// <param name="executablePath">The binary to run.</param>
    /// <param name="arguments">Argument vector; each element becomes one argv slot.</param>
    /// <param name="destination">Where stdout goes.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The number of bytes written to <paramref name="destination"/>.</returns>
    /// <remarks>
    /// Public and process-agnostic so the parts that need a live child - the
    /// concurrent drain and the kill-on-cancel - can be tested without ffmpeg
    /// being installed.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="FfmpegExecutionException">The process exited non-zero.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task<long> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        Stream destination,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(logger);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            ErrorDialog = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Not redirected: the arguments already carry -nostdin, and leaving
            // the child the parent's stdin handle is what -nostdin exists to
            // prevent being consumed.
            RedirectStandardInput = false,
        };

        foreach (var argument in arguments)
        {
            // ArgumentList, never Arguments. FfmpegArguments' safety argument -
            // no quoting, no escaping - holds only while each element stays one
            // argv slot.
            startInfo.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        // Linked, so both the caller's cancellation and a mid-stream pump failure
        // arrive at the same kill.
        using var abortSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _logStarting(logger, executablePath, arguments.Count, null);
        process.Start();

        ProcessOutputPumpResult pumped;
        try
        {
            // Registered after Start so there is a process to kill, and disposed
            // (awaited) before the exit code is read so no kill can still be in
            // flight at that point.
            await using (abortSource.Token.Register(() => Kill(process, logger)).ConfigureAwait(false))
            {
                pumped = await ProcessOutputPump.PumpAsync(
                    process.StandardOutput.BaseStream,
                    process.StandardError.BaseStream,
                    destination,
                    ProcessOutputPump.DefaultBufferSize,
                    ProcessOutputPump.DefaultDiagnosticTailBytes,
                    abortSource).ConfigureAwait(false);
            }
        }
        catch
        {
            // Both pipes are at EOF or broken by now, but the process may not be:
            // a destination that threw leaves ffmpeg happily decoding. Nothing
            // leaves this method with a child still running.
            Kill(process, logger);
            await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
            throw;
        }

        // Safe now: the pipes are drained, so this cannot be the deadlock. It is
        // deliberately not cancellable - the process is already finished or
        // already killed, and we want its exit code either way.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        stopwatch.Stop();

        // A killed process exits non-zero; report the cancellation, not a bogus
        // ffmpeg failure.
        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
        {
            _logFailed(logger, process.ExitCode, pumped.DiagnosticTail, null);
            throw new FfmpegExecutionException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "ffmpeg exited with code {0} after producing {1} bytes.",
                    process.ExitCode,
                    pumped.BytesCopied),
                process.ExitCode,
                pumped.DiagnosticTail,
                pumped.BytesCopied);
        }

        _logCompleted(logger, pumped.BytesCopied, stopwatch.ElapsedMilliseconds, null);

        return pumped.BytesCopied;
    }

    /// <summary>
    /// Kills the process and everything it spawned, tolerating the race where it
    /// exited on its own a moment earlier.
    /// </summary>
    private static void Kill(Process process, ILogger logger)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            _logKilled(logger, null);
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill, or was never started.
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // The OS refused. Worth knowing about: this is the case that leaves a
            // process behind.
            _logKillFailed(logger, ex);
        }
        catch (NotSupportedException ex)
        {
            _logKillFailed(logger, ex);
        }
        catch (AggregateException ex)
        {
            // Kill(entireProcessTree: true) aggregates per-child failures.
            _logKillFailed(logger, ex);
        }
    }

    /// <summary>
    /// Reaps a killed process without letting the wait itself become the failure.
    /// </summary>
    private static async Task WaitForExitQuietlyAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Never started, already reaped, or disposed underneath us. Nothing
            // to wait for in any of those cases. ObjectDisposedException derives
            // from this one, so it is covered too.
        }
    }
}
