using FreeFlow.UI;
using Xunit;

namespace FreeFlow.Tests;

/// <summary>
/// Regression tests for the overlay transcript fitter. The original binary
/// search ran the wrong way through a monotone predicate and collapsed a long
/// transcript to an ellipsis plus its final character; these pin the intended
/// "longest visible suffix" behavior with real GDI measurements.
/// </summary>
public class OverlayTextFitTests
{
    // 20px em = the 150%-monitor transcript size, where the two-line block is tightest.
    private static readonly Font TranscriptFont = new("Segoe UI", 20, GraphicsUnit.Pixel);
    private const string DemoTranscript =
        "The quick brown fox jumps over the lazy dog. " +
        "Pack my box with five dozen liquor jugs. Sphinx of black quartz, judge my vow.";

    [Fact]
    public void ShortTranscript_ReturnedUnchanged()
    {
        var rect = new Rectangle(87, 13, 499, 69);
        var fitted = OverlayTextFit.FitTail("hello world", rect, TranscriptFont);
        Assert.Equal("hello world", fitted);
    }

    [Fact]
    public void LongTranscript_KeepsALongVisibleTail_NotJustTheLastCharacter()
    {
        // The old bug returned "…<last char>" (length 2) for this exact input.
        var rect = new Rectangle(87, 13, 499, 69);
        var fitted = OverlayTextFit.FitTail(DemoTranscript, rect, TranscriptFont);

        Assert.StartsWith("…", fitted);
        Assert.True(fitted.Length > 60,
            $"expected a long visible tail, got {fitted.Length} chars: '{fitted}'");
    }

    [Fact]
    public void LongTranscript_FittedResult_FitsTheRect()
    {
        var rect = new Rectangle(87, 13, 499, 69);
        var fitted = OverlayTextFit.FitTail(DemoTranscript, rect, TranscriptFont);
        Assert.True(OverlayTextFit.MeasuredHeight(fitted, rect, TranscriptFont) <= rect.Height);
    }

    [Fact]
    public void LongTranscript_FittedSuffix_IsTheLongestThatFits()
    {
        // One character longer of a tail must NOT fit — otherwise the search
        // stopped early and the card hides more dictated text than needed.
        var rect = new Rectangle(87, 13, 499, 69);
        var fitted = OverlayTextFit.FitTail(DemoTranscript, rect, TranscriptFont);
        Assert.StartsWith("…", fitted);
        var suffixStart = DemoTranscript.Length - (fitted.Length - 1);
        Assert.True(suffixStart > 1, "fitted result must be a trimmed suffix");
        var oneCharacterLongerTail = "…" + DemoTranscript[(suffixStart - 1)..];
        Assert.True(OverlayTextFit.MeasuredHeight(oneCharacterLongerTail, rect, TranscriptFont) > rect.Height,
            "a one-character-longer tail must overflow — otherwise the fit is not maximal");
    }

    [Fact]
    public void TinyRect_ReturnsTheShortestFallback_WithoutThrowing()
    {
        var rect = new Rectangle(0, 0, 4, 4);
        var fitted = OverlayTextFit.FitTail(DemoTranscript, rect, TranscriptFont);
        Assert.StartsWith("…", fitted);
        Assert.True(fitted.Length >= 2);
    }
}
