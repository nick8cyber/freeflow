using System.Text;
using System.Text.RegularExpressions;

namespace FreeFlow.Core;

public sealed record TranscriptCommandParsing(string Transcript, bool ShouldPressEnterAfterPaste);

public static class TextUtil
{
    private static readonly Regex TrailingPressEnterCommand = new(
        @"(?:^|[ \t\r\n,;:\-]+)press[ \t\r\n]+enter[\s\p{P}]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    /// <summary>Port of AppState.parseTranscriptCommands: strips a trailing "press enter" command.</summary>
    public static TranscriptCommandParsing ParseTranscriptCommands(string transcript, bool pressEnterEnabled)
    {
        if (!pressEnterEnabled)
            return new TranscriptCommandParsing(transcript.Trim(), false);

        var match = TrailingPressEnterCommand.Match(transcript);
        if (!match.Success)
            return new TranscriptCommandParsing(transcript.Trim(), false);

        var stripped = transcript.Remove(match.Index, match.Length);
        return new TranscriptCommandParsing(stripped.Trim(), true);
    }

    /// <summary>Port of AppState.normalize: lowercase, punctuation stripped, trimmed.
    /// Foundation's punctuationCharacters includes the ASCII symbol run, so those go too.</summary>
    public static string NormalizeForMacro(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            var isPunctuationLike = char.IsPunctuation(ch) || "$+<=>^`|~".Contains(ch);
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || !isPunctuationLike)
                sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Port of the custom-vocabulary parsing: split on newlines/commas/semicolons, dedupe case-insensitively.</summary>
    public static List<string> ParseVocabulary(string raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        // Swift splits on \n , ; only; TrimEntries removes the \r of CRLF lines.
        foreach (var part in raw.Split(['\n', ',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(part)) result.Add(part);
        }
        return result;
    }

    /// <summary>Final pasted text gets a trailing space when it ends with sentence punctuation.</summary>
    public static string WithTrailingSpace(string text) =>
        text.Length > 0 && ".!?".Contains(text[^1]) ? text + " " : text;

    /// <summary>Port of AppContextService.normalizedActivitySummary: split on ./。/!/? and
    /// rejoin the first two sentences with ". " when there are more than two.</summary>
    public static string FirstTwoSentences(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return trimmed;

        var sentences = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in trimmed)
        {
            if (ch is '.' or '。' or '!' or '?')
            {
                var part = sb.ToString().Trim();
                sb.Clear();
                if (part.Length > 0) sentences.Add(part);
            }
            else
            {
                sb.Append(ch);
            }
        }
        var tail = sb.ToString().Trim();
        if (tail.Length > 0) sentences.Add(tail);

        if (sentences.Count <= 2) return trimmed;
        return sentences[0] + ". " + sentences[1] + ".";
    }
}
