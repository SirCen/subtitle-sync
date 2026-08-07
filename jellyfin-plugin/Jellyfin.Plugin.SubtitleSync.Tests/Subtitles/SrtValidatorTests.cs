using System;
using System.Linq;
using Jellyfin.Plugin.SubtitleSync.Subtitles;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests.Subtitles;

/// <summary>
/// The gate between attacker-controlled text and a file written into someone's
/// media library. Everything that is not recognisably SRT has to be refused
/// here, before any path is resolved and long before anything is created.
/// </summary>
public class SrtValidatorTests
{
    private const string Minimal = "1\n00:00:01,000 --> 00:00:02,000\nHello\n";

    // -----------------------------------------------------------------------
    // Accepting real SRT
    // -----------------------------------------------------------------------

    [Fact]
    public void AcceptsAMinimalSingleCueDocument()
    {
        var result = SrtValidator.Validate(Minimal);

        Assert.True(result.IsValid);
        Assert.Equal(SrtValidationError.None, result.Error);
        Assert.Equal(1, result.CueCount);
    }

    [Fact]
    public void AcceptsSeveralCuesSeparatedByBlankLines()
    {
        var result = SrtValidator.Validate(
            "1\n00:00:01,000 --> 00:00:02,000\nHello\n\n2\n00:00:03,000 --> 00:00:04,500\nWorld\nSecond line\n");

        Assert.True(result.IsValid);
        Assert.Equal(2, result.CueCount);
    }

    /// <summary>
    /// <c>lib/srt.ts</c> writes plain <c>\n</c>. A file that has been through a
    /// Windows editor arrives as <c>\r\n</c>. Both are the same document.
    /// </summary>
    [Fact]
    public void AcceptsCrlfAndBareCrLineEndings()
    {
        Assert.True(SrtValidator.Validate("1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n").IsValid);
        Assert.True(SrtValidator.Validate("1\r00:00:01,000 --> 00:00:02,000\rHello\r").IsValid);
    }

    [Fact]
    public void AcceptsALeadingByteOrderMark()
    {
        var result = SrtValidator.Validate("\uFEFF" + Minimal);

        Assert.True(result.IsValid);
        Assert.DoesNotContain("\uFEFF", result.NormalisedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The index line is optional in practice and <c>lib/srt.ts</c>'s own parser
    /// tolerates its absence, so refusing it would be stricter than the code that
    /// produced the text.
    /// </summary>
    [Fact]
    public void AcceptsCuesWithNoIndexLine()
    {
        var result = SrtValidator.Validate("00:00:01,000 --> 00:00:02,000\nHello\n");

        Assert.True(result.IsValid);
        Assert.Equal(1, result.CueCount);
    }

    /// <summary>
    /// A dot as the millisecond separator is common enough that
    /// <c>lib/srt.ts</c> parses it. It is normalised on the way out.
    /// </summary>
    [Fact]
    public void AcceptsADotMillisecondSeparatorAndCanonicalisesIt()
    {
        var result = SrtValidator.Validate("1\n00:00:01.250 --> 00:00:02.750\nHi\n");

        Assert.True(result.IsValid);
        Assert.Contains("00:00:01,250 --> 00:00:02,750", result.NormalisedText, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsHourFieldsBeyondTwoDigits()
    {
        Assert.True(SrtValidator.Validate("1\n100:00:01,000 --> 100:00:02,000\nHi\n").IsValid);
    }

    /// <summary>
    /// VobSub-derived files carry coordinates after the timing. They are not ours
    /// to reject, and they are preserved verbatim.
    /// </summary>
    [Fact]
    public void AcceptsAndPreservesTrailingTimingLineExtensions()
    {
        var result = SrtValidator.Validate(
            "1\n00:00:01,000 --> 00:00:02,000 X1:100 X2:200 Y1:300 Y2:400\nHi\n");

        Assert.True(result.IsValid);
        Assert.Contains("X1:100 X2:200 Y1:300 Y2:400", result.NormalisedText, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsAZeroLengthCue()
    {
        Assert.True(SrtValidator.Validate("1\n00:00:01,000 --> 00:00:01,000\nHi\n").IsValid);
    }

    [Fact]
    public void AcceptsMarkupAndNonLatinTextInTheCueBody()
    {
        Assert.True(SrtValidator.Validate("1\n00:00:01,000 --> 00:00:02,000\n<i>你好</i>\n{\\an8}wat\n").IsValid);
    }

    // -----------------------------------------------------------------------
    // Refusing everything else
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\uFEFF")]
    [InlineData("\n\n\n")]
    public void RefusesAnEmptyDocument(string text)
    {
        var result = SrtValidator.Validate(text);

        Assert.False(result.IsValid);
        Assert.Equal(SrtValidationError.Empty, result.Error);
    }

    [Fact]
    public void RefusesTextWithNoTimingLineAtAll()
    {
        var result = SrtValidator.Validate("this is just prose\nand more prose\n");

        Assert.False(result.IsValid);
        Assert.Equal(SrtValidationError.MissingTiming, result.Error);
    }

    /// <summary>
    /// The realistic mistake: posting the wrong file. WebVTT looks close enough
    /// to SRT to be worth a specific refusal rather than a generic one.
    /// </summary>
    [Fact]
    public void RefusesWebVtt()
    {
        var result = SrtValidator.Validate("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHello\n");

        Assert.False(result.IsValid);
        Assert.Equal(SrtValidationError.MissingTiming, result.Error);
    }

    [Fact]
    public void RefusesAdvancedSubStation()
    {
        var result = SrtValidator.Validate(
            "[Script Info]\nTitle: x\n\n[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Hello\n");

        Assert.False(result.IsValid);
        Assert.Equal(SrtValidationError.MissingTiming, result.Error);
    }

    [Theory]
    [InlineData("1\n00:00:01,000 -> 00:00:02,000\nHi\n")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,00\nHi\n")]
    [InlineData("1\n00:00:1,000 --> 00:00:02,000\nHi\n")]
    [InlineData("1\n00:00:01,000 --> \nHi\n")]
    [InlineData("1\n00:00:01,000000 --> 00:00:02,000\nHi\n")]
    [InlineData("1\n0:00:01,000 --> 00:00:02,000\nHi\n")]
    public void RefusesAMalformedTimingLine(string text)
    {
        var result = SrtValidator.Validate(text);

        Assert.False(result.IsValid);
        Assert.Equal(SrtValidationError.MissingTiming, result.Error);
    }

    [Fact]
    public void RefusesAnEndTimeBeforeItsStart()
    {
        var result = SrtValidator.Validate("1\n00:00:05,000 --> 00:00:02,000\nHi\n");

        Assert.False(result.IsValid);
        Assert.Equal(SrtValidationError.ReversedCue, result.Error);
    }

    [Fact]
    public void RefusesACueWithNoText()
    {
        var result = SrtValidator.Validate("1\n00:00:01,000 --> 00:00:02,000\n\n2\n00:00:03,000 --> 00:00:04,000\nHi\n");

        Assert.False(result.IsValid);
        Assert.Equal(SrtValidationError.EmptyCue, result.Error);
    }

    [Fact]
    public void RefusesACueWithNoTextAtEndOfDocument()
    {
        var result = SrtValidator.Validate("1\n00:00:01,000 --> 00:00:02,000\n");

        Assert.False(result.IsValid);
        Assert.Equal(SrtValidationError.EmptyCue, result.Error);
    }

    /// <summary>
    /// A NUL is the classic way to make a path or a parser stop early. It has no
    /// business in a subtitle.
    /// </summary>
    [Theory]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nHi\0there\n")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nHi\u0007there\n")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nHi\u001bthere\n")]
    public void RefusesControlCharactersInTheBody(string text)
    {
        var result = SrtValidator.Validate(text);

        Assert.False(result.IsValid);
        Assert.Equal(SrtValidationError.ControlCharacter, result.Error);
    }

    [Fact]
    public void AllowsTabsInTheBody()
    {
        Assert.True(SrtValidator.Validate("1\n00:00:01,000 --> 00:00:02,000\nHi\tthere\n").IsValid);
    }

    [Fact]
    public void RefusesADocumentLongerThanTheCueLimit()
    {
        var text = string.Concat(
            Enumerable.Range(0, SrtValidator.MaxCues + 1)
                .Select(i => "00:00:01,000 --> 00:00:02,000\nx\n\n"));

        var result = SrtValidator.Validate(text);

        Assert.False(result.IsValid);
        Assert.Equal(SrtValidationError.TooManyCues, result.Error);
    }

    /// <summary>
    /// Every refusal has to be something an administrator can act on, because
    /// this is the endpoint whose failures are otherwise invisible.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("prose")]
    [InlineData("1\n00:00:05,000 --> 00:00:02,000\nHi\n")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\n")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nHi\0\n")]
    public void EveryRefusalCarriesAMessage(string text)
    {
        var result = SrtValidator.Validate(text);

        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Fact]
    public void ARefusalReportsTheOffendingLineNumber()
    {
        var result = SrtValidator.Validate("1\n00:00:01,000 --> 00:00:02,000\nHi\n\n2\nnot a timing line\nHi\n");

        Assert.False(result.IsValid);
        Assert.Contains("Line 6", result.ErrorMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The message is read out of a log by an administrator. Echoing the input
    /// back at them is how a subtitle turns into a log-injection vector.
    /// </summary>
    [Fact]
    public void ARefusalDoesNotEchoTheRejectedContent()
    {
        const string Secret = "SUPERCALIFRAGILISTIC";
        var result = SrtValidator.Validate("1\n" + Secret + "\nHi\n");

        Assert.False(result.IsValid);
        Assert.DoesNotContain(Secret, result.ErrorMessage!, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Normalisation of what is written
    // -----------------------------------------------------------------------

    [Fact]
    public void NormalisedTextUsesCrlfAndEndsWithASingleNewline()
    {
        var result = SrtValidator.Validate("1\n00:00:01,000 --> 00:00:02,000\nHello\n\n\n\n");

        Assert.True(result.IsValid);
        Assert.Equal("1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n", result.NormalisedText);
    }

    [Fact]
    public void NormalisedTextPreservesTheCueBodyByteForByte()
    {
        var result = SrtValidator.Validate("1\n00:00:01,000 --> 00:00:02,000\n<i>Ünïcödé</i>\nline 2\n");

        Assert.True(result.IsValid);
        Assert.Contains("<i>Ünïcödé</i>\r\nline 2", result.NormalisedText, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalisedTextIsEmptyForARefusal()
    {
        Assert.Equal(string.Empty, SrtValidator.Validate("nope").NormalisedText);
    }
}
