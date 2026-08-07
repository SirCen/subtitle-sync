using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleSync.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.SubtitleSync.Tests.MediaEncoding;

/// <summary>
/// Integration tests for <see cref="FfmpegPcmStreamer.RunAsync"/> against a real
/// child process.
/// </summary>
/// <remarks>
/// <para>
/// These need a live process, so they use the platform shell rather than ffmpeg:
/// the properties under test - that a cancelled run leaves nothing running, that
/// a non-zero exit is reported with its stderr, that stdout arrives verbatim -
/// are properties of the runner, not of ffmpeg, and requiring ffmpeg on every
/// developer and CI machine would mean these tests simply did not run.
/// </para>
/// <para>
/// The child prints its own process id as its first line, which is what makes
/// "cancellation actually killed it" an observable fact rather than an assumed
/// one.
/// </para>
/// </remarks>
public class FfmpegPcmStreamerProcessTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegPcmStreamerProcessTests"/> class.
    /// </summary>
    /// <param name="output">xunit output, used to report skipped conditions.</param>
    public FfmpegPcmStreamerProcessTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// The orphan test. A cancelled request must leave no process behind: a
    /// server that accumulates abandoned transcodes is unusable, and the damage
    /// is invisible until it is severe.
    /// </summary>
    [Fact]
    public async Task Cancellation_KillsTheProcess()
    {
        var destination = new PidCapturingStream();
        using var cancellation = new CancellationTokenSource();

        var (fileName, arguments) = Shell("echo-pid-then-spew");

        var run = FfmpegPcmStreamer.RunAsync(
            fileName,
            arguments,
            destination,
            NullLogger.Instance,
            cancellation.Token);

        var pid = await destination.PidTask.WaitAsync(TestTimeout);
        Assert.True(IsRunning(pid), "the child should be running before cancellation");

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TestTimeout));

        // The kill is asynchronous at the OS level, so poll rather than assert
        // instantly. A leak fails this by timing out, not by flaking.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (IsRunning(pid) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.False(IsRunning(pid), $"process {pid} survived cancellation");
    }

    /// <summary>
    /// A destination that fails mid-stream - an HTTP client that disconnected -
    /// must kill the process too, not just stop reading it.
    /// </summary>
    [Fact]
    public async Task DestinationFailure_KillsTheProcess()
    {
        var destination = new PidCapturingStream { FailAfterBytes = 64 };

        var (fileName, arguments) = Shell("echo-pid-then-spew");

        var run = FfmpegPcmStreamer.RunAsync(
            fileName,
            arguments,
            destination,
            NullLogger.Instance,
            CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TestTimeout));

        var pid = await destination.PidTask.WaitAsync(TestTimeout);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (IsRunning(pid) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.False(IsRunning(pid), $"process {pid} survived a failed destination write");
    }

    /// <summary>
    /// A non-zero exit is a failure, and the stderr tail travels with it: without
    /// that, "ffmpeg failed" is all an administrator would ever see.
    /// </summary>
    [Fact]
    public async Task NonZeroExit_ThrowsWithTheStderrTail()
    {
        var (fileName, arguments) = Shell("fail-with-stderr");

        var exception = await Assert.ThrowsAsync<FfmpegExecutionException>(
            () => FfmpegPcmStreamer.RunAsync(
                fileName,
                arguments,
                new MemoryStream(),
                NullLogger.Instance,
                CancellationToken.None).WaitAsync(TestTimeout));

        Assert.Equal(3, exception.ExitCode);
        Assert.Contains("boom", exception.DiagnosticTail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Standard output reaches the destination unmodified, and nothing from
    /// standard error contaminates it. On this endpoint that is the whole
    /// contract: a single stray byte shifts every sample by half a sample.
    /// </summary>
    [Fact]
    public async Task Stdout_ReachesTheDestinationUncontaminated()
    {
        var (fileName, arguments) = Shell("payload-and-noise");
        var destination = new MemoryStream();

        var written = await FfmpegPcmStreamer.RunAsync(
                fileName,
                arguments,
                destination,
                NullLogger.Instance,
                CancellationToken.None)
            .WaitAsync(TestTimeout);

        var text = System.Text.Encoding.UTF8.GetString(destination.ToArray());
        Assert.StartsWith("PAYLOAD", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NOISE", text, StringComparison.Ordinal);
        Assert.Equal(destination.Length, written);
    }

    /// <summary>
    /// A large payload streams through without being buffered whole, and a
    /// simultaneously noisy stderr does not wedge it. This is the real-process
    /// counterpart to the modelled deadlock in
    /// <see cref="ProcessOutputPumpTests"/>: a genuine OS pipe, genuinely more
    /// than one buffer's worth on both pipes.
    /// </summary>
    [Fact]
    public async Task LargeOutputOnBothPipes_DoesNotDeadlock()
    {
        var (fileName, arguments) = Shell("both-pipes-loud");
        var destination = new CountingSinkStream();

        var written = await FfmpegPcmStreamer.RunAsync(
                fileName,
                arguments,
                destination,
                NullLogger.Instance,
                CancellationToken.None)
            .WaitAsync(TestTimeout);

        // Both pipes carry well over the ~64 KB buffer that causes the deadlock.
        Assert.True(written > 256 * 1024, $"expected a large payload, got {written} bytes");
    }

    /// <summary>
    /// If the machine happens to have an ffmpeg, prove the end-to-end shape of
    /// the output against it: exactly 16 kHz mono s16le, no header.
    /// </summary>
    /// <remarks>
    /// Best-effort by design. It is a no-op where ffmpeg is absent, and says so
    /// in the test output, because requiring it would make the suite
    /// unrunnable on a machine that only builds the plugin. The authoritative
    /// end-to-end check is against the live Jellyfin container, which always has
    /// one.
    /// </remarks>
    [Fact]
    public async Task RealFfmpeg_ProducesHeaderlessPcmOfTheExpectedLength()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            _output.WriteLine("No ffmpeg on PATH or FFMPEG_PATH; skipping.");
            return;
        }

        // Two seconds of a 440 Hz tone, produced without touching the filesystem.
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "warning", "-nostats",
            "-progress", "pipe:2",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "s16le", "-",
        };

        var destination = new MemoryStream();
        var written = await FfmpegPcmStreamer.RunAsync(
                ffmpeg,
                arguments,
                destination,
                NullLogger.Instance,
                CancellationToken.None)
            .WaitAsync(TestTimeout);

        // 2 s * 16000 Hz * 2 bytes. The lavfi source is sample-exact, unlike a
        // container whose reported runtime is only approximately its audio.
        Assert.Equal(64000, written);

        // Headerless: a RIFF header would put "RIFF" here.
        var head = destination.ToArray().AsSpan(0, 4).ToArray();
        Assert.NotEqual("RIFF"u8.ToArray(), head);
    }

    /// <summary>
    /// Builds a platform shell invocation for a named scenario.
    /// </summary>
    private static (string FileName, IReadOnlyList<string> Arguments) Shell(string scenario)
    {
        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        if (windows)
        {
            var script = scenario switch
            {
                "echo-pid-then-spew" =>
                    "[Console]::Out.WriteLine($PID); [Console]::Out.Flush(); while($true){ [Console]::Out.Write('x' * 1024) }",
                "fail-with-stderr" =>
                    "[Console]::Error.WriteLine('boom: Invalid data found'); exit 3",
                "payload-and-noise" =>
                    "[Console]::Error.WriteLine('NOISE'); [Console]::Out.Write('PAYLOAD')",
                "both-pipes-loud" =>
                    "$c = 'y' * 1024; for($i=0; $i -lt 512; $i++){ [Console]::Out.Write($c); [Console]::Error.Write($c) }",
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };

            return ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", script });
        }

        var shellScript = scenario switch
        {
            "echo-pid-then-spew" =>
                "echo $$; while :; do printf 'xxxxxxxxxxxxxxxx'; done",
            "fail-with-stderr" =>
                "echo 'boom: Invalid data found' >&2; exit 3",
            "payload-and-noise" =>
                "echo NOISE >&2; printf PAYLOAD",
            "both-pipes-loud" =>
                "i=0; while [ $i -lt 512 ]; do dd if=/dev/zero bs=1024 count=1 2>/dev/null; printf '%01024d' 0 >&2; i=$((i+1)); done",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        return ("/bin/sh", new[] { "-c", shellScript });
    }

    private static string? FindFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry. Skip it.
            }
        }

        return null;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// A destination that reads the child's process id off the front of the
    /// stream, and can be told to fail partway through like a disconnected HTTP
    /// client.
    /// </summary>
    private sealed class PidCapturingStream : Stream
    {
        private readonly TaskCompletionSource<int> _pid = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Text.StringBuilder _firstLine = new();
        private long _written;
        private bool _lineComplete;

        public Task<int> PidTask => _pid.Task;

        public int FailAfterBytes { get; init; } = int.MaxValue;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _written;

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Capture(buffer.Span);
            _written += buffer.Length;

            if (_written > FailAfterBytes)
            {
                return ValueTask.FromException(new IOException("The client reset the connection."));
            }

            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private void Capture(ReadOnlySpan<byte> chunk)
        {
            if (_lineComplete)
            {
                return;
            }

            foreach (var b in chunk)
            {
                if (b == (byte)'\n')
                {
                    _lineComplete = true;
                    if (int.TryParse(_firstLine.ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                    {
                        _pid.TrySetResult(pid);
                    }
                    else
                    {
                        _pid.TrySetException(new InvalidOperationException(
                            $"expected a pid on the first line, got '{_firstLine}'"));
                    }

                    return;
                }

                if (b != (byte)'\r')
                {
                    _firstLine.Append((char)b);
                }
            }
        }
    }

    /// <summary>
    /// A destination that counts and discards, so a multi-megabyte payload does
    /// not have to be held in memory to be measured.
    /// </summary>
    private sealed class CountingSinkStream : Stream
    {
        private long _written;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _written;

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _written += buffer.Length;
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => _written += count;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
