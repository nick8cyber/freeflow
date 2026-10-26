using FreeFlow.Services;
using Xunit;

namespace FreeFlow.Tests;

/// <summary>
/// Regression tests for the lost-beginning bug: transcribe-live's interim text
/// REPLACES the current voice-activity segment, so segments must be committed on
/// their boundaries — otherwise every segment after the first overwrites the
/// transcript and only the tail gets pasted (observed live, 2026-10-27).
/// </summary>
public class LiveTranscriptAccumulatorTests
{
    [Fact]
    public void InterimReplacesCurrentSegmentNotAccumulates()
    {
        var acc = new LiveTranscriptAccumulator();
        acc.FeedInterim("первый");
        acc.FeedInterim("первый текст");
        Assert.Equal("первый текст", acc.Current());
    }

    [Fact]
    public void EndSegmentCommitsAndNextSegmentStartsClean()
    {
        var acc = new LiveTranscriptAccumulator();
        acc.FeedInterim("первая фраза целиком");
        acc.EndSegment();
        acc.FeedInterim("вторая");
        Assert.Equal("первая фраза целиком вторая", acc.Current());
    }

    [Fact]
    public void StartSegmentClosesAnyOpenSegment()
    {
        var acc = new LiveTranscriptAccumulator();
        acc.FeedInterim("раз");
        acc.StartSegment();
        acc.FeedInterim("два");
        Assert.Equal("раз два", acc.Current());
    }

    [Fact]
    public void EmptySegmentsAreNotCommitted()
    {
        var acc = new LiveTranscriptAccumulator();
        acc.StartSegment();
        acc.EndSegment();
        acc.FeedInterim("текст");
        acc.EndSegment();
        acc.EndSegment(); // double end: idempotent
        Assert.Equal("текст", acc.Final());
    }

    [Fact]
    public void FinalIncludesOpenSegmentTail()
    {
        var acc = new LiveTranscriptAccumulator();
        acc.FeedInterim("раз");
        acc.EndSegment();
        acc.FeedInterim("два три");
        // no ACTIVITY_END before the session finishes — the open segment still counts
        Assert.Equal("раз два три", acc.Final());
    }
}
