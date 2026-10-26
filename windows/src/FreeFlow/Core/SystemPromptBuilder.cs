using System.Text;

namespace FreeFlow.Core;

/// <summary>
/// Composes the cleanup and command-mode system prompts (port of the Swift
/// PostProcessingService prompt assembly, including the command-mode language-line
/// replacement and the vocabulary/output-language clause order).
/// </summary>
public static class SystemPromptBuilder
{
    private const string CommandModeLanguageLine =
        "- Preserve the original language unless VOICE_COMMAND explicitly requests translation.";

    public static string BuildCleanupPrompt(string baseSystemPrompt, string customVocabulary, string outputLanguage)
    {
        var sb = new StringBuilder(baseSystemPrompt);
        sb.Append(OutputLanguageClause(outputLanguage));
        var vocabulary = VocabularyClause(customVocabulary);
        if (vocabulary != null) sb.Append("\n\n").Append(vocabulary);
        return sb.ToString();
    }

    public static string BuildCommandModePrompt(string outputLanguage, string customVocabulary)
    {
        var prompt = Prompts.CommandModeSystemPrompt;
        var language = outputLanguage.Trim();
        if (language.Length > 0)
        {
            prompt = prompt.Replace(
                CommandModeLanguageLine,
                $"- Output the result in {language}.",
                StringComparison.Ordinal);
        }
        var vocabulary = VocabularyClause(customVocabulary);
        if (vocabulary != null) prompt += "\n\n" + vocabulary;
        return prompt;
    }

    /// <summary>Port of the Swift clause order: output language first, vocabulary second.</summary>
    private static string OutputLanguageClause(string outputLanguage) =>
        string.IsNullOrWhiteSpace(outputLanguage) ? "" : Prompts.OutputLanguageClause(outputLanguage);

    private static string? VocabularyClause(string customVocabulary)
    {
        var terms = TextUtil.ParseVocabulary(customVocabulary);
        return terms.Count == 0 ? null : Prompts.VocabularyClause(string.Join(", ", terms));
    }
}
