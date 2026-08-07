using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleSync.Paths;

namespace Jellyfin.Plugin.SubtitleSync.Writing;

/// <summary>
/// Puts a synced subtitle on disk, or explains why it could not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is ever written in place.</b> The bytes go to a uniquely named
/// temporary file in the destination folder, are flushed through to the storage
/// device, and only then become visible under their real name with a single
/// rename. A crash, a full disk or a killed process therefore leaves either the
/// old file untouched or a stray <c>.tmp</c> - never a partially written
/// subtitle where a valid one was, and never a partially written new one. The
/// single exception is documented three paragraphs down, and it is an empty
/// file, not a truncated one.
/// </para>
/// <para>
/// <b>Concurrent saves of the same item cannot interleave.</b> Each request
/// writes its own temporary file, so two writers never share a handle, and a
/// name is claimed with <see cref="FileMode.CreateNew"/> - <c>O_CREAT|O_EXCL</c>,
/// the one primitive that exactly one of two racing callers can win - before any
/// content exists. The loser is not told to give up; it resolves again and takes
/// the next collision suffix.
/// </para>
/// <para>
/// <b>The claim is not <c>File.Move(..., overwrite: false)</c>, on purpose.</b>
/// That looks like the obvious answer and is wrong on Linux, which is the only
/// platform this ships on: .NET implements it there as an existence check
/// followed by a plain <c>rename</c>, so two callers can both pass the check and
/// the second silently destroys the first's file. Verified against a real
/// Jellyfin 10.11.11 container, where sixteen simultaneous saves reported sixteen
/// distinct paths and left fifteen files. Windows does not have the flaw, so a
/// test suite running there would never have caught it.
/// </para>
/// <para>
/// <b>The one crash artifact is an empty file.</b> Between the claim and the
/// rename the destination exists at zero length. A process killed in that window
/// leaves an empty <c>.srt</c> which the library may index as a track with no
/// cues. That is the deliberate trade against the alternative, which is two
/// requests silently resolving the same name; an empty file is visible,
/// harmless and fixed by deleting it and syncing again.
/// </para>
/// <para>
/// <b>Symlinks are not followed.</b> The temporary file is created with
/// <see cref="FileMode.CreateNew"/>, which fails rather than opening a link's
/// target, and a rename replaces a symlink itself rather than what it points at.
/// A planted <c>Movie.en.synced.srt -> /etc/passwd</c> in the media folder is
/// seen as an existing file by the collision check and skipped.
/// </para>
/// <para>
/// The filesystem lives behind no interface here. The behaviour that matters -
/// atomicity, collision, permissions, concurrency - is behaviour of the real
/// filesystem, and a mock would only assert that this class calls the methods
/// its own tests were written against. The tests use a real temporary directory.
/// </para>
/// </remarks>
public static class SyncedSubtitleWriter
{
    /// <summary>
    /// How many times a name lost to another save is resolved again before
    /// giving up.
    /// </summary>
    /// <remarks>
    /// Losing the race is progress, not failure: it means another request
    /// successfully created that file, so the next resolve sees it and moves on
    /// to the following suffix. Sixty-four consecutive losses therefore requires
    /// sixty-four other saves of the same item to have completed while this one
    /// was in flight, which is contention no real deployment produces. The bound
    /// exists so a misbehaving caller cannot make the request spin forever, not
    /// because reaching it is expected.
    /// </remarks>
    public const int MaxPublishAttempts = 64;

    /// <summary>
    /// How many times a single rename is retried before it is reported as a
    /// failure.
    /// </summary>
    /// <remarks>
    /// Unix <c>rename</c> is atomic and does not need this. Windows does:
    /// <c>MoveFileEx</c> with <c>MOVEFILE_REPLACE_EXISTING</c> answers
    /// <c>ERROR_ACCESS_DENIED</c> when the destination is momentarily open,
    /// which is what two overlapping overwrites of the same file look like. A
    /// genuinely read-only folder fails earlier, at the temporary file, so this
    /// backoff never delays that diagnosis.
    /// </remarks>
    private const int MaxPublishRetries = 5;

    /// <summary>
    /// The prefix on every file this class creates transiently. Chosen so it
    /// cannot pass Jellyfin's external-subtitle candidate filter, which requires
    /// the name to start with the video's own file name.
    /// </summary>
    private const string TempPrefix = ".subtitlesync-";

    private const string TempSuffix = ".tmp";

    /// <summary>
    /// UTF-8 with no byte order mark, and no silent substitution of bad input.
    /// </summary>
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Writes a synced subtitle for one item.
    /// </summary>
    /// <param name="request">
    /// Where the media file is, what the sync was derived from, and whether the
    /// source may be replaced. Every string in it must come from the server's own
    /// view of the item, never from a request body.
    /// </param>
    /// <param name="srtText">The validated, canonicalised SRT text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The path written, or an actionable failure.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public static async Task<SubtitleWriteResult> WriteAsync(
        SubtitleOutputRequest request,
        string srtText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!MediaPathParts.TrySplit(request.MediaPath, out var mediaFolder, out _, out _))
        {
            return SubtitleWriteResult.Failed(
                SubtitleWriteFailure.InvalidMediaPath,
                FormattableString.Invariant(
                    $"'{request.MediaPath}' is not a usable media file path, so there is nowhere to put the subtitle."));
        }

        if (!Directory.Exists(mediaFolder))
        {
            return SubtitleWriteResult.Failed(
                SubtitleWriteFailure.MediaFolderMissing,
                FormattableString.Invariant(
                    $"The folder '{mediaFolder}' is not on disk. The library points at a path the server cannot see - check that the volume or network share is still mounted in the container."));
        }

        if (!File.Exists(request.MediaPath))
        {
            return SubtitleWriteResult.Failed(
                SubtitleWriteFailure.MediaFileMissing,
                FormattableString.Invariant(
                    $"The media file '{request.MediaPath}' is no longer on disk. Rescan the library so the item matches what is actually there, then sync again."));
        }

        var bytes = _utf8.GetBytes(srtText ?? string.Empty);
        var resolver = new SubtitlePathResolver(File.Exists, DirectoryIsWritable);

        for (var attempt = 1; attempt <= MaxPublishAttempts; attempt++)
        {
            var resolution = resolver.Resolve(request);
            if (!resolution.Succeeded)
            {
                return Map(resolution);
            }

            if (!SaveTargetGuard.IsSafeTarget(request, resolution, out var reason))
            {
                return SubtitleWriteResult.Failed(
                    SubtitleWriteFailure.UnsafeTarget,
                    FormattableString.Invariant(
                        $"Refusing to write outside the item's own media folder: {reason} This is a bug; nothing was changed."));
            }

            if (!MediaPathParts.TrySplit(resolution.OutputPath, out var destinationFolder, out _, out _))
            {
                return SubtitleWriteResult.Failed(
                    SubtitleWriteFailure.UnsafeTarget,
                    "The resolved subtitle path names no containing folder. This is a bug; nothing was changed.");
            }

            var result = await PublishOnceAsync(resolution, destinationFolder, bytes, cancellationToken)
                .ConfigureAwait(false);

            if (result is not null)
            {
                return result;
            }

            // Null means another save claimed the name first. Nothing was
            // created, nothing is corrupt: resolve again and take the next
            // collision suffix.
        }

        return SubtitleWriteResult.Failed(
            SubtitleWriteFailure.NoAvailableName,
            FormattableString.Invariant(
                $"Gave up after {MaxPublishAttempts.ToString(CultureInfo.InvariantCulture)} attempts: every name this save resolved was taken by another write before it could be used. Nothing was changed."));
    }

    /// <summary>
    /// Claims one resolved name, fills it, and publishes it.
    /// </summary>
    /// <remarks>
    /// Its own method so the reservation handle has a lifetime a reader can see
    /// at a glance: it is opened at the top, released in the <c>finally</c>, and
    /// set to null the moment the rename takes ownership of the name.
    /// </remarks>
    /// <param name="resolution">The resolved, guarded destination.</param>
    /// <param name="destinationFolder">The folder that destination lives in.</param>
    /// <param name="bytes">The subtitle bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The outcome, or <see langword="null"/> when the name was claimed by
    /// another writer first and the caller should resolve again.
    /// </returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The handle is held in a local declared before the try, set to null on every path that hands it off, and disposed unconditionally in the finally. The analyser cannot see that the disposal inside ReleaseAsync is followed by the null assignment, because it happens across a method call. A using declaration is not an option: the whole point of the reservation is that it outlives the block that creates it and is closed at a precise moment before the rename.")]
    private static async Task<SubtitleWriteResult?> PublishOnceAsync(
        SubtitlePathResolution resolution,
        string destinationFolder,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        // Claim the name before writing anything. This is the only step in the
        // whole sequence that is atomic against another process, and everything
        // below depends on it: see the class remarks. The overwrite path needs no
        // claim - the file it replaces is already the one it is allowed to have.
        FileStream? reservation = null;
        var claimed = false;

        try
        {
            if (!resolution.OverwritesSource)
            {
                try
                {
                    reservation = Reserve(resolution.OutputPath);
                    claimed = true;
                }
                catch (IOException) when (File.Exists(resolution.OutputPath))
                {
                    return null;
                }
                catch (UnauthorizedAccessException ex)
                {
                    return NotWritable(destinationFolder, ex);
                }
                catch (IOException ex)
                {
                    return SubtitleWriteResult.Failed(
                        SubtitleWriteFailure.WriteFailed,
                        FormattableString.Invariant(
                            $"Could not create '{resolution.OutputPath}': {ex.Message} Nothing was changed."));
                }
            }

            var temporaryPath = MediaPathParts.Join(
                destinationFolder,
                TempPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + TempSuffix);

            try
            {
                await WriteTemporaryAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                await ReleaseAsync(reservation, claimed, resolution.OutputPath).ConfigureAwait(false);
                reservation = null;
                return NotWritable(destinationFolder, ex);
            }
            catch (IOException ex)
            {
                await ReleaseAsync(reservation, claimed, resolution.OutputPath).ConfigureAwait(false);
                reservation = null;
                return SubtitleWriteResult.Failed(
                    SubtitleWriteFailure.WriteFailed,
                    FormattableString.Invariant(
                        $"Could not write a temporary file in '{destinationFolder}': {ex.Message} Nothing was changed."));
            }

            // The handle has to go before the rename can replace what it holds.
            // The empty placeholder stays on disk, so the name is still not
            // available to anyone else.
            if (reservation is not null)
            {
                await reservation.DisposeAsync().ConfigureAwait(false);
                reservation = null;
            }

            for (var retry = 0; retry <= MaxPublishRetries; retry++)
            {
                var outcome = TryPublish(temporaryPath, resolution.OutputPath, out var error);

                if (outcome == PublishOutcome.Published)
                {
                    return SubtitleWriteResult.Success(
                        resolution.OutputPath,
                        resolution.Language,
                        resolution.OverwritesSource,
                        bytes.Length);
                }

                if (retry == MaxPublishRetries)
                {
                    TryDelete(temporaryPath);

                    if (claimed)
                    {
                        // Our own empty placeholder. Nobody else's file has been
                        // touched, so removing it leaves the folder as it was.
                        TryDelete(resolution.OutputPath);
                    }

                    return outcome == PublishOutcome.Denied
                        ? PublishDenied(resolution.OutputPath, error!)
                        : SubtitleWriteResult.Failed(
                            SubtitleWriteFailure.WriteFailed,
                            FormattableString.Invariant(
                                $"Could not move the finished subtitle into place as '{resolution.OutputPath}': {error?.Message} The existing files were not changed."));
                }

                await Task.Delay(1 << retry, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
        finally
        {
            if (reservation is not null)
            {
                await reservation.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// How a single rename attempt ended.
    /// </summary>
    private enum PublishOutcome
    {
        /// <summary>The file is now under its real name.</summary>
        Published,

        /// <summary>The filesystem refused for a reason that may not persist.</summary>
        Transient,

        /// <summary>The filesystem refused with a permissions error.</summary>
        Denied,
    }

    /// <summary>
    /// Claims a destination name, atomically, before anything is written to it.
    /// </summary>
    /// <remarks>
    /// <see cref="FileMode.CreateNew"/> is <c>O_CREAT|O_EXCL</c>, the one
    /// filesystem primitive that is guaranteed to succeed for exactly one of two
    /// racing callers. The handle is held with <see cref="FileShare.None"/> and
    /// the file left empty; the content arrives later, by rename.
    /// </remarks>
    /// <param name="outputPath">The name to claim.</param>
    /// <returns>The open handle holding the claim.</returns>
    private static FileStream Reserve(string outputPath)
        => new(
            outputPath,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            });

    /// <summary>
    /// Gives a claim back, removing the empty placeholder with it.
    /// </summary>
    /// <param name="reservation">The claim, or null on the overwrite path.</param>
    /// <param name="claimed">Whether this call created the placeholder.</param>
    /// <param name="outputPath">The claimed name.</param>
    /// <returns>A task that completes when the name is free again.</returns>
    private static async Task ReleaseAsync(FileStream? reservation, bool claimed, string outputPath)
    {
        if (reservation is not null)
        {
            await reservation.DisposeAsync().ConfigureAwait(false);
        }

        if (claimed)
        {
            TryDelete(outputPath);
        }
    }

    /// <summary>
    /// Attempts the one operation that makes the new subtitle visible.
    /// </summary>
    /// <remarks>
    /// Always an overwriting rename, which is <c>rename(2)</c> on Unix and
    /// <c>MoveFileEx(MOVEFILE_REPLACE_EXISTING)</c> on Windows: atomic, and it
    /// replaces either our own empty placeholder or, on the overwrite path, the
    /// source file the resolver approved.
    /// <para>
    /// The non-overwriting form of <see cref="File.Move(string, string, bool)"/>
    /// is deliberately not used. On Unix .NET implements it as an existence check
    /// followed by a plain rename, so two callers can both pass the check and the
    /// second silently destroys the first's file. That was observed against a
    /// real Jellyfin container: sixteen concurrent saves reported sixteen
    /// distinct paths and left fifteen files.
    /// </para>
    /// </remarks>
    /// <param name="temporaryPath">The complete, flushed temporary file.</param>
    /// <param name="outputPath">Where it should appear.</param>
    /// <param name="error">What the filesystem said, when it refused.</param>
    /// <returns>How it ended.</returns>
    private static PublishOutcome TryPublish(string temporaryPath, string outputPath, out Exception? error)
    {
        error = null;

        try
        {
            File.Move(temporaryPath, outputPath, overwrite: true);
            return PublishOutcome.Published;
        }
        catch (IOException ex)
        {
            error = ex;
            return PublishOutcome.Transient;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex;
            return PublishOutcome.Denied;
        }
    }

    /// <summary>
    /// Writes the bytes to a brand new file and pushes them through to the
    /// storage device.
    /// </summary>
    /// <remarks>
    /// <see cref="FileMode.CreateNew"/> and <see cref="FileShare.None"/>: the
    /// name is a fresh GUID, so a collision means something else is creating
    /// files there and the right answer is to fail. The blocking
    /// <see cref="FileStream.Flush(bool)"/> is the part that makes the rename
    /// meaningful - without it the metadata operation can be ordered ahead of the
    /// data, and a power loss leaves a correctly named empty file.
    /// </remarks>
    /// <param name="path">The temporary file path.</param>
    /// <param name="bytes">The bytes to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the bytes are durable.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA1849:Call async methods when in an async method",
        Justification = "FlushAsync only drains the managed buffer to the operating system. There is no asynchronous equivalent of Flush(true), which is the fsync that makes the following rename mean something. Blocking once on a file of at most a few hundred kilobytes is the price of not publishing an empty file after a power loss.")]
    private static async Task WriteTemporaryAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            PreallocationSize = bytes.Length,
        };

        var stream = new FileStream(path, options);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>
    /// Answers the resolver's writability question by actually trying it.
    /// </summary>
    /// <remarks>
    /// There is no portable way to ask a directory whether this process may
    /// write to it: a POSIX mode check misses ACLs, a read-only bind mount and
    /// a full disk, and .NET exposes none of them. Creating a file is the only
    /// answer that is true. <see cref="FileOptions.DeleteOnClose"/> removes it
    /// on the way out, including on Unix where the link is dropped immediately,
    /// so nothing is left in the user's media folder even if the process dies
    /// mid-probe.
    /// </remarks>
    /// <param name="folder">The folder to probe.</param>
    /// <returns>True when a file can be created there.</returns>
    private static bool DirectoryIsWritable(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return false;
        }

        var probe = MediaPathParts.Join(
            folder,
            TempPrefix + "probe-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + TempSuffix);

        try
        {
            var stream = new FileStream(
                probe,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.DeleteOnClose,
                });

            stream.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The refusal for a folder that would not accept a file.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <param name="exception">What the filesystem said.</param>
    /// <returns>The failed result.</returns>
    private static SubtitleWriteResult NotWritable(string folder, Exception exception)
        => SubtitleWriteResult.Failed(
            SubtitleWriteFailure.NotWritable,
            FormattableString.Invariant(
                $"Permission denied writing to '{folder}': {exception.Message} Mount the library read-write in the container, or give the account Jellyfin runs as write access to that folder. Nothing was changed."));

    /// <summary>
    /// The refusal for a rename the filesystem would not allow.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NotWritable(string, Exception)"/>, which is about
    /// the folder: by the time a rename is attempted the folder has already
    /// accepted a file, so a permissions error here is far more likely to be
    /// something holding the destination open. Naming both causes is the
    /// difference between an administrator checking the right thing and
    /// remounting a volume that was never the problem.
    /// </remarks>
    /// <param name="outputPath">The destination.</param>
    /// <param name="exception">What the filesystem said.</param>
    /// <returns>The failed result.</returns>
    private static SubtitleWriteResult PublishDenied(string outputPath, Exception exception)
        => SubtitleWriteResult.Failed(
            SubtitleWriteFailure.NotWritable,
            FormattableString.Invariant(
                $"Permission denied replacing '{outputPath}': {exception.Message} Either something else is holding that file open, or the account Jellyfin runs as cannot replace it. Nothing was changed."));

    /// <summary>
    /// Translates a resolver refusal into a write refusal.
    /// </summary>
    /// <param name="resolution">The failed resolution.</param>
    /// <returns>The failed result.</returns>
    private static SubtitleWriteResult Map(SubtitlePathResolution resolution)
    {
        var failure = resolution.Failure switch
        {
            SubtitlePathFailure.InvalidMediaPath => SubtitleWriteFailure.InvalidMediaPath,
            SubtitlePathFailure.MediaFolderNotWritable => SubtitleWriteFailure.NotWritable,
            SubtitlePathFailure.NameTooLong => SubtitleWriteFailure.NameTooLong,
            SubtitlePathFailure.NoAvailableName => SubtitleWriteFailure.NoAvailableName,
            _ => SubtitleWriteFailure.WriteFailed,
        };

        return SubtitleWriteResult.Failed(
            failure,
            resolution.ErrorMessage ?? "The subtitle path could not be resolved.");
    }

    /// <summary>
    /// Removes a temporary file, ignoring a failure to do so.
    /// </summary>
    /// <remarks>
    /// A leftover <c>.tmp</c> is untidy; throwing here would turn a successful
    /// save, or a clean refusal, into a 500. The name cannot pass Jellyfin's
    /// external subtitle filter, so a stray one is invisible to the library.
    /// </remarks>
    /// <param name="path">The temporary file.</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            // Nothing useful to do; see the remarks.
        }
        catch (IOException)
        {
            // Nothing useful to do; see the remarks.
        }
    }
}
