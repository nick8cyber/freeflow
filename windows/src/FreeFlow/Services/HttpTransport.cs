using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using FreeFlow.Core;

namespace FreeFlow.Services;

/// <summary>
/// Shared HTTP plumbing for provider calls: per-request ephemeral client with the
/// request timeout applied (port of LLMAPITransport), and error mapping to ProviderException.
/// </summary>
public static class HttpTransport
{
    // One shared client for the process: per-request timeouts come from the linked CTS
    // below, so connection pooling actually works (a per-request HttpClient never reuses
    // its pool and burns a fresh connection every dictation).
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient(
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { { "User-Agent", "FreeFlow-Windows/1.0" } },
    });

    public static async Task<HttpResponseMessage> SendWithTimeoutAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpContent? content,
        int timeoutSeconds,
        CancellationToken userCancellation)
    {
        var timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : TimeSpan.FromSeconds(60);
        var client = SharedClient.Value;

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userCancellation, timeoutCts.Token);
        try
        {
            var request = requestFactory();
            request.Content = content;
            return await client.SendAsync(request, linked.Token);
        }
        catch (OperationCanceledException) when (!userCancellation.IsCancellationRequested)
        {
            throw new ProviderException(ProviderFailureKind.Timeout, "Request timed out");
        }
        catch (OperationCanceledException)
        {
            throw new ProviderException(ProviderFailureKind.Cancelled, "Cancelled");
        }
        catch (HttpRequestException)
        {
            throw new ProviderException(ProviderFailureKind.Network, "Network request failed");
        }
    }

    public static ProviderException HttpError(HttpStatusCode status, HttpResponseHeaders headers, string host, string? body = null)
    {
        var kind = status == HttpStatusCode.TooManyRequests
            ? ProviderFailureKind.RateLimited
            : ProviderFailureKind.Http;
        var message = FriendlyErrors.HttpMessage(status, host);
        if (!string.IsNullOrEmpty(body))
        {
            // The provider's own validation text (never user content) — the only way
            // to tell an invalid field from a bad model name from a blocked request.
            message += " — " + (body.Length > 1500 ? body[..1500] + "…" : body);
        }
        return new ProviderException(kind, message, status, headers, host);
    }

    public static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    public static string HostOf(string baseUrl)
    {
        try { return new Uri(baseUrl).Host; }
        catch (Exception) { return baseUrl; }
    }
}
