using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.SubtitleSync.Subtitles;

/// <summary>
/// Decides whether a posted document is SRT, and produces the canonical bytes to
/// write if it is.
/// </summary>
/// <remarks>
/// <para>
/// The body of a save request is attacker-controlled text on its way into a
/// user's media folder, so nothing reaches the filesystem until it has parsed
/// here. Pure by construction: no I/O, no state, no dependency on a running
/// server, which is what lets every awkward document be a unit test.
/// </para>
/// <para>
/// The grammar accepted is deliberately the one <c>lib/srt.ts</c> reads rather
/// than the one it writes: refusing a document our own parser would have
/// accepted would make the plugin stricter than the algorithm it exists to
/// serve. So the cue index is optional and the millisecond separator may be a
/// dot. Both are normalised on the way out.
/// </para>
/// <para>
/// Hand-written rather than a regular expression. The input is untrusted and
/// unbounded, a character-at-a-time scan has no backtracking to worry about, and
/// the error messages can name the offending line, which a match failure cannot.
/// </para>
/// </remarks>
public static class SrtValidator
{
    /// <summary>
    /// The largest request body the save endpoint will buffer, in bytes.
    /// </summary>
    /// <remarks>
    /// A dense three-hour SDH track is comfortably under 300 KB, so 8 MiB is
    /// roughly thirty times the worst realistic case while still being far too
    /// small to be interesting as a way to fill a disk. The cap is enforced as
    /// the body is read, not after, so an unbounded POST is refused without ever
    /// being held in memory.
    /// </remarks>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The largest number of cues that will be written.
    /// </summary>
    /// <remarks>
    /// A feature-length film runs to a couple of thousand. Fifty thousand is
    /// past anything real and bounds the work done per request independently of
    /// <see cref="MaxBytes"/>.
    /// </remarks>
    public const int MaxCues = 50_000;

    private const char ByteOrderMark = '\uFEFF';

    private const string Arrow = "-->";

    /// <summary>
    /// Validates a document and canonicalises it.
    /// </summary>
    /// <param name="text">The decoded request body.</param>
    /// <returns>The verdict, carrying the text to write when it passes.</returns>
    public static SrtValidation Validate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Empty();
        }

        var body = text[0] == ByteOrderMark ? text[1..] : text;

        var control = FindControlCharacter(body);
        if (control > 0)
        {
            return SrtValidation.Invalid(
                SrtValidationError.ControlCharacter,
                FormattableString.Invariant(
                    $"Line {control} contains a control character. A subtitle may only contain printable text and tabs."));
        }

        var lines = SplitLines(body);
        var output = new StringBuilder(body.Length + 64);
        var cueCount = 0;
        var i = 0;

        while (i < lines.Count)
        {
            if (IsBlank(lines[i]))
            {
                i++;
                continue;
            }

            // A bare numeric line is the cue index. Consumed unconditionally:
            // the only other line that can start a cue is the timing line, and
            // that never parses as a number.
            if (IsAllDigits(lines[i]))
            {
                i++;
            }

            if (i >= lines.Count || !TryParseTiming(lines[i], out var start, out var end, out var timing))
            {
                return SrtValidation.Invalid(
                    SrtValidationError.MissingTiming,
                    FormattableString.Invariant(
                        $"Line {Math.Min(i, lines.Count - 1) + 1} is not a subtitle timing line. Expected 'HH:MM:SS,mmm --> HH:MM:SS,mmm'. This does not look like an SRT file; WebVTT and ASS are not accepted."));
            }

            if (end < start)
            {
                return SrtValidation.Invalid(
                    SrtValidationError.ReversedCue,
                    FormattableString.Invariant(
                        $"The cue on line {i + 1} ends before it starts. Re-run the sync; a subtitle with reversed timings will not play."));
            }

            var timingLine = i;
            i++;

            var textStart = i;
            while (i < lines.Count && !IsBlank(lines[i]))
            {
                i++;
            }

            if (i == textStart)
            {
                return SrtValidation.Invalid(
                    SrtValidationError.EmptyCue,
                    FormattableString.Invariant(
                        $"The cue on line {timingLine + 1} has no text under its timing line."));
            }

            cueCount++;
            if (cueCount > MaxCues)
            {
                return SrtValidation.Invalid(
                    SrtValidationError.TooManyCues,
                    FormattableString.Invariant(
                        $"The document holds more than {MaxCues.ToString(CultureInfo.InvariantCulture)} cues, which is past anything a real subtitle track contains."));
            }

            if (cueCount > 1)
            {
                output.Append("\r\n");
            }

            output.Append(cueCount.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            output.Append(timing).Append("\r\n");

            for (var t = textStart; t < i; t++)
            {
                output.Append(lines[t]).Append("\r\n");
            }
        }

        return cueCount == 0 ? Empty() : SrtValidation.Valid(output.ToString(), cueCount);
    }

    /// <summary>
    /// The refusal for a document with nothing in it.
    /// </summary>
    /// <returns>The validation.</returns>
    private static SrtValidation Empty()
        => SrtValidation.Invalid(
            SrtValidationError.Empty,
            "The subtitle is empty. There is nothing to save.");

    /// <summary>
    /// Finds the first line carrying a character a subtitle may not contain.
    /// </summary>
    /// <remarks>
    /// Tab, carriage return and line feed are the only control characters that
    /// belong in text. A NUL in particular is how a string is made to end early
    /// somewhere further down the stack, so it is refused outright rather than
    /// stripped.
    /// </remarks>
    /// <param name="text">The document.</param>
    /// <returns>The 1-based line number, or zero when the text is clean.</returns>
    private static int FindControlCharacter(string text)
    {
        var line = 1;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\n')
            {
                line++;
                continue;
            }

            if (c is '\r' or '\t')
            {
                continue;
            }

            if (c < ' ' || c == '\u007f')
            {
                return line;
            }
        }

        return 0;
    }

    /// <summary>
    /// Splits a document into lines, accepting CRLF, LF and bare CR.
    /// </summary>
    /// <remarks>
    /// Not <c>string.Split</c>: a document with mixed endings has to come apart
    /// the same way whichever it uses, and a bare CR is what a file that has been
    /// through an old Mac editor looks like.
    /// </remarks>
    /// <param name="text">The document.</param>
    /// <returns>The lines, without their terminators.</returns>
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>(64);
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is not ('\n' or '\r'))
            {
                continue;
            }

            lines.Add(text[start..i]);

            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>
    /// Tests whether a line is empty or whitespace only.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>True when the line separates cues rather than carrying content.</returns>
    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    /// <summary>
    /// Tests whether a line is nothing but ASCII digits, ignoring surrounding
    /// whitespace.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>True when the line is a cue index.</returns>
    private static bool IsAllDigits(string line)
    {
        var trimmed = line.AsSpan().Trim();
        if (trimmed.IsEmpty)
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses a timing line.
    /// </summary>
    /// <param name="line">The candidate line.</param>
    /// <param name="start">The cue start, in milliseconds.</param>
    /// <param name="end">The cue end, in milliseconds.</param>
    /// <param name="canonical">
    /// The line re-emitted with comma millisecond separators, preserving any
    /// trailing extension such as the VobSub coordinates some tools append.
    /// </param>
    /// <returns>True when the line is a well-formed timing line.</returns>
    private static bool TryParseTiming(string line, out long start, out long end, out string canonical)
    {
        start = 0;
        end = 0;
        canonical = string.Empty;

        var span = line.AsSpan();
        var at = 0;

        SkipWhitespace(span, ref at);
        if (!TryParseTimecode(span, ref at, out start, out var startText))
        {
            return false;
        }

        SkipWhitespace(span, ref at);
        if (at + Arrow.Length > span.Length || !span[at..(at + Arrow.Length)].SequenceEqual(Arrow))
        {
            return false;
        }

        at += Arrow.Length;
        SkipWhitespace(span, ref at);
        if (!TryParseTimecode(span, ref at, out end, out var endText))
        {
            return false;
        }

        var rest = span[at..].Trim();

        // Anything left has to be separated from the timecode, so a stray digit
        // glued to the end cannot pass as an extension.
        if (!rest.IsEmpty && at < span.Length && span[at] is not (' ' or '\t'))
        {
            return false;
        }

        canonical = rest.IsEmpty
            ? startText + " " + Arrow + " " + endText
            : startText + " " + Arrow + " " + endText + " " + rest.ToString();

        return true;
    }

    /// <summary>
    /// Parses one <c>HH:MM:SS,mmm</c> timecode.
    /// </summary>
    /// <remarks>
    /// The hour field takes two or more digits, because a long recording legally
    /// runs past 99 hours and some tools emit three. Minutes, seconds and
    /// milliseconds are fixed width, which is what makes a truncated or
    /// over-long field a refusal rather than a silent misreading.
    /// </remarks>
    /// <param name="span">The line.</param>
    /// <param name="at">The cursor, advanced past the timecode on success.</param>
    /// <param name="milliseconds">The parsed position.</param>
    /// <param name="canonical">The timecode re-emitted with a comma separator.</param>
    /// <returns>True when a timecode was parsed.</returns>
    private static bool TryParseTimecode(ReadOnlySpan<char> span, ref int at, out long milliseconds, out string canonical)
    {
        milliseconds = 0;
        canonical = string.Empty;

        var cursor = at;

        var hourStart = cursor;
        while (cursor < span.Length && span[cursor] is >= '0' and <= '9')
        {
            cursor++;
        }

        var hourDigits = cursor - hourStart;
        if (hourDigits < 2 || cursor >= span.Length || span[cursor] != ':')
        {
            return false;
        }

        var hours = long.Parse(span[hourStart..cursor], CultureInfo.InvariantCulture);
        cursor++;

        if (!TryParseFixedDigits(span, ref cursor, 2, out var minutes) || cursor >= span.Length || span[cursor] != ':')
        {
            return false;
        }

        cursor++;

        if (!TryParseFixedDigits(span, ref cursor, 2, out var seconds)
            || cursor >= span.Length
            || span[cursor] is not (',' or '.'))
        {
            return false;
        }

        cursor++;

        if (!TryParseFixedDigits(span, ref cursor, 3, out var fraction))
        {
            return false;
        }

        // A fourth digit means the field was not milliseconds, so the whole
        // timecode is something else and must not be accepted as one.
        if (cursor < span.Length && span[cursor] is >= '0' and <= '9')
        {
            return false;
        }

        if (minutes > 59 || seconds > 59)
        {
            return false;
        }

        milliseconds = (((hours * 60) + minutes) * 60 * 1000) + (seconds * 1000) + fraction;

        canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{span[hourStart..(hourStart + hourDigits)]}:{minutes:D2}:{seconds:D2},{fraction:D3}");

        at = cursor;
        return true;
    }

    /// <summary>
    /// Parses exactly the requested number of ASCII digits.
    /// </summary>
    /// <param name="span">The line.</param>
    /// <param name="at">The cursor, advanced on success.</param>
    /// <param name="count">How many digits are required.</param>
    /// <param name="value">The parsed value.</param>
    /// <returns>True when exactly that many digits were present.</returns>
    private static bool TryParseFixedDigits(ReadOnlySpan<char> span, ref int at, int count, out long value)
    {
        value = 0;

        if (at + count > span.Length)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            var c = span[at + i];
            if (c is < '0' or > '9')
            {
                return false;
            }

            value = (value * 10) + (c - '0');
        }

        at += count;
        return true;
    }

    /// <summary>
    /// Advances a cursor past spaces and tabs.
    /// </summary>
    /// <param name="span">The line.</param>
    /// <param name="at">The cursor.</param>
    private static void SkipWhitespace(ReadOnlySpan<char> span, ref int at)
    {
        while (at < span.Length && span[at] is ' ' or '\t')
        {
            at++;
        }
    }
}
