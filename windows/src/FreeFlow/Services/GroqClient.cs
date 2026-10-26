using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using FreeFlow.Core;

namespace FreeFlow.Services;

public sealed record GroqChatResult(string Text, string Model);

/// <summary>
/// Groq client (fallback provider), OpenAI-compatible. Ports the macOS TranscriptionService
/// multipart format and PostProcessingService chat/completions payload rules.
/// </summary>
public sealed class GroqClient(string baseUrl, string apiKey)
{
    private static readonly HashSet<string> VerboseJsonModels = new(StringComparer.OrdinalIgnoreCase)
    { "whisper-1", "whisper-large-v3", "whisper-large-v3-turbo" };

    private string TranscriptionsEndpoint => $"{baseUrl.TrimEnd('/')}/audio/transcriptions";
    private string ChatEndpoint => $"{baseUrl.TrimEnd('/')}/chat/completions";

    public async Task<string> TranscribeAsync(
        string wavPath, string model, string language, int timeoutSeconds, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(model), "model");
        var responseFormat = VerboseJsonModels.Contains(model.Trim()) ? "verbose_json" : "json";
        form.Add(new StringContent(responseFormat), "response_format");
        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language), "language");

        var fileBytes = await File.ReadAllBytesAsync(wavPath, ct);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
        form.Add(fileContent, "file", Path.GetFileName(wavPath));

        var response = await HttpTransport.SendWithTimeoutAsync(
            () => new HttpRequestMessage(HttpMethod.Post, TranscriptionsEndpoint)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", apiKey) },
            },
            form,
            timeoutSeconds,
            ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if ((int)response.StatusCode is >= 200 and < 300)
        {
            try
            {
                return TranscriptionResponseParser.Parse(body);
            }
            catch (TranscriptParseException)
            {
                throw new ProviderException(
                    ProviderFailureKind.InvalidResponse,
                    "Provider returned an invalid transcription response.");
            }
        }

        throw HttpTransport.HttpError(response.StatusCode, response.Headers, HttpTransport.HostOf(baseUrl));
    }

    public async Task<string> ChatAsync(
        string systemPrompt, string userMessage, string model, int timeoutSeconds, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["temperature"] = 0.0,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
        };

        // Port of ModelConfiguration.config(for:): only gpt-oss-20b receives the
        // completion knobs; qwen3.6 gets reasoning disabled. Aliases are normalized first.
        var trimmed = NormalizeModel(model);
        if (trimmed.Equals("openai/gpt-oss-20b", StringComparison.OrdinalIgnoreCase))
        {
            payload["max_completion_tokens"] = 4096;
            payload["reasoning_effort"] = "low";
            payload["include_reasoning"] = false;
        }
        else if (trimmed.Equals("qwen/qwen3.6-27b", StringComparison.OrdinalIgnoreCase))
        {
            payload["reasoning_effort"] = "none";
            payload["include_reasoning"] = false;
        }

        var response = await HttpTransport.SendWithTimeoutAsync(
            () => new HttpRequestMessage(HttpMethod.Post, ChatEndpoint)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", apiKey) },
            },
            HttpTransport.JsonContent(JsonSerializer.Serialize(payload, JsonOpts.Serializer)),
            timeoutSeconds,
            ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if ((int)response.StatusCode is >= 200 and < 300)
            return ParseChatResponse(body, NormalizeModel(model));

        throw HttpTransport.HttpError(response.StatusCode, response.Headers, HttpTransport.HostOf(baseUrl));
    }

    /// <summary>Port of the Swift providerless aliases (ModelConfiguration.swift:36-40).</summary>
    private static string NormalizeModel(string model)
    {
        var trimmed = model.Trim();
        return trimmed switch
        {
            "gpt-oss-20b" => "openai/gpt-oss-20b",
            "gpt-oss-120b" => "openai/gpt-oss-120b",
            "gpt-oss-safeguard-20b" => "openai/gpt-oss-safeguard-20b",
            "qwen3.6-27b" => "qwen/qwen3.6-27b",
            "qwen3-32b" => "qwen/qwen3-32b",
            _ => trimmed,
        };
    }

    private static string ParseChatResponse(string body, string model)
    {
        string? content = null;
        try
        {
            using var doc = JsonDocument.Parse(body, JsonOpts.DocumentOptions);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var contentEl) &&
                contentEl.ValueKind == JsonValueKind.String)
            {
                content = contentEl.GetString();
            }
        }
        catch (JsonException)
        {
            throw new ProviderException(ProviderFailureKind.InvalidResponse, "Provider returned invalid JSON.");
        }

        if (content is null)
            throw new ProviderException(ProviderFailureKind.InvalidResponse, "Provider returned no choices.");

        // Port of the per-model think-tag stripping (qwen3.6 and qwen3-32b).
        if (model.Equals("qwen/qwen3.6-27b", StringComparison.OrdinalIgnoreCase) ||
            model.Equals("qwen/qwen3-32b", StringComparison.OrdinalIgnoreCase))
        {
            content = ThinkTagStripper.Strip(content);
        }

        if (string.IsNullOrWhiteSpace(content))
            throw new ProviderException(ProviderFailureKind.EmptyOutput, "Provider returned no text.");

        return content;
    }

    public async Task<bool> ValidateKeyAsync(CancellationToken ct)
    {
        var response = await HttpTransport.SendWithTimeoutAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", apiKey) },
            },
            content: null!,
            timeoutSeconds: 10,
            ct);
        if ((int)response.StatusCode is >= 200 and < 300) return true;
        throw HttpTransport.HttpError(response.StatusCode, response.Headers, HttpTransport.HostOf(baseUrl));
    }
}
