using System.Globalization;
using System.Net.Http.Headers;

namespace FreeFlow.Core;

/// <summary>
/// Port of LLMCooldownManager: tracks per-model rate-limit cooldowns so subsequent
/// requests skip a rate-limited model instead of paying a doomed round-trip.
/// Minute-level cooldowns live in memory; daily-level ones are persisted to settings.
/// </summary>
public sealed class CooldownManager
{
    /// <summary>Cooldowns at or above this threshold are treated as daily limits and persisted.</summary>
    private const double DailyLimitThresholdSeconds = 3600;

    /// <summary>Fallback cooldown when a 429 carries no parseable timing header.</summary>
    private const double DefaultReprobeCooldownSeconds = 60;

    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldowns = [];
    private readonly Func<string, DateTimeOffset?> _readPersisted;
    private readonly Action<string, DateTimeOffset> _persist;
    private readonly Action<string> _clearPersisted;

    public CooldownManager(
        Func<string, DateTimeOffset?> readPersisted,
        Action<string, DateTimeOffset> persist,
        Action<string> clearPersisted)
    {
        _readPersisted = readPersisted;
        _persist = persist;
        _clearPersisted = clearPersisted;
    }

    public bool IsInCooldown(string key)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (_cooldowns.TryGetValue(key, out var until))
            {
                if (now < until) return true;
                _cooldowns.Remove(key);
            }
        }
        var persisted = _readPersisted(key);
        if (persisted.HasValue)
        {
            if (now < persisted.Value) return true;
            _clearPersisted(key);
        }
        return false;
    }

    public void SetCooldown(string key, double retryAfterSeconds, bool persist = false)
    {
        var expiry = DateTimeOffset.UtcNow.AddSeconds(retryAfterSeconds);
        if (persist || retryAfterSeconds >= DailyLimitThresholdSeconds)
        {
            _persist(key, expiry);
        }
        else
        {
            lock (_lock) _cooldowns[key] = expiry;
        }
    }

    /// <summary>Primary if not cooling, else fallback if not cooling, else null (skip the request).</summary>
    public string? EffectivePrimary(string primary, string? fallback)
    {
        if (!IsInCooldown(primary)) return primary;
        if (fallback != null && !IsInCooldown(fallback)) return fallback;
        return null;
    }

    public static string PersistKey(string key) => $"llm_cooldown_expiry_{key}";

    /// <summary>
    /// Port of rateLimitCooldown(from:): reads how long a 429 must cool down and whether it is a
    /// daily limit. Priority: exhausted daily request quota → retry-after → reset-tokens → re-probe.
    /// </summary>
    public static (double Seconds, bool IsDaily) RateLimitCooldown(HttpResponseHeaders headers)
    {
        var remainingRequests = GetHeader(headers, "x-ratelimit-remaining-requests");
        if (remainingRequests != null &&
            double.TryParse(remainingRequests, NumberStyles.Float, CultureInfo.InvariantCulture, out var remaining) &&
            remaining <= 0)
        {
            var dailyReset = ParseGroqDuration(GetHeader(headers, "x-ratelimit-reset-requests"));
            if (dailyReset.HasValue) return (dailyReset.Value, true);
        }

        var retryAfter = ParseGroqDuration(GetHeader(headers, "retry-after"));
        if (retryAfter.HasValue) return (retryAfter.Value, false);

        var resetTokens = ParseGroqDuration(GetHeader(headers, "x-ratelimit-reset-tokens"));
        if (resetTokens.HasValue) return (resetTokens.Value, false);

        return (DefaultReprobeCooldownSeconds, false);
    }

    private static string? GetHeader(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()?.Trim() is { Length: > 0 } v ? v : null
            : null;

    /// <summary>
    /// Parses a Groq duration string: bare seconds ("2", "7.66"), suffixed units ("7.66s",
    /// "120ms"), or compound forms ("1h2m3.5s"). Returns null for malformed/negative input.
    /// </summary>
    public static double? ParseGroqDuration(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0 ? seconds : null;

        double total = 0;
        var numberBuffer = new System.Text.StringBuilder();
        var matchedAnyUnit = false;
        var i = 0;
        while (i < trimmed.Length)
        {
            var ch = trimmed[i];
            if (char.IsDigit(ch) || ch == '.')
            {
                numberBuffer.Append(ch);
                i++;
                continue;
            }
            if (!double.TryParse(numberBuffer.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return null;
            numberBuffer.Clear();
            if (i + 1 < trimmed.Length && trimmed[i] == 'm' && trimmed[i + 1] == 's')
            {
                total += number / 1000.0;
                i += 2;
            }
            else if (ch == 'h')
            {
                total += number * 3600.0;
                i++;
            }
            else if (ch == 'm')
            {
                total += number * 60.0;
                i++;
            }
            else if (ch == 's')
            {
                total += number;
                i++;
            }
            else
            {
                return null;
            }
            matchedAnyUnit = true;
        }
        if (numberBuffer.Length > 0 || !matchedAnyUnit) return null;        return !double.IsNaN(total) && !double.IsInfinity(total) && total >= 0 ? total : null;
    }
}
