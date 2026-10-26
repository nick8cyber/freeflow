using FreeFlow.Core;
using Xunit;

namespace FreeFlow.Tests;

public class SystemPromptBuilderTests
{
    [Fact]
    public void CleanupPrompt_AppendsLanguageClauseBeforeVocabulary()
    {
        var prompt = SystemPromptBuilder.BuildCleanupPrompt(
            Prompts.DefaultSystemPrompt, "FreeFlow, Kubernetes", "Russian");

        var languageIndex = prompt.IndexOf(
            "IMPORTANT: Translate the final cleaned text into Russian.", StringComparison.Ordinal);
        var vocabularyIndex = prompt.IndexOf(
            "The following vocabulary must be treated as high-priority terms", StringComparison.Ordinal);

        Assert.True(languageIndex >= 0);
        Assert.True(vocabularyIndex > languageIndex);
    }

    [Fact]
    public void CleanupPrompt_WithoutExtras_MatchesBasePrompt()
    {
        var prompt = SystemPromptBuilder.BuildCleanupPrompt(Prompts.DefaultSystemPrompt, "", "");
        Assert.Equal(Prompts.DefaultSystemPrompt, prompt);
    }

    [Fact]
    public void CommandModePrompt_ReplacesLanguageLineWhenOutputLanguageSet()
    {
        var prompt = SystemPromptBuilder.BuildCommandModePrompt("Russian", "");
        Assert.DoesNotContain(
            "- Preserve the original language unless VOICE_COMMAND explicitly requests translation.",
            prompt);
        Assert.Contains("- Output the result in Russian.", prompt);
    }

    [Fact]
    public void CommandModePrompt_KeepsLanguageLineWithoutOutputLanguage()
    {
        var prompt = SystemPromptBuilder.BuildCommandModePrompt("", "");
        Assert.Contains(
            "- Preserve the original language unless VOICE_COMMAND explicitly requests translation.",
            prompt);
        Assert.DoesNotContain("- Output the result in", prompt);
    }

    [Fact]
    public void CommandModePrompt_AppendsVocabulary()
    {
        var prompt = SystemPromptBuilder.BuildCommandModePrompt("", "FreeFlow");
        Assert.Contains("Use these spellings exactly in the output when relevant:\nFreeFlow", prompt);
    }
}

public class CooldownStoreTests
{
    [Fact]
    public void ReadWriteClear_RoundTrip()
    {
        var key = "test:" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(CooldownStore.Read(key));
            var expiry = DateTimeOffset.UtcNow.AddHours(2);
            CooldownStore.Write(key, expiry);
            var read = CooldownStore.Read(key);
            Assert.NotNull(read);
            Assert.Equal(expiry.ToUnixTimeSeconds(), read!.Value.ToUnixTimeSeconds());
            CooldownStore.Clear(key);
            Assert.Null(CooldownStore.Read(key));
        }
        finally
        {
            CooldownStore.Clear(key);
        }
    }
}

public class FirstTwoSentencesTests
{
    [Fact]
    public void RejoinsFirstTwoSentencesWithPeriodSpace()
    {
        // Swift joins the first two with ". " + ".", dropping the original terminators.
        Assert.Equal(
            "First. Second.",
            TextUtil.FirstTwoSentences("First! Second? Third. Fourth."));
    }

    [Fact]
    public void TwoOrFewerSentences_ReturnsOriginal()
    {
        Assert.Equal("One? Two!", TextUtil.FirstTwoSentences("One? Two!"));
        Assert.Equal("No terminator here", TextUtil.FirstTwoSentences("No terminator here"));
    }

    [Fact]
    public void HandlesChinesePeriod()
    {
        Assert.Equal("第一句. 第二句.", TextUtil.FirstTwoSentences("第一句。第二句。第三句。第四。"));
    }
}
