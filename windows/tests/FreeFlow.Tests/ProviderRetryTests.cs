using System.Net;
using System.Net.Sockets;
using System.Text;
using FreeFlow.Core;
using FreeFlow.Services;
using Xunit;

namespace FreeFlow.Tests;

/// <summary>
/// Regression tests for the first-dictation-after-idle failure: a cold upstream
/// answers 200 with no text while the same request succeeds seconds later
/// ("the second dictation always works"). ProviderRouter must transparently
/// retry the transcription ONCE on an empty result before the fallback chain.
/// Uses a local TCP stub, never a live provider.
/// </summary>
public class ProviderRetryTests
{
    /// <summary>Minimal HTTP/1.1 stub: one scripted response per request, closes
    /// each connection so request counting stays exact.</summary>
    private sealed class HttpStub : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _acceptLoop;
        private readonly Queue<string> _responses = new();
        private int _hits;
        public List<string> Paths { get; } = new();

        public HttpStub(params string[] jsonBodies)
        {
            foreach (var body in jsonBodies) _responses.Enqueue(body);
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
                    var headerEnd = -1;
                    var buffer = new StringBuilder();
                    var read = new byte[8192];
                    int contentLength = 0, received = 0;
                    // Read headers, then the body per Content-Length.
                    while (true)
                    {
                        var n = await stream.ReadAsync(read);
                        if (n <= 0) break;
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
                    var request = buffer.ToString();
                    var path = request.Split(' ')[1];
                    lock (this) { _hits++; Paths.Add(path); }
                    var body = _responses.Count > 0 ? _responses.Dequeue() : "{}";
                    var payload = Encoding.UTF8.GetBytes(body);
                    var head = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                        $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(payload);
                }
            }
            catch (Exception) { /* listener stopped */ }
        }

        public void Dispose()
        {
            _listener.Stop();
            _ = _acceptLoop;
        }
    }

    private static string EmptyCandidatesBody =>
        """{"candidates":[{"finishReason":"STOP"}]}""";

    private static string TranscribedBody(string text) =>
        "{\"candidates\":[{\"content\":{\"parts\":[{\"audioTranscription\":{\"text\":\"" +
        text + "\"}}]}}]}";

    private static AppSettings SettingsFor(string baseUrl) => new()
    {
        GeminiApiKey = "test-key",
        GeminiBaseUrl = baseUrl,
        TranscriptionModelGemini = "gemini-3.5-transcribe",
        TranscriptionTimeoutSeconds = 10,
        GroqApiKey = "",
    };

    private static string TempWav()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ff-retry-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, "RIFFsynthetic"u8.ToArray());
        return path;
    }

    [Fact]
    public async Task EmptyResult_RetriedOnce_SecondAttemptWins()
    {
        using var stub = new HttpStub(EmptyCandidatesBody, TranscribedBody("восстановлено"));
        var router = new ProviderRouter(() => SettingsFor(stub.BaseUrl));
        var wav = TempWav();
        try
        {
            var outcome = await router.TranscribeAsync(wav, CancellationToken.None);
            Assert.Equal("восстановлено", outcome.Text);
            Assert.Equal("Gemini", outcome.Provider);
            Assert.Equal(2, stub.Hits);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public async Task EmptyResult_TwiceInARow_FallsThroughWithError_NoInfiniteRetry()
    {
        using var stub = new HttpStub(EmptyCandidatesBody, EmptyCandidatesBody);
        var router = new ProviderRouter(() => SettingsFor(stub.BaseUrl));
        var wav = TempWav();
        try
        {
            var ex = await Assert.ThrowsAsync<ProviderException>(
                () => router.TranscribeAsync(wav, CancellationToken.None));
            Assert.Equal(ProviderFailureKind.EmptyOutput, ex.Kind);
            Assert.Equal(2, stub.Hits);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public async Task FirstAttemptSucceeds_NoExtraRequests()
    {
        using var stub = new HttpStub(TranscribedBody("ok"));
        var router = new ProviderRouter(() => SettingsFor(stub.BaseUrl));
        var wav = TempWav();
        try
        {
            var outcome = await router.TranscribeAsync(wav, CancellationToken.None);
            Assert.Equal("ok", outcome.Text);
            Assert.Equal(1, stub.Hits);
        }
        finally { File.Delete(wav); }
    }
}
