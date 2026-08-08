using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleSync.MediaEncoding;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests.MediaEncoding;

/// <summary>
/// Tests for <see cref="ProcessOutputPump"/>.
/// </summary>
/// <remarks>
/// <para>
/// The reason this class exists is <see cref="StdoutBlockedOnStderrBeingDrained_StillCompletes"/>.
/// The bug it guards against - draining ffmpeg's stdout to completion before
/// touching stderr, which wedges both processes once the 64 KB stderr pipe
/// buffer fills - cannot be reproduced by running the real thing on a short
/// fixture, and always reproduces on a feature-length film. Fake streams model
/// the pipe backpressure directly, so the failure is a hang here rather than in
/// production.
/// </para>
/// <para>
/// Every test that could hang on a regression is bounded by
/// <see cref="TestTimeout"/> so a broken implementation fails in seconds instead
/// of blocking the suite.
/// </para>
/// </remarks>
public class ProcessOutputPumpTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The deadlock, modelled. Standard output refuses to produce its second half
    /// until standard error has been read past the point a real pipe buffer would
    /// have filled - which is exactly the constraint the OS imposes on a process
    /// writing to two pipes. An implementation that copies stdout to EOF before
    /// reading stderr never gets there and hangs.
    /// </summary>
    [Fact]
    public async Task StdoutBlockedOnStderrBeingDrained_StillCompletes()
    {
        const int PipeBuffer = 64 * 1024;

        var stderrDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var payload = RandomBytes(PipeBuffer * 4);
        var stdout = new GatedStream(payload, releaseAfter: PipeBuffer, gate: stderrDrained.Task);

        // Four pipe buffers' worth of progress blocks, which is roughly what an
        // hour of `-progress pipe:2` produces.
        var diagnostics = Encoding.UTF8.GetBytes(new string('e', PipeBuffer * 4));
        var stderr = new CountingStream(diagnostics, onBytesRead: read =>
        {
            if (read >= PipeBuffer)
            {
                stderrDrained.TrySetResult();
            }
        });

        var destination = new MemoryStream();
        using var abortSource = new CancellationTokenSource();

        var result = await ProcessOutputPump.PumpAsync(
                stdout,
                stderr,
                destination,
                ProcessOutputPump.DefaultBufferSize,
                ProcessOutputPump.DefaultDiagnosticTailBytes,
                abortSource)
            .WaitAsync(TestTimeout);

        Assert.Equal(payload.Length, result.BytesCopied);
        Assert.Equal(payload, destination.ToArray());
    }

    /// <summary>
    /// The payload is copied byte for byte, including across read boundaries that
    /// do not align with the copy buffer.
    /// </summary>
    [Fact]
    public async Task Payload_IsCopiedVerbatim()
    {
        var payload = RandomBytes(197_003);
        var destination = new MemoryStream();
        using var abortSource = new CancellationTokenSource();

        var result = await ProcessOutputPump.PumpAsync(
                new GatedStream(payload, releaseAfter: payload.Length, gate: Task.CompletedTask),
                new CountingStream(Array.Empty<byte>(), _ => { }),
                destination,
                4096,
                ProcessOutputPump.DefaultDiagnosticTailBytes,
                abortSource)
            .WaitAsync(TestTimeout);

        Assert.Equal(payload.Length, result.BytesCopied);
        Assert.Equal(payload, destination.ToArray());
    }

    /// <summary>
    /// Standard error is bounded: a long extraction's megabytes of progress
    /// blocks must not accumulate in memory, and the end - where the error
    /// message is - is the part kept.
    /// </summary>
    [Fact]
    public async Task Stderr_KeepsOnlyTheTail()
    {
        var noise = new string('a', 20_000);
        var diagnostics = Encoding.UTF8.GetBytes(noise + "the actual error");
        var destination = new MemoryStream();
        using var abortSource = new CancellationTokenSource();

        var result = await ProcessOutputPump.PumpAsync(
                new GatedStream(Array.Empty<byte>(), 0, Task.CompletedTask),
                new CountingStream(diagnostics, _ => { }),
                destination,
                ProcessOutputPump.DefaultBufferSize,
                1024,
                abortSource)
            .WaitAsync(TestTimeout);

        Assert.Equal(1024, result.DiagnosticTail.Length);
        Assert.EndsWith("the actual error", result.DiagnosticTail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Standard error shorter than the tail budget survives whole.
    /// </summary>
    [Fact]
    public async Task Stderr_ShorterThanTheTail_IsKeptWhole()
    {
        var destination = new MemoryStream();
        using var abortSource = new CancellationTokenSource();

        var result = await ProcessOutputPump.PumpAsync(
                new GatedStream(Array.Empty<byte>(), 0, Task.CompletedTask),
                new CountingStream(Encoding.UTF8.GetBytes("Invalid data found"), _ => { }),
                destination,
                ProcessOutputPump.DefaultBufferSize,
                1024,
                abortSource)
            .WaitAsync(TestTimeout);

        Assert.Equal("Invalid data found", result.DiagnosticTail);
    }

    /// <summary>
    /// The client-disconnect case, and the reason the pump owns the abort source.
    /// The destination throwing mid-body must not leave the pump waiting for a
    /// stderr stream that only ends when the process does: it cancels, which is
    /// what triggers the owner's kill.
    /// </summary>
    [Fact]
    public async Task DestinationFailure_CancelsTheAbortSourceRatherThanHanging()
    {
        var stdout = new GatedStream(RandomBytes(1_000_000), releaseAfter: 1_000_000, gate: Task.CompletedTask);

        // Never ends on its own, exactly like the stderr of a live ffmpeg.
        var stderr = new NeverEndingStream();

        using var abortSource = new CancellationTokenSource();

        var pump = ProcessOutputPump.PumpAsync(
            stdout,
            stderr,
            new ThrowingStream(),
            ProcessOutputPump.DefaultBufferSize,
            ProcessOutputPump.DefaultDiagnosticTailBytes,
            abortSource);

        await Assert.ThrowsAsync<IOException>(() => pump.WaitAsync(TestTimeout));
        Assert.True(abortSource.IsCancellationRequested);
    }

    /// <summary>
    /// A cancelled abort source stops both pumps rather than running to the end
    /// of the payload.
    /// </summary>
    [Fact]
    public async Task Cancellation_StopsBothPumps()
    {
        using var abortSource = new CancellationTokenSource();
        var pump = ProcessOutputPump.PumpAsync(
            new NeverEndingStream(),
            new NeverEndingStream(),
            new MemoryStream(),
            ProcessOutputPump.DefaultBufferSize,
            ProcessOutputPump.DefaultDiagnosticTailBytes,
            abortSource);

        await abortSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.WaitAsync(TestTimeout));
    }

    /// <summary>
    /// Null arguments are rejected rather than producing a partial stream.
    /// </summary>
    [Fact]
    public async Task NullArguments_AreRejected()
    {
        using var abortSource = new CancellationTokenSource();
        var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentNullException>(() => ProcessOutputPump.PumpAsync(
            null!, stream, stream, 1024, 1024, abortSource));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ProcessOutputPump.PumpAsync(
            stream, null!, stream, 1024, 1024, abortSource));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ProcessOutputPump.PumpAsync(
            stream, stream, null!, 1024, 1024, abortSource));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ProcessOutputPump.PumpAsync(
            stream, stream, stream, 1024, 1024, null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ProcessOutputPump.PumpAsync(
            stream, stream, stream, 0, 1024, abortSource));
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        new Random(20260807).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// A readable stream that models pipe backpressure: it hands over
    /// <c>releaseAfter</c> bytes freely and then waits on <c>gate</c> before
    /// producing any more.
    /// </summary>
    private sealed class GatedStream : Stream
    {
        private readonly byte[] _content;
        private readonly int _releaseAfter;
        private readonly Task _gate;
        private int _position;

        public GatedStream(byte[] content, int releaseAfter, Task gate)
        {
            _content = content;
            _releaseAfter = releaseAfter;
            _gate = gate;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _content.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _releaseAfter)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var remaining = _content.Length - _position;
            if (remaining == 0)
            {
                return 0;
            }

            var take = Math.Min(buffer.Length, Math.Min(remaining, 8192));
            _content.AsMemory(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A readable stream that reports its cumulative progress, so a test can make
    /// one stream's advance depend on the other's.
    /// </summary>
    private sealed class CountingStream : Stream
    {
        private readonly byte[] _content;
        private readonly Action<int> _onBytesRead;
        private int _position;

        public CountingStream(byte[] content, Action<int> onBytesRead)
        {
            _content = content;
            _onBytesRead = onBytesRead;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _content.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = _content.Length - _position;
            if (remaining == 0)
            {
                return ValueTask.FromResult(0);
            }

            var take = Math.Min(buffer.Length, Math.Min(remaining, 4096));
            _content.AsMemory(_position, take).CopyTo(buffer);
            _position += take;
            _onBytesRead(_position);
            return ValueTask.FromResult(take);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A stream that never reaches EOF and only stops when cancelled, standing in
    /// for the pipes of a process that is still running.
    /// </summary>
    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            buffer.Span[0] = (byte)'x';
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A destination that fails on first write, standing in for an HTTP response
    /// body whose client has gone away.
    /// </summary>
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("The client reset the connection."));

        public override void Write(byte[] buffer, int offset, int count)
            => throw new IOException("The client reset the connection.");

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
