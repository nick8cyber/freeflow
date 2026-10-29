using System.Net;
using System.Net.Sockets;
using System.Text;
using FreeFlow.Core;
using FreeFlow.Services;
using Xunit;

namespace FreeFlow.Tests;

/// <summary>
/// Regression tests for the transcription retry policy: a failed dictation is
/// retried on the SAME provider up to two times (initial + 2 retries) for every
/// transient failure — empty result, network drop, timeout, malformed response,
/// 5xx — while deterministic failures (rate limits, HTTP 4xx) fail fast into the
/// fallback chain. Originally added for the first-dictation-after-idle failure
/// (a cold upstream answers 200 with no text; the same request seconds later
/// succeeds). Uses a local TCP stub, never a live provider.
/// </summary>
public class ProviderRetryTests
{
    /// <summary>One scripted reply. Status 0 closes the connection without any
    /// response bytes (client sees a network failure).</summary>
    public sealed record StubReply(int Status, string Body);

    /// <summary>Minimal HTTP/1.1 stub: one scripted reply per request, closes each
    /// connection so request counting stays exact.</summary>
    private sealed class HttpStub : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _acceptLoop;
        private readonly Queue<StubReply> _replies = new();
        private int _hits;
        public List<string> Paths { get; } = new();

        public HttpStub(params StubReply[] replies)
        {
            foreach (var reply in replies) _replies.Enqueue(reply);
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1beta";
        public int Hits => _hits;

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    var (request, closeWithoutReply) = await ReadRequestAsync(stream);
                    var path = request.Length > 0 ? request.Split(' ')[1] : "";
                    lock (this) { _hits++; Paths.Add(path); }
                    if (closeWithoutReply) continue;
                    var reply = _replies.Count > 0 ? _replies.Dequeue() : new StubReply(200, "{}");
                    var payload = Encoding.UTF8.GetBytes(reply.Body);
                    var head = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {reply.Status} X\r\nContent-Type: application/json\r\n" +
                        $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(payload);
                }
            }
            catch (Exception) { /* listener stopped */ }
        }

        private static async Task<(string Request, bool CloseWithoutReply)> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new StringBuilder();
            var read = new byte[8192];
            int headerEnd = -1, contentLength = 0, received = 0;
            while (true)
            {
                var n = await stream.ReadAsync(read);
                if (n <= 0) return (buffer.ToString(), true); // client vanished
                buffer.Append(Encoding.ASCII.GetString(read, 0, n));
                var text = buffer.ToString();
                if (headerEnd < 0)
                {
                    var idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        headerEnd = idx;
                        foreach (var line in text[..idx].Split("\r\n"))
                        {
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                        }
                    }
                }
                if (headerEnd >= 0)
                {
                    received = text.Length - headerEnd - 4;
                    if (received >= contentLength) break;
                }
            }
            return (buffer.ToString(), false);
        }

        public void Dispose()
        {
            _listener.Stop();
            _ = _acceptLoop;
        }
    }

    private static StubReply Ok(int status = 200) => new(status, "{}");

    private static StubReply EmptyCandidates() => new(200,
        """{"candidates":[{"finishReason":"STOP"}]}""");

    private static StubReply Transcribed(string text) => new(200,
        "{\"candidates\":[{\"content\":{\"parts\":[{\"audioTranscription\":{\"text\":\"" +
        text + "\"}}]}}]}");

    private static AppSettings GeminiSettings(string baseUrl) => new()
    {
        GeminiApiKey = "test-key",
        GeminiBaseUrl = baseUrl,
        TranscriptionModelGemini = "gemini-3.5-transcribe",
        TranscriptionTimeoutSeconds = 10,
        GroqApiKey = "",
    };

    private static AppSettings GeminiPlusGroqSettings(string geminiBaseUrl, string groqBaseUrl) => new()
    {
        GeminiApiKey = "test-key",
        GeminiBaseUrl = geminiBaseUrl,
        TranscriptionModelGemini = "gemini-3.5-transcribe",
        TranscriptionTimeoutSeconds = 10,
        GroqApiKey = "groq-key",
        GroqBaseUrl = groqBaseUrl,
        TranscriptionModelGroq = "whisper-large-v3",
    };

    private static string TempWav()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ff-retry-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, "RIFFsynthetic"u8.ToArray());
        return path;
    }

    [Fact]
    public async Task EmptyResult_RetriedTwice_ThirdAttemptWins()
    {
        using var stub = new HttpStub(EmptyCandidates(), EmptyCandidates(), Transcribed("восстановлено"));
        var router = new ProviderRouter(() => GeminiSettings(stub.BaseUrl));
        var wav = TempWav();
        try
        {
            var outcome = await router.TranscribeAsync(wav, CancellationToken.None);
            Assert.Equal("восстановлено", outcome.Text);
            Assert.Equal("Gemini", outcome.Provider);
            Assert.Equal(3, stub.Hits);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public async Task EmptyResult_ExhaustsTwoRetries_ThenFailsWithEmptyOutput()
    {
        using var stub = new HttpStub(EmptyCandidates(), EmptyCandidates(), EmptyCandidates());
        var router = new ProviderRouter(() => GeminiSettings(stub.BaseUrl));
        var wav = TempWav();
        try
        {
            var ex = await Assert.ThrowsAsync<ProviderException>(
                () => router.TranscribeAsync(wav, CancellationToken.None));
            Assert.Equal(ProviderFailureKind.EmptyOutput, ex.Kind);
            Assert.Equal(3, stub.Hits);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public async Task FirstAttemptSucceeds_NoExtraRequests()
    {
        using var stub = new HttpStub(Transcribed("ok"));
        var router = new ProviderRouter(() => GeminiSettings(stub.BaseUrl));
        var wav = TempWav();
        try
        {
            var outcome = await router.TranscribeAsync(wav, CancellationToken.None);
            Assert.Equal("ok", outcome.Text);
            Assert.Equal(1, stub.Hits);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public async Task RateLimited_FailsFast_NoRetry()
    {
        using var stub = new HttpStub(Ok(429));
        var router = new ProviderRouter(() => GeminiSettings(stub.BaseUrl));
        var wav = TempWav();
        try
        {
            var ex = await Assert.ThrowsAsync<ProviderException>(
                () => router.TranscribeAsync(wav, CancellationToken.None));
            Assert.Equal(ProviderFailureKind.RateLimited, ex.Kind);
            Assert.Equal(1, stub.Hits);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public async Task HttpClientError4xx_FailsFast_NoRetry()
    {
        using var stub = new HttpStub(Ok(400));
        var router = new ProviderRouter(() => GeminiSettings(stub.BaseUrl));
        var wav = TempWav();
        try
        {
            var ex = await Assert.ThrowsAsync<ProviderException>(
                () => router.TranscribeAsync(wav, CancellationToken.None));
            Assert.Equal(ProviderFailureKind.Http, ex.Kind);
            Assert.Equal(1, stub.Hits);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public async Task ServerError5xx_Retried()
    {
        using var stub = new HttpStub(Ok(500), Transcribed("после пятисотой"));
        var router = new ProviderRouter(() => GeminiSettings(stub.BaseUrl));
        var wav = TempWav();
        try
        {
            var outcome = await router.TranscribeAsync(wav, CancellationToken.None);
            Assert.Equal("после пятисотой", outcome.Text);
            Assert.Equal(2, stub.Hits);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public async Task ConnectionDrop_Retried()
    {
        using var stub = new HttpStub(new StubReply(0, ""), Transcribed("после обрыва"));
        var router = new ProviderRouter(() => GeminiSettings(stub.BaseUrl));
        var wav = TempWav();
        try
        {
            var outcome = await router.TranscribeAsync(wav, CancellationToken.None);
            Assert.Equal("после обрыва", outcome.Text);
            Assert.Equal(2, stub.Hits);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public async Task GeminiExhausted_GroqFallback_AlsoRetriesTransientErrors()
    {
        using var gemini = new HttpStub(EmptyCandidates(), EmptyCandidates(), EmptyCandidates());
        // Groq: an empty body parses as InvalidResponse (transient → retried),
        // then a successful {"text": ...} reply.
        using var groq = new HttpStub(new StubReply(200, ""), new StubReply(200, """{"text":"оттуда"}"""));
        var router = new ProviderRouter(() => GeminiPlusGroqSettings(gemini.BaseUrl, groq.BaseUrl));
        var wav = TempWav();
        try
        {
            var outcome = await router.TranscribeAsync(wav, CancellationToken.None);
            Assert.Equal("оттуда", outcome.Text);
            Assert.Equal("Groq", outcome.Provider);
            Assert.Equal(3, gemini.Hits);
            Assert.Equal(2, groq.Hits);
        }
        finally { File.Delete(wav); }
    }
}
