using System.Text;
using System.Text.RegularExpressions;

namespace FreeFlow.Core;

public enum TranscriptParseError { InvalidResponse }

/// <summary>Port of TranscriptionResponseParser from the macOS sources.</summary>
public static class TranscriptionResponseParser
{
    // Whisper can emit these stock phrases for silence or background noise.
    // Only suppress them when segment metadata independently reports a high
    // probability of no speech, which protects genuine short dictations.
    private static readonly HashSet<string> HallucinationPhrases = new(StringComparer.Ordinal)
    {
        "thank you",
        "thank you for watching",
        "thank you very much",
        "thank you so much",
        "thanks for watching",
        "please subscribe",
        "like and subscribe",
        "subtitles by",
        "subtitles by the amara.org community",
        "you"
    };

    private const double HallucinationNoSpeechThreshold = 0.1;

    /// <summary>Parses a transcription response body. Throws TranscriptParseException on invalid responses.</summary>
    public static string Parse(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body, JsonOpts.DocumentOptions);
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                root.TryGetProperty("text", out var textEl) &&
                textEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var text = textEl.GetString() ?? "";
                if (IsHallucination(text, root)) return "";
                return text;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON — fall through to the plain-text path.
        }

        // Port of components(separatedBy: .newlines): split on all Unicode newline
        // characters and keep empty components (they join into interior spaces).
        var plainText = string.Join(" ", SplitOnNewlines(body)).Trim();
        if (plainText.Length == 0) throw new TranscriptParseException(TranscriptParseError.InvalidResponse);
        return plainText;
    }

    private static string[] SplitOnNewlines(string body)
    {
        var parts = body.Split(['\r', '\n', '\v', '\f', '\u0085', '\u2028', '\u2029']);
        return parts;
    }

    private static bool IsHallucination(string text, System.Text.Json.JsonElement json)
    {
        // Port of trimmingCharacters(in: punctuation ∪ whitespace): all Unicode
        // punctuation/symbols plus whitespace, so curly quotes and dashes also strip.
        var start = 0;
        var end = text.Length;
        while (start < end && IsPunctuationOrWhitespace(text[start])) start++;
        while (end > start && IsPunctuationOrWhitespace(text[end - 1])) end--;
        var normalized = text.Substring(start, end - start).ToLowerInvariant();
        if (!HallucinationPhrases.Contains(normalized)) return false;

        if (!json.TryGetProperty("segments", out var segments) ||
            segments.ValueKind != System.Text.Json.JsonValueKind.Array ||
            segments.GetArrayLength() == 0)
        {
            // Provider response has no segments/no_speech metadata: keep the text.
            return false;
        }

        var first = segments[0];
        if (!first.TryGetProperty("no_speech_prob", out var probEl) ||
            probEl.ValueKind != System.Text.Json.JsonValueKind.Number)
        {
            return false;
        }

        return probEl.GetDouble() >= HallucinationNoSpeechThreshold;
    }

    // Mirrors CharacterSet.punctuationCharacters ∪ whitespacesAndNewlines: all
    // Unicode punctuation and whitespace plus the ASCII symbol run Foundation adds
    // ($+<=>^`|~) — but NOT the full Unicode S* category (©, €, … stay).
    private static bool IsPunctuationOrWhitespace(char ch) =>
        char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || "$+<=>^`|~".Contains(ch);
}

public sealed class TranscriptParseException(TranscriptParseError error)
    : Exception(error.ToString())
{
    public TranscriptParseError Error { get; } = error;
}

/// <summary>Port of TranscriptOutputSanitizer from the macOS sources.</summary>
public static class TranscriptOutputSanitizer
{
    public static string VerbatimTranslation(string value)
    {
        var result = value.Trim();
        if (result.Length == 0) return "";
        if (result.Length > 1 && result[0] == '"' && result[^1] == '"')
        {
            result = result[1..^1].Trim();
        }
        return result;
    }

    public static string PostProcessedTranscript(string value)
    {
        var result = value.Trim();
        if (result.Length == 0) return "";
        if (result.Length > 1 && result[0] == '"' && result[^1] == '"')
        {
            result = result[1..^1].Trim();
        }
        if (result == "EMPTY") return "";
        return result;
    }

    public static string CommandModeTranscript(string value) => value.Trim();

    public static bool AppearsToHaveExecutedInstruction(
        string rawTranscript, string cleanedTranscript, string outputLanguage)
    {
        if (!string.IsNullOrWhiteSpace(outputLanguage)) return false;

        var rawTokens = SignificantTokens(rawTranscript);
        var cleanedTokens = SignificantTokens(cleanedTranscript);
        if (rawTokens.Count == 0 || cleanedTokens.Count == 0) return false;

        var instructionMarkers = new HashSet<string>(StringComparer.Ordinal)
        {
            "ask", "answer", "compose", "create", "draft", "email", "generate", "make",
            "message", "prompt", "reply", "respond", "response", "summarize", "tell",
            "translate", "write", "claude", "chatgpt", "ai", "llm"
        };
        var rawMarkers = rawTokens.Intersect(instructionMarkers).ToHashSet();
        if (rawMarkers.Count == 0) return false;

        var preservedMarkers = rawMarkers.Intersect(cleanedTokens).ToHashSet();
        var overlap = rawTokens.Intersect(cleanedTokens).ToHashSet();
        var overlapRatio = (double)overlap.Count / Math.Max(rawTokens.Count, 1);

        var assistantPreamble = new Regex(
            @"^\s*(sure|certainly|absolutely|here(?:'s| is)|i(?:'d| would) be happy to|i can)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        var cleanedHasPreamble = assistantPreamble.IsMatch(cleanedTranscript);
        var rawHasSamePreamble = assistantPreamble.IsMatch(rawTranscript);

        return (cleanedHasPreamble && !rawHasSamePreamble)
               || (preservedMarkers.Count == 0 && overlapRatio < 0.35);
    }

    private static HashSet<string> SignificantTokens(string text)
    {
        var stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "a", "an", "and", "are", "as", "at", "be", "but", "by", "can", "could",
            "for", "from", "had", "has", "have", "he", "her", "him", "his", "i", "if",
            "in", "into", "is", "it", "its", "just", "me", "my", "of", "on", "or", "our",
            "please", "she", "so", "that", "the", "their", "them", "then", "there", "this",
            "to", "um", "uh", "was", "we", "were", "what", "when", "where", "who", "with",
            "would", "you", "your"
        };

        var result = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else
            {
                Flush(sb, result, stopWords);
            }
        }
        Flush(sb, result, stopWords);
        return result;
    }

    private static void Flush(StringBuilder sb, HashSet<string> into, HashSet<string> stopWords)
    {
        if (sb.Length == 0) return;
        var token = sb.ToString();
        sb.Clear();
        if (token.Length > 1 && !stopWords.Contains(token)) into.Add(token);
    }
}

public static class ThinkTagStripper
{
    /// <summary>Port of the qwen think-tag stripping: removes closed and unclosed leading think blocks.</summary>
    public static string Strip(string text)
    {
        // Match timeouts: provider output is untrusted; these regexes have no
        // catastrophic blowup but the timeout keeps a pathological input bounded.
        var timeout = TimeSpan.FromSeconds(2);
        var stripped = Regex.Replace(
            text, @"^(?:\s*<think>[\s\S]*?</think>)+", "",
            RegexOptions.CultureInvariant, timeout);
        stripped = Regex.Replace(
            stripped, @"^\s*<think>[\s\S]*$", "",
            RegexOptions.CultureInvariant, timeout);
        return stripped.Trim();
    }
}
