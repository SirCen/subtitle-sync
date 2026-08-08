using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SubtitleSync.Paths;
using Jellyfin.Plugin.SubtitleSync.Writing;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests.Writing;

/// <summary>
/// The wiring check. <see cref="SubtitlePathResolver"/> is safe as a function of
/// its inputs; this asserts that no reachable set of inputs gets a path outside
/// the item's own media folder past the last gate.
/// </summary>
/// <remarks>
/// Resolutions are produced by running the real resolver rather than being
/// fabricated, because a fabricated one proves nothing about the pair. The
/// hostile cases are therefore expressed the way an attack would have to arrive:
/// as a hostile <see cref="SubtitleOutputRequest"/>.
/// </remarks>
public class SaveTargetGuardTests
{
    private const string MediaFolder = "/media/Movies";
    private const string MoviePath = MediaFolder + "/Movie.mkv";

    // -----------------------------------------------------------------------
    // The intended paths
    // -----------------------------------------------------------------------

    [Fact]
    public void AcceptsANewSiblingBesideTheMediaFile()
    {
        var request = Request(MoviePath, SubtitleSource.Embedded());

        Assert.True(SaveTargetGuard.IsSafeTarget(request, Resolve(request), out _));
    }

    [Fact]
    public void AcceptsACollisionSuffixedSibling()
    {
        var request = Request(MoviePath, SubtitleSource.Embedded());
        var resolution = Resolve(request, existing: ["/media/Movies/Movie.en.synced.srt"]);

        Assert.Equal("/media/Movies/Movie.en.synced.2.srt", resolution.OutputPath);
        Assert.True(SaveTargetGuard.IsSafeTarget(request, resolution, out _));
    }

    [Fact]
    public void AcceptsAnOverwriteOfTheItemsOwnExternalTrack()
    {
        const string Source = "/media/Movies/Movie.en.srt";
        var request = Request(MoviePath, SubtitleSource.External(Source), overwrite: true);
        var resolution = Resolve(request, existing: [Source]);

        Assert.True(resolution.OverwritesSource);
        Assert.True(SaveTargetGuard.IsSafeTarget(request, resolution, out _));
    }

    /// <summary>
    /// Jellyfin also finds external subtitles in an item's internal metadata
    /// folder, so an overwrite target is not required to sit beside the video -
    /// only to be named after it.
    /// </summary>
    [Fact]
    public void AcceptsAnOverwriteInTheItemsInternalMetadataFolder()
    {
        const string Source = "/config/metadata/library/aa/Movie.en.srt";
        var request = Request(MoviePath, SubtitleSource.External(Source), overwrite: true);
        var resolution = Resolve(request, existing: [Source]);

        Assert.True(SaveTargetGuard.IsSafeTarget(request, resolution, out _));
    }

    // -----------------------------------------------------------------------
    // Escaping the media folder
    // -----------------------------------------------------------------------

    /// <summary>
    /// The overwrite branch is the one that returns a caller-supplied path
    /// verbatim, so it is the one worth attacking. A file that is not named after
    /// this item cannot be one of its external tracks.
    /// </summary>
    [Fact]
    public void RefusesAnOverwriteOfAFileNotNamedAfterTheItem()
    {
        const string Source = "/etc/cron.d/payload.srt";
        var request = Request(MoviePath, SubtitleSource.External(Source), overwrite: true);
        var resolution = Resolve(request, existing: [Source]);

        Assert.True(resolution.OverwritesSource, "the resolver returns the source verbatim, by design");
        Assert.False(SaveTargetGuard.IsSafeTarget(request, resolution, out var reason));
        Assert.Contains("named after", reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The near miss: right prefix, wrong item. <c>Movie2.en.srt</c> would not be
    /// picked up as a track of <c>Movie.mkv</c> either, because Jellyfin requires
    /// a delimiter after the prefix.
    /// </summary>
    [Fact]
    public void RefusesAnOverwriteOfANameThatMerelyStartsWithTheItemsName()
    {
        const string Source = "/media/Movies/Movie2.en.srt";
        var request = Request(MoviePath, SubtitleSource.External(Source), overwrite: true);
        var resolution = Resolve(request, existing: [Source]);

        Assert.False(SaveTargetGuard.IsSafeTarget(request, resolution, out _));
    }

    /// <summary>
    /// <c>Movie.srt</c> is a legitimate external track of <c>Movie.mkv</c> - the
    /// prefix is followed by the extension's own dot - so the name check must not
    /// insist on a language segment.
    /// </summary>
    [Fact]
    public void AcceptsAnOverwriteOfTheLanguagelessSibling()
    {
        const string Source = "/media/Movies/Movie.srt";
        var request = Request(MoviePath, SubtitleSource.External(Source), overwrite: true);
        var resolution = Resolve(request, existing: [Source]);

        Assert.True(SaveTargetGuard.IsSafeTarget(request, resolution, out _));
    }

    /// <summary>
    /// A media path built from an attacker-influenced folder name. The resolver
    /// preserves what it is given; the guard has to notice.
    /// </summary>
    [Fact]
    public void RefusesAPathWithATraversalSegment()
    {
        const string Source = "/media/Movies/../../etc/Movie.en.srt";
        var request = Request(MoviePath, SubtitleSource.External(Source), overwrite: true);
        var resolution = Resolve(request, existing: [Source]);

        Assert.False(SaveTargetGuard.IsSafeTarget(request, resolution, out var reason));
        Assert.Contains("..", reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A NUL is how a path is made to mean one thing to a managed string check
    /// and another to a syscall further down.
    /// </summary>
    [Fact]
    public void RefusesAPathContainingANulCharacter()
    {
        const string Source = "/media/Movies/Movie.en\0.srt";
        var request = Request(MoviePath, SubtitleSource.External(Source), overwrite: true);
        var resolution = Resolve(request, existing: [Source]);

        Assert.False(SaveTargetGuard.IsSafeTarget(request, resolution, out var reason));
        Assert.Contains("NUL", reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A source that is not an SRT file never becomes an overwrite target at all:
    /// the resolver drops it and falls back to a new sibling, because writing SRT
    /// bytes into <c>Movie.en.ass</c> would corrupt a track Jellyfin still parses
    /// by extension.
    /// </summary>
    [Fact]
    public void ANonSrtSourceFallsBackToASafeSibling()
    {
        var request = Request(MoviePath, SubtitleSource.External("/etc/cron.d/payload.sh"), overwrite: true);
        var resolution = Resolve(request, existing: ["/etc/cron.d/payload.sh"]);

        Assert.False(resolution.OverwritesSource);
        Assert.Equal("/media/Movies/Movie.en.synced.srt", resolution.OutputPath);
        Assert.True(SaveTargetGuard.IsSafeTarget(request, resolution, out _));
    }

    [Fact]
    public void RefusesANullRequest()
    {
        var request = Request(MoviePath, SubtitleSource.Embedded());
        var resolution = Resolve(request);

        Assert.Throws<ArgumentNullException>(() => SaveTargetGuard.IsSafeTarget(null!, resolution, out _));
        Assert.Throws<ArgumentNullException>(() => SaveTargetGuard.IsSafeTarget(request, null!, out _));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static SubtitleOutputRequest Request(string mediaPath, SubtitleSource source, bool overwrite = false)
        => new()
        {
            MediaPath = mediaPath,
            Language = "en",
            Source = source,
            OverwriteOriginal = overwrite,
        };

    private static SubtitlePathResolution Resolve(SubtitleOutputRequest request, IReadOnlyCollection<string>? existing = null)
    {
        var taken = new HashSet<string>(existing ?? [], StringComparer.Ordinal);
        return new SubtitlePathResolver(taken.Contains, _ => true).Resolve(request);
    }
}
