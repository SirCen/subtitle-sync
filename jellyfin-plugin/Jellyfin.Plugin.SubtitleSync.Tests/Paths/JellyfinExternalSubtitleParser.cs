using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubtitleSync.Tests.Paths;

/// <summary>
/// A faithful replica of the two-stage external-subtitle filename parser that
/// Jellyfin 10.11 applies to sibling subtitle files.
/// </summary>
/// <remarks>
/// Transcribed from <c>MediaBrowser.Providers/MediaInfo/MediaInfoResolver.cs</c>
/// (candidate filter, line 234) and
/// <c>Emby.Naming/ExternalFiles/ExternalPathParser.cs</c> (segment parser) at
/// tag v10.11.11, as recorded in <c>research/jellyfin-10.11-plugin-api.md</c>
/// section 9. It exists so the naming scheme can be asserted against the real
/// consumer of these filenames rather than against our own assumptions. The one
/// piece that cannot be transcribed is
/// <c>ILocalizationManager.FindLanguageInfo</c>, which is supplied as a fake.
/// </remarks>
internal static class JellyfinExternalSubtitleParser
{
    private static readonly string[] MediaForcedFlags = ["foreign", "forced"];
    private static readonly string[] MediaDefaultFlags = ["default"];
    private static readonly string[] MediaHearingImpairedFlags = ["cc", "hi", "sdh"];

    /// <summary>
    /// A small stand-in for the server's localization manager. Keys are what a
    /// filename segment might contain; values are what Jellyfin would store in
    /// <c>MediaStream.Language</c>.
    /// </summary>
    private static readonly Dictionary<string, string> KnownLanguages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "eng",
            ["eng"] = "eng",
            ["english"] = "eng",
            ["fr"] = "fre",
            ["fre"] = "fre",
            ["fra"] = "fre",
            ["de"] = "ger",
            ["ger"] = "ger",
            ["ru"] = "rus",
            ["rus"] = "rus",
            ["hi"] = "hin",
            ["hin"] = "hin",
            ["zh-Hans"] = "zh-Hans",
        };

    /// <summary>
    /// Runs stage 1 (candidate filter) and, if it passes, stage 2 (segment
    /// parser) over a subtitle filename.
    /// </summary>
    /// <param name="videoFileNameWithoutExtension">The video's file name without its extension.</param>
    /// <param name="subtitleFileName">The candidate subtitle file name, with extension.</param>
    /// <returns>The parse result.</returns>
    public static ParsedExternalSubtitle Parse(
        string videoFileNameWithoutExtension,
        string subtitleFileName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(subtitleFileName);
        var prefix = videoFileNameWithoutExtension;

        // Stage 1, MediaInfoResolver line 234. MediaFlagDelimiters is ['.'].
        var isCandidate = stem.Length >= prefix.Length
            && string.Equals(stem[..prefix.Length], prefix, StringComparison.OrdinalIgnoreCase)
            && (stem.Length == prefix.Length || stem[prefix.Length] == '.');

        if (!isCandidate)
        {
            return new ParsedExternalSubtitle { IsCandidate = false };
        }

        // Stage 2, ExternalPathParser.ParseFile, separator '.', right to left.
        var result = new ParsedExternalSubtitle { IsCandidate = true };
        var languageString = stem[prefix.Length..];
        var titleString = string.Empty;

        while (languageString.Length > 0)
        {
            var lastSeparator = languageString.LastIndexOf('.');
            if (lastSeparator == -1)
            {
                break;
            }

            var currentSlice = languageString[lastSeparator..];
            var segment = currentSlice[1..];

            if (MediaDefaultFlags.Any(s => segment.Contains(s, StringComparison.OrdinalIgnoreCase)))
            {
                result.IsDefault = true;
            }
            else if (MediaForcedFlags.Any(s => segment.Contains(s, StringComparison.OrdinalIgnoreCase)))
            {
                result.IsForced = true;
            }
            else
            {
                var culture = KnownLanguages.GetValueOrDefault(segment);

                if (culture is not null && result.Language is null)
                {
                    result.Language = culture;
                }
                else if (culture is not null && string.Equals(result.Language, "hin", StringComparison.Ordinal))
                {
                    result.IsHearingImpaired = true;
                    result.Language = culture;
                }
                else if (MediaHearingImpairedFlags.Any(s => segment.Equals(s, StringComparison.OrdinalIgnoreCase)))
                {
                    result.IsHearingImpaired = true;
                }
                else
                {
                    titleString = currentSlice + titleString;
                }
            }

            languageString = languageString[..lastSeparator];
        }

        result.Title = titleString.Length >= 1 ? titleString[1..] : null;
        return result;
    }

    /// <summary>
    /// Reproduces the subtitle branch of <c>MediaStream.DisplayTitle</c>: the
    /// title leads, and each attribute is appended only when the title does not
    /// already contain it.
    /// </summary>
    /// <param name="parsed">A stage 2 parse result.</param>
    /// <returns>The string the track picker would show.</returns>
    public static string DisplayTitle(ParsedExternalSubtitle parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        var attributes = new List<string>
        {
            parsed.Language switch
            {
                "eng" => "English",
                "fre" => "French",
                "ger" => "German",
                "rus" => "Russian",
                "hin" => "Hindi",
                null => "Und",
                _ => parsed.Language,
            },
        };

        if (parsed.IsHearingImpaired)
        {
            attributes.Add("Hearing Impaired");
        }

        if (parsed.IsDefault)
        {
            attributes.Add("Default");
        }

        if (parsed.IsForced)
        {
            attributes.Add("Forced");
        }

        attributes.Add("SRT");
        attributes.Add("External");

        if (string.IsNullOrEmpty(parsed.Title))
        {
            return string.Join(" - ", attributes);
        }

        var kept = attributes.Where(
            tag => !parsed.Title.Contains(tag, StringComparison.OrdinalIgnoreCase));

        return string.Join(" - ", new[] { parsed.Title }.Concat(kept));
    }
}

/// <summary>
/// What Jellyfin would derive from an external subtitle filename.
/// </summary>
internal sealed class ParsedExternalSubtitle
{
    /// <summary>
    /// Gets or sets a value indicating whether stage 1 accepted the file as
    /// belonging to the video at all.
    /// </summary>
    public bool IsCandidate { get; set; }

    /// <summary>Gets or sets the resolved language, or null.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the leftover segments that become the track title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets a value indicating whether the track is flagged default.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Gets or sets a value indicating whether the track is flagged forced.</summary>
    public bool IsForced { get; set; }

    /// <summary>Gets or sets a value indicating whether the track is flagged hearing impaired.</summary>
    public bool IsHearingImpaired { get; set; }
}
