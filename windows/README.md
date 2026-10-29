# FreeFlow for Windows

Windows migration of [FreeFlow](https://github.com/zachlatta/freeflow) (macOS dictation app),
rewritten as a native C# / .NET 8 tray application (WinForms, no Xcode/SPM dependency).

> This document is the authoritative spec for the Windows port. The repository-root
> `README.md` and `AGENTS.md` describe the **macOS** app only — the platforms' features
> intentionally differ (dictation protocol, providers, settings), so never treat one as
> documentation for the other.

**Dictation runs on Google Gemini Live** (`gemini-3.5-transcribe-live` over the native
BidiGenerateContent WebSocket). Groq (whisper-large-v3) is the optional transcription fallback;
the non-live Gemini `gemini-3.5-transcribe` is the built-in record-then-transcribe fallback.

```
Hold Right Alt            →  open mic (pinned device, format ladder) → stream PCM 16 kHz mono
                             chunks over the Live WebSocket — interim transcript appears in
                             the overlay while speaking
Release                   →  stream end signal → wait for the transcript to settle (adaptive:
                             interim quiet 1.0 s, min 0.8 s, cap 6 s) → paste at cursor
Fallback (any Live error) →  the buffered WAV → gemini-3.5-transcribe :generateContent
                             → Groq whisper-large-v3 (if a Groq key is configured)
```

Google transcripts always paste as-is: the transcribe models punctuate themselves, and LLM
cleanup would only burn the tight free-tier generateContent quota (25 requests/day). LLM
cleanup is an option for the Groq fallback mode only (Settings → Behavior, default on).

## Build

Requires the .NET 8 SDK.

```cmd
cd windows
build.cmd          :: build + tests + publish single-file exe to windows\dist\FreeFlow.exe
```

Or manually:

```cmd
dotnet build FreeFlow.sln
dotnet test  tests\FreeFlow.Tests\FreeFlow.Tests.csproj
dotnet publish src\FreeFlow\FreeFlow.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o dist
```

The published exe is a single ~63 MB file (compression on, no symbols).

## Run

1. Start `FreeFlow.exe`. First run shows setup: paste a **Gemini API key**
   ([aistudio.google.com/apikey](https://aistudio.google.com/apikey)); a **Groq API key**
   ([console.groq.com](https://console.groq.com)) is optional and enables the fallback path.
2. **Hold the Right Alt key** to dictate — the interim transcript appears live in the overlay.
   Release → the text is pasted at the cursor. **Ctrl+Caps Lock** toggles hands-free dictation
   (press-release-press to stop). **Esc** cancels a session (toggle mode only).
3. Settings → Behavior → Microphone: **pin the input device** (recommended — the Windows default
   endpoint is a lottery when Bluetooth headsets connect and leave). The overlay card appears at
   the top center of the **paste-target window's monitor** (the one you are typing into; falls
   back to the cursor's monitor when unknown) and re-asserts topmost every 0.5 s so
   pinned-topmost windows (pinned Telegram etc.) cannot cover it.

Settings live in `%APPDATA%\FreeFlow\settings.json`; API keys are DPAPI-encrypted at rest
(`dpapi:` prefix, CurrentUser scope). Local pipeline history (last 20 entries) is
`%APPDATA%\FreeFlow\history.json`.

**Save-on-failure:** when transcription fails, the recording is kept at
`%APPDATA%\FreeFlow\last-failed.wav` and the tray menu gains a **Paste last recording** item
that re-transcribes and pastes it (the file is deleted after a successful retry, so a stale
recording cannot be pasted twice by accident).

## Architecture

### Dictation pipeline (Services/Pipeline.cs)

`HotkeyEngine` raises hold/toggle activations → marshaled off the hook thread → `Begin()`:
pin-resolves the capture device, opens the recorder (format ladder 16 kHz → 48 k → 44.1 k),
starts context capture, starts the Live session (chunks buffered until `setupComplete`, then
flushed in order). On release: `End()` → `RunProcessingAsync`:
Live transcript (if the session produced text) → else WAV transcription →
command mode / voice macros / cleanup policy → paste.

- **Cleanup policy**: Google transcripts (live or record-then-transcribe) paste as-is;
  Groq transcripts are cleaned by the LLM when the Behavior checkbox is on. Context is awaited
  only by the cleanup and command-mode branches — never on the paste path for Google.
- **Quota note**: free-tier `generateContent` for gemini-3.5-transcribe is **25 requests/day**
  (shared by fallback transcriptions when Live is down). Live sessions have their own,
  larger quota. The non-live Gemini path is the fallback, not the primary.

### Live streaming (Services/LiveTranscriptionService.cs)

Native BidiGenerateContent WebSocket — the OpenAI-style `/realtime` paths 404 on
`generativelanguage.googleapis.com` (verified 2026-10-26; the macOS RealtimeTranscriptionService
targets that OpenAI-style protocol and cannot connect to Google directly).

Wire protocol (verified against the live service):
```
→ {"setup":{"model":"models/gemini-3.5-transcribe-live","inputAudioTranscription":{"languageCodes":["ru"]}}}
→ {"realtimeInput":{"audio":{"data":"<b64>","mimeType":"audio/pcm;rate=16000"}}}   (~50 ms chunks)
→ {"realtimeInput":{"audioStreamEnd":true}}
← {"setupComplete":{}}   ← {"voiceActivity":{"type":"ACTIVITY_START|ACTIVITY_END"}}
← {"serverContent":{"interimInputTranscription":{"text":"…"}}}
```

- `inputAudioTranscription` sits at setup level; language hints go in `languageCodes` (a
  `language` string field makes the server never answer `setupComplete`).
- **Interim text REPLACES the current voice-activity segment** (not a delta). Segments are
  committed on ACTIVITY_START/END by `LiveTranscriptAccumulator` — without that, any dictation
  with a pause loses everything before the last segment (observed live).
- There is no commit verb and no turnComplete on this model — the finish waits adaptively
  (interim quiet 1.0 s, min 0.8 s after stream end, cap 6 s). Server-side tail refinement runs
  up to ~3.2 s after audio stops.
- All writes go through one ordered sender loop (setup first, then audio in capture order);
  a bounded DropOldest channel absorbs stalls.

### Keyboard hook (Services/HotkeyEngine.cs)

WH_KEYBOARD_LL, installed on the UI thread; session begin/end are marshaled off the hook
thread (a slow hook callback gets silently removed by Windows — all hotkeys die until restart).
Key invariants, each fixed against a live failure:

- Edge-aware activation: at a fresh keydown, `GetAsyncKeyState` does not yet include the press,
  so the edge itself is proof the key is down — otherwise the first keydown of the bound key
  leaks to the OS (menu mode for Alt holds) and the keyup gets swallowed → the hold key strands
  logically held after every dictation.
- While a session is active, modifier events are consumed — but a modifier keyup is swallowed
  only when its keydown was swallowed by the same session (Ctrl released after a Ctrl+V paste,
  or released first in a Ctrl+CapsLock combo).
- Suppression markers are paired per press; auto-repeat after a leaked keydown never creates a
  false pairing.
- Health telemetry: `EventsSeen` counter + `RecentKeyDownVks` ring, exposed in
  `%APPDATA%\FreeFlow\hookstate.json` and the tray tooltip; an uninstalled hook is retried
  every 2 s.

### Capture (Services/AudioRecorder.cs, AudioChunkResampler.cs)

MME capture with a format ladder (16 kHz mono target → 48 k → 44.1 k stereo) and a pinned
device resolved by name per start (`MicDeviceResolver` — MME indices are unstable). The Live
path consumes raw chunks, resampled per-chunk by the stateful `AudioChunkResampler` when the
device opened at a non-16k format; the WAV fallback path resamples at Stop via
MediaFoundationResampler.

### Overlay (UI/OverlayForm.cs)

Top-center card anchored to the paste-target window's monitor (`AnchorScreen`, falls back to
the cursor), position clamped into the monitor's working area. Animated 5-bar equalizer, live
interim transcript (tail shown, start trimmed), status line; double-buffered; topmost
re-asserted during recording.

Scaling contract (v8): ONE logical design (constants at 96 DPI) and ONE scale source — the
effective DPI of the presenting monitor (`GetDpiForMonitor`), never the form's `DeviceDpi`,
which lies for a borderless form shown across monitors. Fonts are pixel-sized
(`GraphicsUnit.Pixel`) and the SAME instance measures and draws a string, so measurement and
rendering can never disagree on mixed-DPI setups. The transcript block and equalizer sit
strictly inside the card by construction; `FitTail` (UI/OverlayTextFit.cs, regression-tested)
binary-searches the SMALLEST fitting cut point — the longest visible suffix. Dev tooling:
`--demo-overlay` (cycles states, works alongside a running tray instance) and
`design/capture-overlay.ps1` (screenshots all states on every monitor).

### Transcription retry policy + first-dictation-after-idle resilience (ProviderRouter/AudioRecorder)

Each transcription provider (Gemini, then Groq) gets the initial request plus TWO same-provider
retries for every TRANSIENT failure: empty result (a cold upstream answers 200 with no text —
the "first dictation after idle fails, second works" signature), network drop, timeout,
malformed response, and HTTP 5xx. Deterministic failures fail fast with no retry — rate limits
(cooldown + fallback chain own those), HTTP 4xx client errors (bad key, wrong URL) and
cancellations — so quota is never burned on guaranteed-identical repeats. Independently, if the
first capture session after a device idle delivers zero buffers, `AudioRecorder`'s watchdog
silently re-opens the capture ladder once. Verified with a local TCP stub (ProviderRetryTests)
— no live provider calls.

## Code map

```
windows/
  build.cmd                                build + test + publish
  src/FreeFlow/
    Program.cs                             entry point, single-instance mutex, first-run setup
    Core/
      Prompts.cs                           verbatim prompt texts (post-processing, command mode)
      TranscriptText.cs                    sanitizers, hallucination filter, text parsing
      TextUtil.cs                          macros, vocabulary, "press enter", vocabulary parsing
      CooldownManager.cs / CooldownStore   LLM cooldown tiers + persistence
      AppSettings.cs                       settings model, DPAPI key protection, migrations
      FriendlyErrors.cs                    provider error taxonomy; 4xx carries the provider body
    Services/
      GeminiClient.cs                      generateContent (audio/text/vision) + wire validation
      LiveTranscriptionService.cs          Bidi WebSocket streaming client
      LiveTranscriptAccumulator.cs         per-segment transcript accumulation
      AudioChunkResampler.cs               stateful per-chunk resampler (any capture → 16 k mono)
      GroqClient.cs / ProviderRouter.cs    Groq fallback + provider routing + cooldowns
      AudioRecorder.cs                     NAudio capture, format ladder, chunk events, watchdog
      MicDeviceResolver.cs                 pinned-device name → MME index resolution
      ForegroundTracker.cs                 last real foreground window (paste target for tray
                                           menu actions; shell windows excluded)
      HotkeyEngine.cs                      LL hook, edge-aware matching, suppression, telemetry
      PasteService.cs                      clipboard markers, Ctrl+V SendInput, restore
      ContextService.cs                    foreground app metadata (+ opt-in screenshot)
      Pipeline.cs                          orchestrator: sessions, Live+fallback, policy, history
      HistoryStore.cs / HttpTransport.cs   history file; shared HTTP client + error mapping
    UI/
      TrayContext.cs                       tray, wiring, hook health, autostart
      OverlayForm.cs                       top-center live overlay (equalizer + transcript)
      OverlayTextFit.cs                    transcript tail-fitting (longest visible suffix,
                                           regression-tested)
      SettingsForm(.Pages).cs              providers/shortcuts/behavior/vocabulary/history
      SetupForm.cs / HotkeyBox.cs          first-run wizard; shortcut capture control
  tests/FreeFlow.Tests/                    deterministic tests (ports of Swift core tests + new
                                           logic: retry policy via local TCP stub, tail-fitting,
                                           resampler, protocol builders, hook decisions)
  design/
    capture.ps1                            settings-UI screenshot helper (launch/shot/click)
    capture-overlay.ps1                    overlay demo screenshots: all states × every monitor
    zoom-crop.ps1                          crop+zoom a PNG region for inspection
```

## Diagnostics

- `%APPDATA%\FreeFlow\hookstate.json` (updated every 2 s, counter only, no user content):
  `installed`, hook `events` counter, `lastVk` + `recent` ring of last physical keydowns,
  `holdActive`, bound `holdVk`. A counter frozen while the user types = Windows removed the
  hook; an uninstalled hook is retried automatically.
- `history.json` error lines include the Live state at failure
  (`live: no-setup / no-text / error (...)`).
- `LiveTranscriptionService.WireLog` (unwired in the app) dumps wire frames for probes.

## Privacy

Same rules as upstream: no telemetry, no crash reporting, no FreeFlow server. Network traffic
is provider calls only (Gemini, optionally Groq). Recorded audio is temporary and deleted right
after transcription. `hookstate.json` contains counters and virtual-key codes only — never text
or audio. History stores the last 20 dictations in plaintext when enabled (default, matching
the macOS app); disable in Settings → Behavior.

## Manual test checklist

Automated tests cover the pure logic (accumulators, resampler, protocol builders, hook
decision, resolver, text ports, provider retry policy via a local TCP stub, transcript
tail-fitting with real GDI measurements). These need a human at the machine — items checked
[✓] were verified live on this machine (2026-10-26–30):

- [x] Hold Right Alt → live transcript in the overlay; release → paste at cursor (~1.5 s).
- [x] Multi-pause dictation keeps every segment (no lost beginning).
- [x] Pinned Telegram (topmost) does not cover the overlay.
- [x] Overlay appears top-center on the paste-target's monitor; input field stays clear.
- [x] Overlay renders identically on 100% and 150% monitors in all states — recording
      (2-line transcript, pills), status, error (capture-overlay.ps1 screenshots, 2026-10-29).
- [x] Hotkeys survive app restarts; stuck-modifier state self-clears (hook fixes).
- [x] Transcription retry policy: transient failures retried twice per provider, rate limits
      and 4xx fail fast (ProviderRetryTests, local stub).
- [ ] First dictation after a LONG idle pastes without a manual repeat (policy shipped
      2026-10-30; needs a real idle period to confirm).
- [ ] Ctrl+Caps Lock toggle mode, Esc cancel.
- [ ] Groq fallback path with a Groq key configured.
- [ ] Save-on-failure: last-failed.wav kept, tray "Paste last recording" re-transcribes and
      deletes the file.
- [ ] Clipboard restore after paste; dictation hidden from clipboard history by default.
- [ ] Edit Mode (select text → "make it shorter").
