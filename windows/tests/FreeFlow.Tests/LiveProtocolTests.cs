using System.Text.Json;
using FreeFlow.Services;
using Xunit;

namespace FreeFlow.Tests;

/// <summary>
/// Wire-format tests for the Gemini Live (BidiGenerateContent) streaming client:
/// URL derivation, setup message, and realtime audio chunk shapes as verified
/// against the live service on 2026-10-26.
/// </summary>
public class LiveProtocolTests
{
    [Fact]
    public void WebSocketUrlReusesHostAndCarriesKeyAsQuery()
    {
        var url = LiveTranscriptionService.DeriveWebSocketUrl(
            "https://generativelanguage.googleapis.com/v1beta", "TESTKEY");
        Assert.Equal(
            "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=TESTKEY",
            url);
    }

    [Fact]
    public void SetupMessagePinsModelAndEnablesInputTranscription()
    {
        var json = LiveTranscriptionService.BuildSetupMessage("gemini-3.5-transcribe-live", "ru");
        using var doc = JsonDocument.Parse(json);
        var setup = doc.RootElement.GetProperty("setup");
        Assert.Equal("models/gemini-3.5-transcribe-live", setup.GetProperty("model").GetString());
        Assert.True(setup.TryGetProperty("inputAudioTranscription", out var transcription));
        // languageCodes is a BCP-47 array; a "language" string field makes the
        // server never answer setupComplete (verified against the live service).
        Assert.Equal("ru", transcription.GetProperty("languageCodes")[0].GetString());
    }

    [Fact]
    public void SetupMessageOmitsLanguageWhenEmpty()
    {
        var json = LiveTranscriptionService.BuildSetupMessage("models/gemini-3.5-transcribe-live", "");
        using var doc = JsonDocument.Parse(json);
        var setup = doc.RootElement.GetProperty("setup");
        Assert.False(setup.GetProperty("inputAudioTranscription").TryGetProperty("languageCodes", out _));
    }

    [Fact]
    public void AudioChunkDeclaresSixteenKiloherzPcm()
    {
        var json = LiveTranscriptionService.BuildAudioMessage([1, 2, 3, 4]);
        using var doc = JsonDocument.Parse(json);
        var audio = doc.RootElement.GetProperty("realtimeInput").GetProperty("audio");
        Assert.Equal("audio/pcm;rate=16000", audio.GetProperty("mimeType").GetString());
        Assert.Equal("AQIDBA==", audio.GetProperty("data").GetString());
    }
}
