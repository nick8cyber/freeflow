using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text;
using System.Text.Json;
using FreeFlow.Core;

namespace FreeFlow.Services;

public sealed record GeminiResult(string Text, string Model);

/// <summary>
/// Google Gemini client (primary provider). Audio transcription and text completion
/// both go through models/{model}:generateContent with inline base64 payloads.
/// </summary>
public sealed class GeminiClient(string baseUrl, string apiKey)
{
    private const string AudioMimeType = "audio/wav";

    private string Endpoint(string model) =>
        $"{baseUrl.TrimEnd('/')}/models/{model}:generateContent";

    public async Task<string> TranscribeAsync(
        byte[] wavBytes, string model, string language, string[] vocabulary, int timeoutSeconds, CancellationToken ct)
    {
        // The dedicated speech-recognition family takes audio-only requests with
        // audioTranscriptionConfig; generic chat models keep the prompt-based path.
        if (IsTranscribeFamily(model))
        {
            return await PostAsync(model, BuildTranscribeBody(wavBytes, language, vocabulary), timeoutSeconds, ct);
        }

        var parts = new JsonArrayBuilder();
        parts.AddText(Prompts.TranscriptionInstruction(string.IsNullOrWhiteSpace(language) ? null : language));
        parts.AddInlineData(AudioMimeType, Convert.ToBase64String(wavBytes));
        var body = BuildBody(systemPrompt: null, parts.Build(), thinkingBudget: 0);
        return await PostAsync(model, body, timeoutSeconds, ct);
    }

    /// <summary>True for the Gemini 3.5 Transcribe family (including -live variants).</summary>
    public static bool IsTranscribeFamily(string model) =>
        model.StartsWith("gemini-3.5-transcribe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Official REST shape for the transcribe family: contents carry inline audio
    /// ONLY (no text prompt, no system_instruction, no thinkingConfig), and the
    /// language/vocabulary options live in generationConfig.audioTranscriptionConfig.
    /// </summary>
    private static string BuildTranscribeBody(byte[] wavBytes, string language, string[] vocabulary)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("contents");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WritePropertyName("parts");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WritePropertyName("inline_data");
            writer.WriteStartObject();
            writer.WriteString("mime_type", AudioMimeType);
            writer.WriteString("data", Convert.ToBase64String(wavBytes));
            writer.WriteEndObject(); // inline_data
            writer.WriteEndObject(); // part
            writer.WriteEndArray();  // parts
            writer.WriteEndObject(); // content
            writer.WriteEndArray();  // contents
            writer.WritePropertyName("generationConfig");
            writer.WriteStartObject();
            writer.WritePropertyName("audioTranscriptionConfig");
            writer.WriteStartObject();
            if (!string.IsNullOrWhiteSpace(language))
            {
                writer.WritePropertyName("languageCodes");
                writer.WriteStartArray();
                writer.WriteStringValue(language);
                writer.WriteEndArray();
            }
            if (vocabulary is { Length: > 0 })
            {
                writer.WritePropertyName("customVocabulary");
                writer.WriteStartArray();
                foreach (var term in vocabulary.Take(100)) writer.WriteStringValue(term);
                writer.WriteEndArray();
            }
            writer.WriteEndObject(); // audioTranscriptionConfig
            writer.WriteEndObject(); // generationConfig
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public async Task<string> CompleteAsync(
        string? systemPrompt, string userMessage, string model, int timeoutSeconds, CancellationToken ct,
        double temperature = 0.0, int maxOutputTokens = 4096)
    {
        var parts = new JsonArrayBuilder();
        parts.AddText(userMessage);
        var body = BuildBody(systemPrompt, parts.Build(), thinkingBudget: 0, temperature, maxOutputTokens);
        return await PostAsync(model, body, timeoutSeconds, ct);
    }

    public async Task<string> CompleteWithImageAsync(
        string? systemPrompt, string screenshotPrompt, string metadataText,
        string jpegBase64, string model, int timeoutSeconds, CancellationToken ct)
    {
        var parts = new JsonArrayBuilder();
        parts.AddText(screenshotPrompt);
        parts.AddText(metadataText);
        parts.AddInlineData("image/jpeg", jpegBase64);
        var body = BuildBody(systemPrompt, parts.Build(), thinkingBudget: 0, temperature: 0.2);
        return await PostAsync(model, body, timeoutSeconds, ct);
    }

    /// <summary>Validates the key with GET /models (10 s), mirroring the macOS key check.</summary>
    public async Task<bool> ValidateKeyAsync(CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/models";
        var response = await HttpTransport.SendWithTimeoutAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("x-goog-api-key", apiKey);
                return request;
            },
            content: null!,
            timeoutSeconds: 10,
            ct);
        if ((int)response.StatusCode is >= 200 and < 300) return true;
        throw HttpTransport.HttpError(response.StatusCode, response.Headers, HttpTransport.HostOf(baseUrl));
    }

    private static string BuildBody(
        string? systemPrompt, JsonElement userParts, int thinkingBudget,
        double temperature = 0.0, int maxOutputTokens = 8192)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                writer.WritePropertyName("system_instruction");
                writer.WriteStartObject();
                writer.WritePropertyName("parts");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("text", systemPrompt);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WritePropertyName("contents");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WritePropertyName("parts");
            userParts.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndArray();
            // NOTE: thinkingConfig is intentionally NOT sent — the 3.5 generation
            // rejects thinkingBudget:0 with HTTP 400 "invalid argument", and the 2.5
            // models work fine on their default thinking settings.
            writer.WritePropertyName("generationConfig");
            writer.WriteStartObject();
            writer.WriteNumber("temperature", temperature);
            writer.WriteNumber("maxOutputTokens", maxOutputTokens);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        var body = Encoding.UTF8.GetString(stream.ToArray());
        // A malformed body only ever surfaces as an opaque Google 400 ("Invalid JSON
        // payload…") — validate locally so a construction bug fails loudly at the source.
        using (var check = JsonDocument.Parse(body))
        {
            if (check.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Gemini request body is not a JSON object.");
        }
        return body;
    }

    private async Task<string> PostAsync(string model, string body, int timeoutSeconds, CancellationToken ct)
    {
        var response = await HttpTransport.SendWithTimeoutAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(model));
                request.Headers.Add("x-goog-api-key", apiKey);
                return request;
            },
            HttpTransport.JsonContent(body),
            timeoutSeconds,
            ct);

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if ((int)response.StatusCode is >= 200 and < 300)
            return ExtractText(responseBody, model);

        throw HttpTransport.HttpError(response.StatusCode, response.Headers, HttpTransport.HostOf(baseUrl), responseBody);
    }

    private static string ExtractText(string responseBody, string model)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody, JsonOpts.DocumentOptions);
            var root = doc.RootElement;

            if (root.TryGetProperty("promptFeedback", out var feedback) &&
                feedback.TryGetProperty("blockReason", out var block))
            {
                throw new ProviderException(
                    ProviderFailureKind.InvalidResponse,
                    $"Gemini blocked the request ({block.GetString()}).");
            }

            if (root.TryGetProperty("candidates", out var candidates) &&
                candidates.ValueKind == JsonValueKind.Array &&
                candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var textEl) &&
                            textEl.ValueKind == JsonValueKind.String)
                        {
                            sb.Append(textEl.GetString());
                        }
                        else if (part.TryGetProperty("audioTranscription", out var transcription) &&
                                 transcription.ValueKind == JsonValueKind.Object &&
                                 transcription.TryGetProperty("text", out var transcribed) &&
                                 transcribed.ValueKind == JsonValueKind.String)
                        {
                            // The 3.5 Transcribe family returns the transcript in
                            // parts[].audioTranscription.text, not parts[].text.
                            sb.Append(transcribed.GetString());
                        }
                    }
                    var text = sb.ToString();
                    if (text.Trim().Length > 0) return text;
                }

                if (candidate.TryGetProperty("finishReason", out var finish) &&
                    finish.GetString() is { } reason && reason != "STOP")
                {
                    throw new ProviderException(
                        ProviderFailureKind.InvalidResponse,
                        $"Gemini returned finishReason={reason} with no text.");
                }
            }

            throw new ProviderException(
                // EmptyOutput, not InvalidResponse: a 200 with no text is how a cold
                // upstream answers after a long idle (the retry-once policy in
                // ProviderRouter keys on this kind). Blocked/finishReason failures
                // above are deterministic and stay InvalidResponse.
                ProviderFailureKind.EmptyOutput,
                $"Gemini returned no text for model {model}.");
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new ProviderException(ProviderFailureKind.InvalidResponse, "Gemini returned invalid JSON.");
        }
    }

    /// <summary>Minimal helper for building JSON arrays of content parts.</summary>
    private sealed class JsonArrayBuilder
    {
        private readonly MemoryStream _stream = new();
        private readonly Utf8JsonWriter _writer;

        public JsonArrayBuilder() => _writer = new Utf8JsonWriter(_stream);

        public void AddText(string text)
        {
            _writer.WriteStartObject();
            _writer.WriteString("text", text);
            _writer.WriteEndObject();
        }

        public void AddInlineData(string mimeType, string base64)
        {
            _writer.WriteStartObject();
            _writer.WritePropertyName("inline_data");
            _writer.WriteStartObject();
            _writer.WriteString("mime_type", mimeType);
            _writer.WriteString("data", base64);
            _writer.WriteEndObject();
            _writer.WriteEndObject();
        }

        public JsonElement Build()
        {
            _writer.Flush();
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(_stream.ToArray()));
            return doc.RootElement.Clone();
        }
    }
}
