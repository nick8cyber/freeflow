using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FreeFlow.Core;

namespace FreeFlow.Services;

/// <summary>
/// Gemini Live streaming transcription over the native BidiGenerateContent
/// WebSocket (parity with the macOS RealtimeTranscriptionService's role, though
/// the wire protocol is the native one — the OpenAI-compatible /realtime path
/// 404s on this host). Streams 16 kHz PCM while the hotkey is held, receives
/// interim transcripts live, and settles the final text after audio stops.
/// Protocol (verified against the live service, 2026-10-26):
///   send {"setup":{"model":"models/gemini-3.5-transcribe-live","inputAudioTranscription":{}}}
///   send {"realtimeInput":{"audio":{"data":"&lt;b64&gt;","mimeType":"audio/pcm;rate=16000"}}}
///   recv {"setupComplete":{}}, {"serverContent":{"interimInputTranscription":{"text":"…"}}}
/// </summary>
public sealed class LiveTranscriptionService : IAsyncDisposable
{
    public sealed record Configuration(string BaseUrl, string ApiKey, string Model, string? Language);

    /// <summary>Full interim transcript so far (replaces the previous value, not a delta).</summary>
    public event Action<string>? PartialTranscript;
    public event Action<string>? Failed;

    /// <summary>Optional wire diagnostics hook (probe tooling only; not wired in the app).</summary>
    public Action<string>? WireLog;

    private readonly Configuration _config;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _pendingGate = new();
    private readonly List<byte[]> _pendingChunks = new();
    // All WebSocket writes go through one ordered sender loop (setup first, then
    // audio in capture order) — concurrent sends would interleave and corrupt audio.
    private readonly Channel<string> _outgoing = Channel.CreateBounded<string>(
        new BoundedChannelOptions(400) { FullMode = BoundedChannelFullMode.DropOldest });
    private ClientWebSocket? _ws;
    private volatile bool _audioStopped;
    private DateTime _audioStoppedUtc = DateTime.MaxValue;
    private readonly LiveTranscriptAccumulator _transcript = new();
    private DateTime _lastInterimUtc;
    private DateTime? _setupCompletedUtc;
    private string? _terminalError;

    public LiveTranscriptionService(Configuration config) => _config = config;

    /// <summary>True once the server confirmed the session setup.</summary>
    public bool SetupCompleted => _setupCompletedUtc != null;

    /// <summary>
    /// Builds the BidiGenerateContent WebSocket URL from the Gemini base URL.
    /// Only the host is reused; the Live handshake path is fixed and the key
    /// travels as a query parameter (Bearer headers are not offered here).
    /// </summary>
    public static string DeriveWebSocketUrl(string baseUrl, string apiKey)
    {
        var host = new Uri(baseUrl).Host;
        return $"wss://{host}/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key={Uri.EscapeDataString(apiKey)}";
    }

    /// <summary>The setup message; inputAudioTranscription sits at setup level (verified).
    /// Language hints go in languageCodes (BCP-47 array) — a "language" string field
    /// makes the server never answer setupComplete.</summary>
    public static string BuildSetupMessage(string model, string? language)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("setup");
            writer.WriteStartObject();
            writer.WriteString("model", model.StartsWith("models/", StringComparison.Ordinal) ? model : "models/" + model);
            writer.WritePropertyName("inputAudioTranscription");
            writer.WriteStartObject();
            if (!string.IsNullOrWhiteSpace(language))
            {
                writer.WritePropertyName("languageCodes");
                writer.WriteStartArray();
                writer.WriteStringValue(language);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The realtime audio chunk message for 16 kHz mono PCM16.</summary>
    public static string BuildAudioMessage(byte[] pcm)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("realtimeInput");
            writer.WriteStartObject();
            writer.WritePropertyName("audio");
            writer.WriteStartObject();
            writer.WriteString("data", Convert.ToBase64String(pcm));
            writer.WriteString("mimeType", "audio/pcm;rate=16000");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Connects, starts the ordered sender loop (which emits the setup
    /// first) and the receive loop. Throws on connection failure so the caller can
    /// fall back to record-then-transcribe.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        _ws = ws;
        await ws.ConnectAsync(new Uri(DeriveWebSocketUrl(_config.BaseUrl, _config.ApiKey)), ct);
        _ = Task.Run(() => SenderLoopAsync(ws, _cts.Token));
        _ = Task.Run(() => ReceiveLoopAsync(ws, _cts.Token));
    }

    /// <summary>State summary for diagnostics — content-free, safe for history.</summary>
    public string DescribeState() =>
        _terminalError != null ? $"error ({_terminalError})"
        : _ws == null ? "off"
        : !_setupCompletedUtc.HasValue ? "no-setup"
        : _transcript.Current().Length == 0 ? "no-text"
        : "ok";

    /// <summary>Streams one 16 kHz mono PCM16 chunk. Chunks that arrive before the
    /// server's setupComplete are buffered and flushed in order afterwards (the
    /// server discards realtimeInput sent during setup); the bounded channel drops
    /// the oldest chunks when the link can't keep up.</summary>
    public void AppendAudio(byte[] pcm)
    {
        if (_audioStopped) return;
        if (!SetupCompleted)
        {
            lock (_pendingGate)
            {
                if (_pendingChunks.Count < 200) _pendingChunks.Add(pcm); // ~10 s cap
            }
            return;
        }
        _outgoing.Writer.TryWrite(BuildAudioMessage(pcm));
    }

    private async Task SenderLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            await SendTextAsync(BuildSetupMessage(_config.Model, _config.Language), ws, ct);
            while (await _outgoing.Reader.WaitToReadAsync(ct))
            {
                while (_outgoing.Reader.TryRead(out var message))
                {
                    WireLog?.Invoke("OUT " + message[..Math.Min(120, message.Length)]);
                    await SendTextAsync(message, ws, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Fail($"Realtime send failed: {ex.Message}");
        }
    }

    private void FlushPendingChunks()
    {
        byte[][] chunks;
        lock (_pendingGate)
        {
            chunks = _pendingChunks.ToArray();
            _pendingChunks.Clear();
        }
        foreach (var chunk in chunks) _outgoing.Writer.TryWrite(BuildAudioMessage(chunk));
    }

    /// <summary>
    /// Stops the audio stream and waits for the final transcript. Verified against
    /// the live service (2026-10-26): transcribe-live keeps refining for up to ~3.2 s
    /// AFTER audio stops — a shorter wait returns text with the tail missing. Waits
    /// until both the minimum post-stop time and the interim quiet time pass, or the
    /// absolute timeout, then returns the latest interim text (empty when none).
    /// </summary>
    public async Task<string> FinishAsync(TimeSpan minWait, TimeSpan settle, TimeSpan timeout, CancellationToken ct)
    {
        _audioStopped = true;
        _audioStoppedUtc = DateTime.UtcNow;
        _outgoing.Writer.TryWrite("{\"realtimeInput\":{\"audioStreamEnd\":true}}");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && _terminalError == null && _ws is { State: WebSocketState.Open })
        {
            var sinceStop = DateTime.UtcNow - _audioStoppedUtc;
            var sinceLast = DateTime.UtcNow - _lastInterimUtc;
            if (sinceStop >= minWait && sinceLast >= settle)
                break;
            await Task.Delay(80, ct);
        }
        return _transcript.Final().Trim();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _outgoing.Writer.TryComplete();
        if (_ws != null)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch (Exception) { }
            _ws.Dispose();
        }
        _cts.Dispose();
    }

    private async Task SendTextAsync(string text, ClientWebSocket ws, CancellationToken ct)
    {
        await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
                var frame = Encoding.UTF8.GetString(message.ToArray());
                WireLog?.Invoke("IN  " + frame[..Math.Min(120, frame.Length)]);
                HandleServerEvent(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Fail($"Realtime connection lost: {ex.Message}");
        }
    }

    private void HandleServerEvent(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("setupComplete", out _))
            {
                _setupCompletedUtc = DateTime.UtcNow;
                _lastInterimUtc = DateTime.UtcNow; // arm the settle timer from setup
                FlushPendingChunks();
                return;
            }
            if (root.TryGetProperty("error", out var error))
            {
                Fail("Realtime server error: " + (error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString()));
                return;
            }
            if (root.TryGetProperty("voiceActivity", out var voiceActivity))
            {
                // Pauses split dictation into segments; each segment's interim text
                // REPLACES the previous one, so segments must be committed on their
                // boundaries or every segment after the first loses the text before it.
                var kind = voiceActivity.TryGetProperty("type", out var vt) ? vt.GetString() : null;
                if (kind == "ACTIVITY_END") _transcript.EndSegment();
                else if (kind == "ACTIVITY_START") _transcript.StartSegment();
                _lastInterimUtc = DateTime.UtcNow;
                return;
            }
            if (root.TryGetProperty("serverContent", out var content))
            {
                if (content.TryGetProperty("interimInputTranscription", out var interim) &&
                    interim.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                {
                    _transcript.FeedInterim(text.GetString() ?? "");
                    _lastInterimUtc = DateTime.UtcNow;
                    PartialTranscript?.Invoke(_transcript.Current());
                }
                if (content.TryGetProperty("inputTranscription", out var final) &&
                    final.TryGetProperty("text", out var finalText) &&
                    finalText.ValueKind == JsonValueKind.String)
                {
                    _transcript.FeedFinal(finalText.GetString() ?? "");
                    _lastInterimUtc = DateTime.UtcNow;
                    PartialTranscript?.Invoke(_transcript.Current());
                }
            }
        }
    }

    private void Fail(string message)
    {
        _terminalError ??= message;
        _lastInterimUtc = DateTime.UtcNow; // let FinishAsync return promptly
        Failed?.Invoke(message);
    }
}
