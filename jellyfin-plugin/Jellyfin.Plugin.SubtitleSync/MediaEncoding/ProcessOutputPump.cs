using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubtitleSync.MediaEncoding;

/// <summary>
/// Drains a child process's standard output and standard error at the same time,
/// copying standard output to a destination stream as it arrives.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because of a deadlock, and the deadlock is the whole point.</b>
/// A pipe between two processes is a fixed-size OS buffer, typically 64 KB. When
/// it fills, the writer blocks in <c>write()</c> until somebody reads. ffmpeg
/// writes PCM to stdout and progress plus warnings to stderr, so a reader that
/// consumes one pipe to completion before touching the other stops the process
/// dead as soon as the pipe it is ignoring fills: ffmpeg is blocked writing
/// stderr, we are blocked reading stdout, and neither ever moves again. The
/// window is roughly 64 KB of stderr, which a few seconds of test fixture never
/// reaches and a feature-length film reaches within the first minute. Both pipes
/// are therefore drained concurrently and only then is exit awaited.
/// </para>
/// <para>
/// <b>Why an abort source rather than a token.</b> The two pumps are joined by
/// <see cref="Task.WhenAll(Task[])"/>, which does not return until both finish.
/// If the destination write fails - the HTTP client disconnected mid-transfer,
/// which is the normal way this ends - the stderr pump is still waiting on a
/// process that is very much alive, and the failure would not surface until the
/// extraction finished on its own. Cancelling
/// <see cref="CancellationTokenSource"/> on any fault lets the owner's
/// registered kill run immediately, which closes the pipes and unblocks the
/// other pump.
/// </para>
/// <para>
/// Streams only, no <see cref="System.Diagnostics.Process"/>: everything here is
/// exercised by unit tests over fake streams, including the deadlock, which a
/// real short-lived ffmpeg run cannot reproduce.
/// </para>
/// </remarks>
public static class ProcessOutputPump
{
    /// <summary>
    /// Default copy buffer size, in bytes. One pipe buffer's worth.
    /// </summary>
    public const int DefaultBufferSize = 64 * 1024;

    /// <summary>
    /// Default number of standard error bytes retained for diagnostics.
    /// </summary>
    public const int DefaultDiagnosticTailBytes = 8 * 1024;

    /// <summary>
    /// Copies <paramref name="standardOutput"/> into <paramref name="destination"/>
    /// while concurrently draining and tailing <paramref name="standardError"/>.
    /// </summary>
    /// <param name="standardOutput">The child's stdout pipe. Carries the payload.</param>
    /// <param name="standardError">The child's stderr pipe. Drained and discarded but for the tail.</param>
    /// <param name="destination">Where the payload goes. Written to incrementally, never buffered whole.</param>
    /// <param name="bufferSize">Copy buffer size in bytes; see <see cref="DefaultBufferSize"/>.</param>
    /// <param name="diagnosticTailBytes">Standard error bytes to retain; see <see cref="DefaultDiagnosticTailBytes"/>.</param>
    /// <param name="abortSource">
    /// Cancellation for both pumps, and the signal back to the owner. Cancelled
    /// here if either pump fails, so that the owner's cancellation registration -
    /// which is what kills the process - runs without waiting for the other pump.
    /// </param>
    /// <returns>The bytes copied and the standard error tail.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive.</exception>
    public static async Task<ProcessOutputPumpResult> PumpAsync(
        Stream standardOutput,
        Stream standardError,
        Stream destination,
        int bufferSize,
        int diagnosticTailBytes,
        CancellationTokenSource abortSource)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(abortSource);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(diagnosticTailBytes);

        // Both started before either is awaited. Ordering the two statements is
        // not enough on its own - the await below is what actually interleaves
        // them - but neither task blocks synchronously, so both are in flight.
        var payloadTask = CopyPayloadAsync(standardOutput, destination, bufferSize, abortSource);
        var diagnosticTask = CaptureTailAsync(standardError, diagnosticTailBytes, abortSource);

        await Task.WhenAll(payloadTask, diagnosticTask).ConfigureAwait(false);

        return new ProcessOutputPumpResult
        {
            BytesCopied = await payloadTask.ConfigureAwait(false),
            DiagnosticTail = await diagnosticTask.ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Copies stdout to the destination, cancelling <paramref name="abortSource"/>
    /// on any failure so the sibling pump is not left waiting on a live process.
    /// </summary>
    private static async Task<long> CopyPayloadAsync(
        Stream source,
        Stream destination,
        int bufferSize,
        CancellationTokenSource abortSource)
    {
        var cancellationToken = abortSource.Token;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        long total = 0;

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                // Push each chunk out rather than accumulating: the consumer is a
                // browser decoding this incrementally, and its progress display is
                // the arrival of bytes.
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

                total += read;
            }

            return total;
        }
        catch
        {
            // Rethrown, so this is not a swallowed general catch. The cancel is
            // the point: it is what lets the owner kill the process.
            CancelQuietly(abortSource);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Drains stderr to its end, keeping only the last
    /// <paramref name="tailBytes"/> bytes.
    /// </summary>
    /// <remarks>
    /// Never throws. A failure to read diagnostics is not itself a failure worth
    /// reporting - the payload copy and the process exit code decide that - and
    /// throwing here would mask the real error in
    /// <see cref="Task.WhenAll(Task[])"/>. Draining to the end is mandatory even
    /// when nobody wants the content: that is the deadlock this class exists to
    /// avoid.
    /// </remarks>
    private static async Task<string> CaptureTailAsync(
        Stream source,
        int tailBytes,
        CancellationTokenSource abortSource)
    {
        var cancellationToken = abortSource.Token;
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        var tail = new byte[tailBytes];
        var tailLength = 0;

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, 4096), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                tailLength = AppendToTail(tail, tailLength, buffer.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException)
        {
            // The owner is tearing the process down; whatever was captured before
            // that is still the most useful thing we have.
        }
        catch (IOException)
        {
            // The pipe went away with the process. Same reasoning.
        }
        catch (ObjectDisposedException)
        {
            // Ditto: the Process was disposed underneath us.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Encoding.UTF8.GetString(tail, 0, tailLength);
    }

    /// <summary>
    /// Appends <paramref name="chunk"/> to a fixed-size tail buffer, discarding
    /// whatever no longer fits from the front.
    /// </summary>
    /// <param name="tail">The tail buffer.</param>
    /// <param name="tailLength">How much of it is currently used.</param>
    /// <param name="chunk">The bytes to append.</param>
    /// <returns>The new used length.</returns>
    private static int AppendToTail(byte[] tail, int tailLength, ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length >= tail.Length)
        {
            chunk[^tail.Length..].CopyTo(tail);
            return tail.Length;
        }

        var overflow = tailLength + chunk.Length - tail.Length;
        if (overflow > 0)
        {
            // Slide the retained bytes down. A ring buffer would avoid the copy,
            // but this runs once per 4 KB of stderr against an 8 KB buffer.
            tail.AsSpan(overflow, tailLength - overflow).CopyTo(tail);
            tailLength -= overflow;
        }

        chunk.CopyTo(tail.AsSpan(tailLength));
        return tailLength + chunk.Length;
    }

    /// <summary>
    /// Cancels without letting a disposed source, or a callback that threw, take
    /// the place of the failure being reported.
    /// </summary>
    private static void CancelQuietly(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down; nothing left to signal.
        }
        catch (AggregateException)
        {
            // A registered callback threw. The original fault is the one to
            // surface, and it is about to be rethrown by the caller.
        }
    }
}
