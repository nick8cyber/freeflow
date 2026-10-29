using System.IO;
using FreeFlow.Core;

namespace FreeFlow.Services;

public sealed record TranscriptionOutcome(string Text, string Provider, string Model);

public sealed record LlmAttemptResult(string? Text, string? ErrorDescription, string ProviderUsed);

/// <summary>
/// Routes requests to the primary provider (Gemini) and falls back to the secondary
/// provider (Groq). Integrates per-model rate-limit cooldowns; a model that is cooling
/// down is skipped without paying a doomed round-trip.
/// </summary>
public sealed class ProviderRouter
{
    private readonly Func<AppSettings> _settings;

    public CooldownManager Cooldowns { get; }

    public ProviderRouter(Func<AppSettings> settings)
    {
        _settings = settings;
        Cooldowns = new CooldownManager(
            readPersisted: CooldownStore.Read,
            persist: CooldownStore.Write,
            clearPersisted: CooldownStore.Clear);
    }

    private GeminiClient? Gemini
    {
        get
        {
            var s = _settings();
            return string.IsNullOrWhiteSpace(s.GeminiApiKey)
                ? null
                : new GeminiClient(s.GeminiBaseUrl, s.GeminiApiKey);
        }
    }

    private GroqClient? Groq
    {
        get
        {
            var s = _settings();
            return string.IsNullOrWhiteSpace(s.GroqApiKey)
                ? null
                : new GroqClient(s.GroqBaseUrl, s.GroqApiKey);
        }
    }

    // ------------------------------------------------------------------
    // Transcription: Gemini primary → Groq fallback.
    // ------------------------------------------------------------------

    /// <summary>Total transcription attempts per provider: the initial request plus
    /// two retries (user policy — a failed dictation is always worth repeating on
    /// the same provider; a manual repeat has resolved every transient failure:
    /// cold upstream after idle, dropped pooled connection, truncated response).</summary>
    private const int TranscribeAttempts = 3;

    /// <summary>Errors a repeat can actually fix. Deterministic failures are
    /// retried nowhere: cancellations, rate limits (the cooldown + fallback chain
    /// owns those) and HTTP 4xx client errors (bad key, wrong URL) fail fast so
    /// quota is not burned on guaranteed-identical repeats.</summary>
    private static bool ShouldRetrySameProvider(ProviderException ex) =>
        ex.Kind is ProviderFailureKind.EmptyOutput
            or ProviderFailureKind.Network
            or ProviderFailureKind.Timeout
            or ProviderFailureKind.InvalidResponse
        || (ex.Kind == ProviderFailureKind.Http && (int?)ex.StatusCode >= 500);

    public async Task<TranscriptionOutcome> TranscribeAsync(string wavPath, CancellationToken ct)
    {
        var s = _settings();
        ProviderException? lastError = null;

        // Cooldowns deliberately do NOT gate transcription (Swift parity: the
        // transcription service has no cooldown logic; a 429 here must neither skip
        // the provider nor poison the LLM cooldown state — the fallback already
        // absorbs the failure).
        var gemini = Gemini;
        if (gemini != null)
        {
            var model = s.TranscriptionModelGemini;
            try
            {
                var wavBytes = await File.ReadAllBytesAsync(wavPath, ct);
                // Custom vocabulary is passed natively to the transcription model
                // (audioTranscriptionConfig.customVocabulary), capped at 100 entries.
                var vocabulary = TextUtil.ParseVocabulary(s.CustomVocabulary).Take(100).ToArray();
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        var text = await gemini.TranscribeAsync(
                            wavBytes, model, s.TranscriptionLanguage, vocabulary, s.TranscriptionTimeoutSeconds, ct);
                        return new TranscriptionOutcome(text, "Gemini", model);
                    }
                    catch (ProviderException ex) when (
                        attempt < TranscribeAttempts && ShouldRetrySameProvider(ex))
                    {
                        lastError = ex;
                    }
                }
            }
            catch (ProviderException ex) when (ex.Kind != ProviderFailureKind.Cancelled)
            {
                lastError = ex;
            }
        }

        var groq = Groq;
        if (groq != null)
        {
            var model = s.TranscriptionModelGroq;
            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        var text = await groq.TranscribeAsync(
                            wavPath, model, s.TranscriptionLanguage, s.TranscriptionTimeoutSeconds, ct);
                        return new TranscriptionOutcome(text, "Groq", model);
                    }
                    catch (ProviderException ex) when (
                        attempt < TranscribeAttempts && ShouldRetrySameProvider(ex))
                    {
                        lastError = ex;
                    }
                }
            }
            catch (ProviderException ex) when (ex.Kind != ProviderFailureKind.Cancelled)
            {
                lastError = ex;
            }
        }

        if (lastError != null) throw lastError;
        // Nothing was attempted: either nothing is configured, or every model is
        // cooling down — those need different messages.
        var anyConfigured = !string.IsNullOrWhiteSpace(s.GeminiApiKey) || !string.IsNullOrWhiteSpace(s.GroqApiKey);
        throw new ProviderException(
            anyConfigured ? ProviderFailureKind.RateLimited : ProviderFailureKind.InvalidResponse,
            anyConfigured
                ? "Both transcription providers are rate-limited — wait a moment and try again."
                : "No transcription provider is configured. Add a Gemini or Groq API key in Settings.");
    }

    // ------------------------------------------------------------------
    // Text completion: Gemini primary → Groq (primary model, then its
    // configured fallback model). Returns null when every option is
    // rate-limited, so the caller can use the raw transcript instead.
    // ------------------------------------------------------------------

    /// <param name="excludeProvider">Skip the named provider ("gemini"/"groq") — used when the
    /// pipeline re-asks the other provider after a suspected instruction execution.</param>
    public async Task<LlmAttemptResult> CompleteWithFallbackAsync(
        string systemPrompt, string userMessage, CancellationToken ct, double temperature = 0.0,
        string? excludeProvider = null)
    {
        var s = _settings();
        ProviderException? lastError = null;

        // Port of the Swift fallback policy: only rate limits, empty output, and
        // suspected instruction execution justify another attempt; timeouts, network
        // errors, and other HTTP failures surface to the caller instead.
        static bool ShouldTryOtherProvider(ProviderException ex) =>
            ex.IsRateLimited ||
            ex.Kind == ProviderFailureKind.EmptyOutput ||
            ex.Kind == ProviderFailureKind.InstructionExecutionSuspected;

        var gemini = excludeProvider == "gemini" ? null : Gemini;
        if (gemini != null)
        {
            var model = s.PostProcessingModelGemini;
            if (!Cooldowns.IsInCooldown(GeminiKey(model)))
            {
                try
                {
                    var text = await gemini.CompleteAsync(
                        systemPrompt, userMessage, model, s.PostProcessingTimeoutSeconds, ct, temperature);
                    return new LlmAttemptResult(text, null, $"Gemini ({model})");
                }
                catch (ProviderException ex) when (ex.Kind != ProviderFailureKind.Cancelled)
                {
                    lastError = ex;
                    if (ex.IsRateLimited) RegisterCooldown(GeminiKey(model), ex.Headers);
                    if (!ShouldTryOtherProvider(ex))
                    {
                        return new LlmAttemptResult(null, FriendlyErrors.Describe(ex), "none");
                    }
                }
            }
        }

        var groq = excludeProvider == "groq" ? null : Groq;
        if (groq != null)
        {
            var primary = s.PostProcessingModelGroq;
            var fallback = s.PostProcessingFallbackModelGroq;
            var chosen = Cooldowns.EffectivePrimary(GroqKey(primary), string.IsNullOrWhiteSpace(fallback) ? null : GroqKey(fallback));
            if (chosen != null)
            {
                var chosenModel = chosen == GroqKey(primary) ? primary : fallback;
                try
                {
                    var text = await groq.ChatAsync(
                        systemPrompt, userMessage, chosenModel, s.PostProcessingTimeoutSeconds, ct);
                    return new LlmAttemptResult(text, null, $"Groq ({chosenModel})");
                }
                catch (ProviderException ex) when (ex.Kind != ProviderFailureKind.Cancelled)
                {
                    lastError = ex;
                    if (ex.IsRateLimited || ex.Kind == ProviderFailureKind.EmptyOutput)
                    {
                        if (ex.IsRateLimited) RegisterCooldown(GroqKey(chosenModel), ex.Headers);
                        // Retry the other Groq model if it is available (port of processWithFallback).
                        var other = chosenModel == primary ? fallback : primary;
                        if (!string.IsNullOrWhiteSpace(other) && !Cooldowns.IsInCooldown(GroqKey(other)))
                        {
                            try
                            {
                                var retry = await groq.ChatAsync(
                                    systemPrompt, userMessage, other, s.PostProcessingTimeoutSeconds, ct);
                                return new LlmAttemptResult(retry, null, $"Groq ({other})");
                            }
                            catch (ProviderException ex2) when (ex2.Kind != ProviderFailureKind.Cancelled)
                            {
                                lastError = ex2;
                                if (ex2.IsRateLimited) RegisterCooldown(GroqKey(other), ex2.Headers);
                                if (!ShouldTryOtherProvider(ex2))
                                {
                                    return new LlmAttemptResult(null, FriendlyErrors.Describe(ex2), "none");
                                }
                            }
                        }
                        return new LlmAttemptResult(null, FriendlyErrors.Describe(lastError), "none");
                    }
                }
            }
        }

        return new LlmAttemptResult(null, lastError is null ? "All providers are rate-limited" : FriendlyErrors.Describe(lastError), "none");
    }

    /// <summary>Two-sentence activity summary for context-aware cleanup. Null on failure.</summary>
    public async Task<string?> SummarizeContextAsync(
        string metadata, string? jpegBase64, CancellationToken ct)
    {
        var s = _settings();
        string? summary = null;

        // Attempt ladder (Swift parity): screenshot+vision first, then a text-only
        // retry on the same model, then — when there is no Gemini key at all — the
        // OpenAI-compatible fallback provider, so Groq-only setups keep context.
        var gemini = Gemini;
        if (gemini != null && jpegBase64 != null)
        {
            try
            {
                summary = await gemini.CompleteWithImageAsync(
                    s.ContextPrompt, Prompts.ContextScreenshotUserText, metadata, jpegBase64,
                    s.ContextModelGemini, s.ContextTimeoutSeconds, ct);
            }
            catch (ProviderException) { }
        }
        if (summary == null && gemini != null)
        {
            try
            {
                summary = await gemini.CompleteAsync(
                    s.ContextPrompt, Prompts.ContextUserText + metadata, s.ContextModelGemini,
                    s.ContextTimeoutSeconds, ct, temperature: 0.2);
            }
            catch (ProviderException) { }
        }
        if (summary == null)
        {
            var groq = Groq;
            if (groq != null)
            {
                try
                {
                    summary = await groq.ChatAsync(
                        s.ContextPrompt, Prompts.ContextUserText + metadata,
                        s.PostProcessingModelGroq, s.ContextTimeoutSeconds, ct);
                }
                catch (ProviderException) { }
            }
        }

        return summary == null
            ? null
            : TextUtil.FirstTwoSentences(ThinkTagStripper.Strip(summary));
    }

    private void RegisterCooldown(string key, System.Net.Http.Headers.HttpResponseHeaders? headers)
    {
        if (headers == null)
        {
            Cooldowns.SetCooldown(key, 60);
            return;
        }
        var (seconds, isDaily) = CooldownManager.RateLimitCooldown(headers);
        Cooldowns.SetCooldown(key, seconds, isDaily);
    }

    private static string GeminiKey(string model) => $"gemini:{model}";
    private static string GroqKey(string model) => $"groq:{model}";
}
