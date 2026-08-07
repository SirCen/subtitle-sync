using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.SubtitleSync.SignalCache;

/// <summary>
/// Derives and validates the identity of a cached speech signal.
/// </summary>
/// <remarks>
/// <para>
/// A key is the SHA-256 of the six values in <see cref="SignalCacheKeyInputs"/>,
/// rendered as sixty-four lowercase hex characters.
/// </para>
/// <para>
/// <see cref="IsValid"/> is a security control, not a tidiness check. The key
/// arrives as a URL path segment and is then used to name a file inside the
/// cache directory, so it is the only thing standing between a request and an
/// arbitrary path. It is therefore a whitelist of exactly sixty-four characters
/// drawn from <c>0-9a-f</c>. Nothing in that alphabet is a directory separator,
/// a dot, a colon, a NUL, a wildcard, a tilde or a percent, and no string of
/// that shape can spell <c>CON</c>, <c>NUL</c> or <c>LPT1</c>. The traversal
/// cases are excluded by construction rather than by a blocklist someone has to
/// keep current.
/// </para>
/// <para>
/// Uppercase hex is rejected rather than folded down. Accepting both spellings
/// would mean one cache entry on NTFS or APFS and two on ext4, so the hit rate
/// would depend on the host filesystem; and a case-folding step before a path
/// join is exactly the kind of normalisation that turns a validated string back
/// into an unvalidated one.
/// </para>
/// </remarks>
public static class SignalCacheKey
{
    /// <summary>
    /// The exact length of a key. SHA-256 as hex.
    /// </summary>
    public const int Length = 64;

    /// <summary>
    /// Separates the components before hashing.
    /// </summary>
    /// <remarks>
    /// Not decoration. Plain concatenation would make item <c>ab</c> with source
    /// <c>c</c> collide with item <c>a</c> with source <c>bc</c>, and a
    /// collision here serves one film's speech signal for another's audio.
    /// </remarks>
    private const char Separator = '|';

    /// <summary>
    /// Computes the cache key for a set of inputs.
    /// </summary>
    /// <param name="inputs">What the signal was derived from.</param>
    /// <returns>Sixty-four lowercase hex characters.</returns>
    public static string Derive(SignalCacheKeyInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var material = new StringBuilder()
            .Append(inputs.ItemId)
            .Append(Separator)
            .Append(inputs.MediaSourceId)
            .Append(Separator)
            .Append(inputs.AudioStreamIndex.ToString(CultureInfo.InvariantCulture))
            .Append(Separator)
            .Append(inputs.VadAggressiveness.ToString(CultureInfo.InvariantCulture))
            .Append(Separator)
            .Append(inputs.FileLength.ToString(CultureInfo.InvariantCulture))
            .Append(Separator)

            // Ticks rather than a formatted date: no calendar, no timezone
            // database and no format string to drift between server versions.
            .Append(inputs.FileModifiedUtc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture))
            .ToString();

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>
    /// Decides whether a caller-supplied string is safe to use as a key.
    /// </summary>
    /// <param name="key">The candidate, straight off the wire.</param>
    /// <returns>
    /// <see langword="true"/> only for exactly sixty-four lowercase hex
    /// characters.
    /// </returns>
    public static bool IsValid(string? key)
    {
        if (key is null || key.Length != Length)
        {
            return false;
        }

        foreach (var c in key)
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Throws unless <paramref name="key"/> passes <see cref="IsValid"/>.
    /// </summary>
    /// <param name="key">The candidate.</param>
    /// <param name="parameterName">The parameter being checked, for the message.</param>
    /// <exception cref="ArgumentException">The key is not well formed.</exception>
    /// <remarks>
    /// The message never echoes the offending value. It would be attacker
    /// controlled text in a server log that an administrator reads in a
    /// terminal, and there is nothing useful in it anyway.
    /// </remarks>
    public static void ThrowIfInvalid(string? key, string parameterName)
    {
        if (!IsValid(key))
        {
            throw new ArgumentException(
                "A speech signal cache key must be exactly "
                + Length.ToString(CultureInfo.InvariantCulture)
                + " lowercase hexadecimal characters.",
                parameterName);
        }
    }
}
