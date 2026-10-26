using FreeFlow.Core;
using Xunit;

namespace FreeFlow.Tests;

public class TranscriptOutputSanitizerTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("  hello  ", "hello")]
    [InlineData("\"hello\"", "hello")]
    [InlineData("EMPTY", "")]
    [InlineData("  EMPTY  ", "")]
    [InlineData("\"EMPTY\"", "")]
    [InlineData("just say EMPTY", "just say EMPTY")]
    public void PostProcessedTranscript_NormalizesProviderOutput(string input, string expected)
    {
        Assert.Equal(expected, TranscriptOutputSanitizer.PostProcessedTranscript(input));
    }

    [Fact]
    public void VerbatimTranslation_KeepsEmptySentinel()
    {
        // "empty" can be legitimate translated speech in verbatim mode.
        Assert.Equal("EMPTY", TranscriptOutputSanitizer.VerbatimTranslation("  EMPTY  "));
        Assert.Equal("hello", TranscriptOutputSanitizer.VerbatimTranslation("\"hello\""));
    }

    [Fact]
    public void CommandModeTranscript_TrimsOnly()
    {
        Assert.Equal("make it shorter", TranscriptOutputSanitizer.CommandModeTranscript("  make it shorter\n"));
    }

    [Fact]
    public void InstructionGuard_FlagsAssistantPreamble()
    {
        const string raw = "summarize this article about the migration";
        const string cleaned = "Sure, here is a summary of the article about the migration.";
        Assert.True(TranscriptOutputSanitizer.AppearsToHaveExecutedInstruction(raw, cleaned, ""));
    }

    [Fact]
    public void InstructionGuard_KeepsGenuineDictation()
    {
        const string raw = "write a message to John saying I'm running late";
        const string cleaned = "Write a message to John saying I'm running late.";
        Assert.False(TranscriptOutputSanitizer.AppearsToHaveExecutedInstruction(raw, cleaned, ""));
    }

    [Fact]
    public void InstructionGuard_SkippedWhenOutputLanguageSet()
    {
        const string raw = "summarize this";
        const string cleaned = "Sure, here is the summary.";
        Assert.True(TranscriptOutputSanitizer.AppearsToHaveExecutedInstruction(raw, cleaned, ""));
        Assert.False(TranscriptOutputSanitizer.AppearsToHaveExecutedInstruction(raw, cleaned, "Russian"));
    }
}

public class TranscriptionResponseParserTests
{
    private const string WhisperJson =
        """{"text":"hello there","segments":[{"no_speech_prob":0.9,"start":0.0}]}""";

    [Fact]
    public void ParsesTextField()
    {
        Assert.Equal("hello there", TranscriptionResponseParser.Parse(WhisperJson));
    }

    [Fact]
    public void SuppressesHallucinationWithHighNoSpeechProbability()
    {
        var body = """{"text":"Thank you.","segments":[{"no_speech_prob":0.5}]}""";
        Assert.Equal("", TranscriptionResponseParser.Parse(body));
    }

    [Fact]
    public void KeepsHallucinationPhraseWhenSpeechWasDetected()
    {
        var body = """{"text":"Thank you.","segments":[{"no_speech_prob":0.0}]}""";
        Assert.Equal("Thank you.", TranscriptionResponseParser.Parse(body));
    }

    [Fact]
    public void KeepsHallucinationPhraseWithoutSegments()
    {
        var body = """{"text":"Thank you."}""";
        Assert.Equal("Thank you.", TranscriptionResponseParser.Parse(body));
    }

    [Fact]
    public void JoinsPlainTextLines()
    {
        Assert.Equal("line one line two", TranscriptionResponseParser.Parse("line one\nline two"));
    }

    [Fact]
    public void RejectsEmptyBody()
    {
        Assert.Throws<TranscriptParseException>(() => TranscriptionResponseParser.Parse("   \n "));
    }
}

public class ThinkTagStripperTests
{
    [Fact]
    public void StripsClosedThinkBlock()
    {
        Assert.Equal("answer", ThinkTagStripper.Strip("  <think>reasoning here</think> answer "));
    }

    [Fact]
    public void StripsUnclosedThinkBlock()
    {
        Assert.Equal("", ThinkTagStripper.Strip("<think>never closed"));
    }

    [Fact]
    public void KeepsTextWithoutThinkTags()
    {
        Assert.Equal("plain", ThinkTagStripper.Strip("plain"));
    }
}

public class CooldownDurationParserTests
{
    [Theory]
    [InlineData("2", 2.0)]
    [InlineData("7.66", 7.66)]
    [InlineData("7.66s", 7.66)]
    [InlineData("120ms", 0.12)]
    [InlineData("2m59.56s", 179.56)]
    [InlineData("1h0m0s", 3600.0)]
    [InlineData("1h2m3.5s", 3723.5)]
    public void ParsesValidDurations(string input, double expected)
    {
        Assert.Equal(expected, CooldownManager.ParseGroqDuration(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("-3")]
    [InlineData("nan")]
    [InlineData("inf")]
    [InlineData("0x10")]
    [InlineData("1h30")] // trailing number without a unit
    [InlineData("3q")] // unrecognized unit
    public void RejectsMalformedDurations(string? input)
    {
        Assert.Null(CooldownManager.ParseGroqDuration(input));
    }

    [Fact]
    public void CooldownManager_FallsBackToOtherModelWhileCooling()
    {
        var persisted = new Dictionary<string, double>();
        var manager = new CooldownManager(
            key => persisted.TryGetValue(key, out var v) ? DateTimeOffset.FromUnixTimeSeconds((long)v) : null,
            (key, expiry) => persisted[key] = expiry.ToUnixTimeSeconds(),
            key => persisted.Remove(key));

        Assert.Equal("primary", manager.EffectivePrimary("primary", "fallback"));
        manager.SetCooldown("primary", 120);
        Assert.Equal("fallback", manager.EffectivePrimary("primary", "fallback"));
        manager.SetCooldown("fallback", 120);
        Assert.Null(manager.EffectivePrimary("primary", "fallback"));
    }

    [Fact]
    public void CooldownManager_PersistsDailyLimitsOnly()
    {
        var persisted = new Dictionary<string, double>();
        var manager = new CooldownManager(
            key => persisted.TryGetValue(key, out var v) ? DateTimeOffset.FromUnixTimeSeconds((long)v) : null,
            (key, expiry) => persisted[key] = expiry.ToUnixTimeSeconds(),
            key => persisted.Remove(key));

        manager.SetCooldown("short", 60);
        Assert.Empty(persisted);

        manager.SetCooldown("daily", 86400);
        Assert.Single(persisted);
        Assert.True(manager.IsInCooldown("daily"));
    }
}
