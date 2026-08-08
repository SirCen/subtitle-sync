using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.SubtitleSync.SignalCache;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests.SignalCache;

/// <summary>
/// Covers <see cref="SignalCacheKey"/>: what the key is derived from, and the
/// validation that stands between a URL segment and a file name.
/// </summary>
/// <remarks>
/// The key arrives from the client as a path segment and is then used to build
/// a file name inside the cache directory, so <see cref="SignalCacheKey.IsValid"/>
/// is a security control, not a tidiness check. It is deliberately a whitelist:
/// exactly sixty-four lowercase hex characters and nothing else. That form
/// cannot contain a separator, a dot, a drive letter, a NUL or a reserved
/// Windows device name, which makes the traversal cases below true by
/// construction rather than by a list of things someone remembered to block.
/// </remarks>
public class SignalCacheKeyTests
{
    private static readonly DateTime ModifiedUtc = new(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static SignalCacheKeyInputs Baseline() => new()
    {
        ItemId = "a1b2c3d4e5f60718293a4b5c6d7e8f90",
        MediaSourceId = "a1b2c3d4e5f60718293a4b5c6d7e8f90",
        AudioStreamIndex = 1,
        VadAggressiveness = 2,
        FileLength = 1_234_567_890L,
        FileModifiedUtc = ModifiedUtc,
    };

    // ------------------------------------------------------------------
    // Shape
    // ------------------------------------------------------------------

    /// <summary>
    /// A SHA-256 rendered as lowercase hex. The shape is the contract the
    /// validator enforces, so it is asserted directly.
    /// </summary>
    [Fact]
    public void DerivesSixtyFourLowercaseHexCharacters()
    {
        var key = SignalCacheKey.Derive(Baseline());

        Assert.Equal(64, key.Length);
        Assert.Equal(SignalCacheKey.Length, key.Length);
        Assert.All(key, c => Assert.True("0123456789abcdef".Contains(c, StringComparison.Ordinal)));
        Assert.True(SignalCacheKey.IsValid(key));
    }

    // ------------------------------------------------------------------
    // Stability and sensitivity
    // ------------------------------------------------------------------

    /// <summary>
    /// Same inputs, same key. Without this the cache never hits.
    /// </summary>
    [Fact]
    public void IsStableForIdenticalInputs()
    {
        Assert.Equal(SignalCacheKey.Derive(Baseline()), SignalCacheKey.Derive(Baseline()));
    }

    /// <summary>
    /// Every component changes the key. File length and modification time are
    /// the two that matter most: they are what makes a replaced media file
    /// invalidate its cached signal without anyone having to remember to purge
    /// anything.
    /// </summary>
    [Theory]
    [MemberData(nameof(SingleComponentChanges))]
    public void ChangesWhenAnySingleComponentChanges(string name, SignalCacheKeyInputs changed)
    {
        Assert.False(
            string.Equals(SignalCacheKey.Derive(Baseline()), SignalCacheKey.Derive(changed), StringComparison.Ordinal),
            name + " did not change the key");
    }

    public static TheoryData<string, SignalCacheKeyInputs> SingleComponentChanges()
    {
        var baseline = new SignalCacheKeyInputs
        {
            ItemId = "a1b2c3d4e5f60718293a4b5c6d7e8f90",
            MediaSourceId = "a1b2c3d4e5f60718293a4b5c6d7e8f90",
            AudioStreamIndex = 1,
            VadAggressiveness = 2,
            FileLength = 1_234_567_890L,
            FileModifiedUtc = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc),
        };

        return new TheoryData<string, SignalCacheKeyInputs>
        {
            { "ItemId", baseline with { ItemId = "b1b2c3d4e5f60718293a4b5c6d7e8f90" } },
            { "MediaSourceId", baseline with { MediaSourceId = "b1b2c3d4e5f60718293a4b5c6d7e8f90" } },
            { "AudioStreamIndex", baseline with { AudioStreamIndex = 2 } },
            { "VadAggressiveness", baseline with { VadAggressiveness = 3 } },
            { "FileLength", baseline with { FileLength = 1_234_567_891L } },
            { "FileModifiedUtc", baseline with { FileModifiedUtc = baseline.FileModifiedUtc.AddSeconds(1) } },
        };
    }

    /// <summary>
    /// The components are separated, so moving a character across a boundary
    /// produces a different key. Concatenating without a separator would make
    /// (<c>"ab"</c>, <c>"c"</c>) and (<c>"a"</c>, <c>"bc"</c>) collide.
    /// </summary>
    [Fact]
    public void DoesNotCollideWhenACharacterMovesAcrossAComponentBoundary()
    {
        var left = Baseline() with { ItemId = "ab", MediaSourceId = "c" };
        var right = Baseline() with { ItemId = "a", MediaSourceId = "bc" };

        Assert.NotEqual(SignalCacheKey.Derive(left), SignalCacheKey.Derive(right));
    }

    /// <summary>
    /// The timestamp is normalised to UTC before hashing. The same instant
    /// expressed in a different kind is the same file, and a server whose
    /// timezone changes must not lose its whole cache.
    /// </summary>
    [Fact]
    public void NormalisesTheModificationTimeToUtc()
    {
        var utc = Baseline();
        var local = Baseline() with { FileModifiedUtc = ModifiedUtc.ToLocalTime() };

        Assert.Equal(SignalCacheKey.Derive(utc), SignalCacheKey.Derive(local));
    }

    /// <summary>
    /// Numbers are formatted invariantly. A server running under a locale with
    /// digit grouping or non-Latin digits must derive the same key as every
    /// other server.
    /// </summary>
    [Fact]
    public void IsIndependentOfTheAmbientCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = SignalCacheKey.Derive(Baseline());

            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            var arabic = SignalCacheKey.Derive(Baseline());

            Assert.Equal(invariant, arabic);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ------------------------------------------------------------------
    // Validation: the traversal boundary
    // ------------------------------------------------------------------

    /// <summary>
    /// Everything a derived key can be is accepted.
    /// </summary>
    [Fact]
    public void AcceptsAnyWellFormedKey()
    {
        Assert.True(SignalCacheKey.IsValid(new string('0', 64)));
        Assert.True(SignalCacheKey.IsValid(new string('f', 64)));
        Assert.True(SignalCacheKey.IsValid(string.Concat(Enumerable.Repeat("0123456789abcdef", 4))));
    }

    /// <summary>
    /// The traversal cases. None of these can be a derived key, and each one is
    /// something a file API would otherwise happily resolve outside the cache
    /// directory.
    /// </summary>
    [Theory]
    // Relative traversal, in the obvious form and dressed up.
    [InlineData("..")]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\Windows\\System32\\config\\SAM")]
    [InlineData("./a")]
    [InlineData("a/../../b")]
    // Percent-encoded traversal, in case anything decodes late.
    [InlineData("%2e%2e%2f%2e%2e%2fetc%2fpasswd")]
    [InlineData("..%2f..%2fetc")]
    [InlineData("%252e%252e%252f")]
    // Absolute paths, POSIX and Windows, including UNC and device namespaces.
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("c:/windows/win.ini")]
    [InlineData("\\\\server\\share\\file")]
    [InlineData("\\\\?\\C:\\Windows\\win.ini")]
    // Reserved Windows device names, bare and with the extension the store adds.
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("COM1")]
    [InlineData("LPT1")]
    [InlineData("CON.sscz")]
    // Embedded NUL and other control characters, the classic truncation trick.
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000\0../x")]
    [InlineData("\0")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000\0")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000\r\n0")]
    // Alternate data streams and trailing dots or spaces, which Windows strips.
    [InlineData("000000000000000000000000000000000000000000000000000000000000000:")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000.")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000 ")]
    [InlineData(" 000000000000000000000000000000000000000000000000000000000000000")]
    // Home directory and environment expansion, in case anything expands late.
    [InlineData("~")]
    [InlineData("$HOME")]
    [InlineData("%TEMP%")]
    public void RejectsAnythingThatCouldEscapeTheCacheDirectory(string candidate)
    {
        Assert.False(SignalCacheKey.IsValid(candidate));
    }

    /// <summary>
    /// Length is exact in both directions. A short key is not padded and a long
    /// one is not truncated, because truncation is how a validated prefix ends
    /// up in front of an unvalidated tail.
    /// </summary>
    [Fact]
    public void RejectsKeysOfTheWrongLength()
    {
        Assert.False(SignalCacheKey.IsValid(string.Empty));
        Assert.False(SignalCacheKey.IsValid(new string('0', 63)));
        Assert.False(SignalCacheKey.IsValid(new string('0', 65)));
        Assert.False(SignalCacheKey.IsValid(new string('0', 100_000)));
        Assert.False(SignalCacheKey.IsValid(null));
    }

    /// <summary>
    /// Uppercase hex is rejected rather than folded. On a case-insensitive
    /// filesystem two spellings of the same key would be one file, and on a
    /// case-sensitive one they would be two: a cache whose hit rate depends on
    /// the host filesystem is worse than one that insists on a single spelling.
    /// </summary>
    [Fact]
    public void RejectsUppercaseHexRatherThanFoldingIt()
    {
        var key = SignalCacheKey.Derive(Baseline());

        Assert.True(SignalCacheKey.IsValid(key));
#pragma warning disable CA1308
        Assert.False(SignalCacheKey.IsValid(key.ToUpperInvariant()));
#pragma warning restore CA1308
        Assert.False(SignalCacheKey.IsValid(new string('A', 64)));
        Assert.False(SignalCacheKey.IsValid("A" + new string('0', 63)));
    }

    /// <summary>
    /// Non-hex characters of the right length are still rejected, including the
    /// Unicode digits and fullwidth forms that some normalisation passes fold
    /// into ASCII.
    /// </summary>
    [Theory]
    [InlineData("g")]
    [InlineData("z")]
    [InlineData("-")]
    [InlineData("_")]
    [InlineData(".")]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData("\u0660")] // Arabic-Indic digit zero.
    [InlineData("\uFF10")] // Fullwidth digit zero.
    [InlineData("\u00A0")] // Non-breaking space.
    public void RejectsNonHexCharactersEvenAtTheRightLength(string intruder)
    {
        var key = new string('0', 63) + intruder;

        Assert.Equal(64, key.Length);
        Assert.False(SignalCacheKey.IsValid(key));
    }
}
