using System;

namespace Jellyfin.Plugin.SubtitleSync.MediaEncoding;

/// <summary>
/// Thrown when the server's ffmpeg ran but did not succeed.
/// </summary>
/// <remarks>
/// Distinct from cancellation, which is normal, and from a failure to start the
/// process at all, which surfaces as the <see cref="System.ComponentModel.Win32Exception"/>
/// the runtime raises.
/// </remarks>
public class FfmpegExecutionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegExecutionException"/> class.
    /// </summary>
    public FfmpegExecutionException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegExecutionException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public FfmpegExecutionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegExecutionException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying failure.</param>
    public FfmpegExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegExecutionException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="exitCode">The process exit code.</param>
    /// <param name="diagnosticTail">The tail of the process's standard error.</param>
    /// <param name="bytesWritten">How much payload had already been emitted.</param>
    public FfmpegExecutionException(string message, int exitCode, string diagnosticTail, long bytesWritten)
        : base(message)
    {
        ExitCode = exitCode;
        DiagnosticTail = diagnosticTail;
        BytesWritten = bytesWritten;
    }

    /// <summary>
    /// Gets the process exit code, or null when the failure was not an exit code.
    /// </summary>
    public int? ExitCode { get; }

    /// <summary>
    /// Gets the tail of the process's standard error, which is where ffmpeg says
    /// what actually went wrong.
    /// </summary>
    public string? DiagnosticTail { get; }

    /// <summary>
    /// Gets the number of payload bytes already written to the destination.
    /// </summary>
    /// <remarks>
    /// Non-zero means the HTTP response has already begun and the body is
    /// truncated; the caller can no longer turn this into an error status.
    /// </remarks>
    public long BytesWritten { get; }
}
