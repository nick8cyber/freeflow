using System.Net;
using System.Net.Http.Headers;

namespace FreeFlow.Core;

public enum ProviderFailureKind
{
    Http,
    RateLimited,
    Network,
    Timeout,
    InvalidResponse,
    EmptyOutput,
    InstructionExecutionSuspected,
    Cancelled,
}

/// <summary>A failure from a transcription/LLM provider. StatusCode is set for Http/RateLimited kinds.</summary>
public sealed class ProviderException : Exception
{
    public ProviderFailureKind Kind { get; }
    public HttpStatusCode? StatusCode { get; }
    public HttpResponseHeaders? Headers { get; }
    public string? Host { get; }

    public ProviderException(
        ProviderFailureKind kind,
        string message,
        HttpStatusCode? statusCode = null,
        HttpResponseHeaders? headers = null,
        string? host = null) : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
        Headers = headers;
        Host = host;
    }

    public bool IsRateLimited =>
        Kind == ProviderFailureKind.RateLimited ||
        (Kind == ProviderFailureKind.Http && StatusCode == HttpStatusCode.TooManyRequests);
}

public static class FriendlyErrors
{
    /// <summary>Port of the macOS friendlyHTTPMessage strings.</summary>
    public static string HttpMessage(HttpStatusCode status, string host) => status switch
    {
        HttpStatusCode.Unauthorized => $"Invalid API key for {host}. Open Settings to fix it.",
        HttpStatusCode.Forbidden => $"Key lacks permission for this endpoint at {host} (HTTP 403). Check the key's scopes.",
        HttpStatusCode.NotFound => $"Endpoint not found at {host} (HTTP 404). Base URL is likely wrong for this provider.",
        HttpStatusCode.RequestEntityTooLarge => $"Audio file too large for {host} (HTTP 413). Try a shorter recording.",
        HttpStatusCode.BadRequest => $"Provider rejected the request (HTTP 400). Check your model name and Base URL in Settings.",
        HttpStatusCode.TooManyRequests => $"Rate limit reached at {host} (HTTP 429). Wait a moment and try again.",
        _ when (int)status >= 500 => $"Provider error at {host} (HTTP {(int)status}). Try again in a moment.",
        _ => $"Request failed at {host} (HTTP {(int)status}).",
    };

    public static string Describe(ProviderException error)
    {
        var host = string.IsNullOrEmpty(error.Host) ? "provider" : error.Host;
        return error.Kind switch
        {
            ProviderFailureKind.Network => "No internet — check connection",
            ProviderFailureKind.Timeout => "Request timed out — try again",
            ProviderFailureKind.RateLimited => HttpMessage(error.StatusCode ?? HttpStatusCode.TooManyRequests, host),
            // HttpError composes the friendly text plus the provider's own response
            // body excerpt — pass it through so the real rejection reason surfaces
            // in the tray and history instead of just the generic status line.
            ProviderFailureKind.Http when error.StatusCode.HasValue => error.Message,
        // InvalidResponse messages are authored as user-facing strings
        // (e.g. the no-provider-configured hint), so pass them through.
        ProviderFailureKind.InvalidResponse => error.Message,
            ProviderFailureKind.EmptyOutput => "Provider returned no text",
            _ => error.Message,
        };
    }
}
