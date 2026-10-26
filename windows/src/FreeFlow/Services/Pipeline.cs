using System.IO;
using System.Runtime.InteropServices;
using FreeFlow.Core;

namespace FreeFlow.Services;

public enum TriggerMode { Hold, Toggle, Manual }

/// <summary>
/// Pipeline orchestrator ported from the macOS AppState flow: record → capture
/// context → transcribe → post-process (or edit mode / macros / verbatim) → paste.
/// Every post-processing failure falls back to the raw trimmed transcript.
///
/// Hotkey entry points are marshaled onto the UI thread by the injected dispatcher
/// so that no heavy work ever runs inside the low-level keyboard hook callback
/// (blocking it stalls system-wide keyboard input and gets the hook silently removed).
/// </summary>
public sealed class Pipeline
{
    private readonly Func<AppSettings> _settings;
    private readonly ProviderRouter _router;
    private readonly PasteService _paste;
    private readonly HistoryStore _history;
    private readonly Action<Action> _marshal;
    private readonly object _gate = new();

    private AudioRecorder? _recorder;
    private LiveTranscriptionService? _live;
    private CancellationTokenSource? _sessionCts;
    private DictationSessionState _sessionState = DictationSessionState.None;
    private bool _isProcessing;
    private bool _editIntent;
    private string _selectionSnapshot = "";
    private string? _contextAppName;
    private string _lastTranscript = "";

    public Pipeline(
        Func<AppSettings> settings,
        ProviderRouter router,
        HotkeyEngine hotkeys,
        HistoryStore history,
        Action<Action>? marshal = null)
    {
        _settings = settings;
        _router = router;
        _history = history;
        _marshal = marshal ?? (action => action());
        _paste = new PasteService(hotkeys.IsAnyShortcutInputHeld);
    }

    public string LastTranscript => _lastTranscript;

    public bool IsBusy
    {
        get { lock (_gate) return _sessionState != DictationSessionState.None || _isProcessing; }
    }

    // --- events (marshaled to the UI context) ---
    public event Action<TriggerMode>? RecordingStarted;
    public event Action<double>? LevelChanged;
    public event Action<string>? StatusChanged;
    public event Action? Finished;
    /// <summary>Live interim transcript from realtime streaming (full text so far).</summary>
    public event Action<string>? PartialTranscript;
    /// <summary>Foreground app captured at recording start — the paste destination.</summary>
    public event Action<string>? TargetAppChanged;
    public event Action<string>? ErrorOccurred;
    public event Action? Cancelled;

    // ------------------------------------------------------------------
    // Hotkey entry points. Called from the keyboard-hook thread; each one
    // hands the real work to the UI thread via the injected dispatcher.
    // ------------------------------------------------------------------

    public void OnHoldActivated() => Marshal(() =>
    {
        if (SessionBegin(DictationSessionState.Hold)) Begin(TriggerMode.Hold);
    });

    public void OnHoldDeactivated() => Marshal(() =>
    {
        if (CurrentState() == DictationSessionState.Hold) End();
    });

    public void OnToggleActivated() => Marshal(() =>
    {
        switch (CurrentState())
        {
            case DictationSessionState.None:
                if (SessionBegin(DictationSessionState.Toggle)) Begin(TriggerMode.Toggle);
                break;
            case DictationSessionState.Hold:
                // Latch hold into toggle without stopping the recording (macOS
                // DictationShortcutSessionController semantics).
                lock (_gate) _sessionState = DictationSessionState.Toggle;
                StatusChanged?.Invoke("Switched to toggle mode");
                break;
            case DictationSessionState.Toggle:
                End();
                break;
        }
    });

    public void Cancel() => Marshal(CancelCore);

    /// <summary>Synchronous cancel for shutdown paths (marshaled posts may never run).</summary>
    public void CancelNow() => CancelCore();

    /// <summary>Escape semantics ported from AppState: a hold-mode recording is NOT
    /// Escape-cancellable (the key belongs to the focused app); transcription and
    /// toggle sessions are. Returns true when the escape was consumed so the hook
    /// can swallow it.</summary>
    public bool OnEscapeRequested()
    {
        if (CurrentState() == DictationSessionState.Hold) return false;
        CancelCore();
        return true;
    }

    private void CancelCore()
    {
        AudioRecorder? recorder;
        LiveTranscriptionService? live;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            recorder = _recorder;
            cts = _sessionCts;
            live = _live;
            _sessionState = DictationSessionState.None;
            _recorder = null;
            _live = null;
        }
        if (recorder == null && cts == null && live == null) return;
        try { recorder?.Cancel(); recorder?.Dispose(); } catch (Exception) { }
        try { cts?.Cancel(); } catch (Exception) { }
        _ = live?.DisposeAsync();
        Post(() => Cancelled?.Invoke());
    }

    public void PasteAgain(AppSettings settings)
    {
        var transcript = _lastTranscript;
        if (transcript.Length == 0) return;
        if (IsBusy) return; // never race the pipeline's own paste
        Task.Run(() =>
        {
            try
            {
                _paste.Paste(
                    transcript, pressEnterAfter: false, settings.PreserveClipboard,
                    settings.KeepInClipboardHistory);
            }
            catch (Exception)
            {
                // Paste-again is best-effort; clipboard races must not crash the app.
            }
        });
    }

    private void Marshal(Action action) => _marshal(action);

    private DictationSessionState CurrentState()
    {
        lock (_gate) return _sessionState;
    }

    private bool SessionBegin(DictationSessionState state)
    {
        lock (_gate)
        {
            // Ignore activations while a previous transcription is still processing
            // (port of the Swift isTranscribing guard).
            if (_sessionState != DictationSessionState.None || _isProcessing) return false;
            _sessionState = state;
            return true;
        }
    }

    private Task<string>? _selectionTask;
    private Task<string>? _contextTask;

    private void Begin(TriggerMode mode)
    {
        var settings = _settings();
        var recorder = new AudioRecorder(settings.MicDeviceName);
        var cts = new CancellationTokenSource();

        // Selection snapshot runs on a worker so a slow cross-process UIA query can
        // never stall the keyboard hook; End() waits (bounded) for it before deciding
        // the edit-mode intent.
        _selectionTask = settings.CommandModeEnabled
            ? Task.Run(SelectionCapture.GetSelectedText)
            : null;

        lock (_gate)
        {
            _recorder = recorder;
            _sessionCts = cts;
            _selectionSnapshot = "";
            _editIntent = false;
            _contextAppName = null;
        }

        // Context capture starts when recording starts (port of startContextCapture):
        // the summary must describe the app the user dictated INTO, not whatever is
        // frontmost when they stop.
        _contextTask = settings.ContextEnabled
            ? Task.Run(() => CollectContextAsync(settings, cts.Token))
            : Task.FromResult("");

        recorder.LevelChanged += level => Post(() => LevelChanged?.Invoke(level));
        recorder.FirstAudioReceived += () => Post(() => StatusChanged?.Invoke("Recording…"));
        recorder.RecordingFailed += message =>
        {
            Cancel();
            Post(() => ErrorOccurred?.Invoke(message));
        };

        // Realtime (Gemini Live) streaming runs alongside the plain recording: the
        // recorder still buffers a WAV, so any Live failure falls back to
        // record-then-transcribe within the same session. Chunks are resampled to
        // 16 kHz mono when the ladder had to open a device-native format.
        if (settings.RealtimeStreamingEnabled &&
            !string.IsNullOrWhiteSpace(settings.GeminiApiKey))
        {
            var live = new LiveTranscriptionService(new LiveTranscriptionService.Configuration(
                settings.GeminiBaseUrl,
                settings.GeminiApiKey,
                string.IsNullOrWhiteSpace(settings.RealtimeStreamingModel)
                    ? "gemini-3.5-transcribe-live"
                    : settings.RealtimeStreamingModel,
                string.IsNullOrEmpty(settings.TranscriptionLanguage)
                    ? null
                    : settings.TranscriptionLanguage));
            lock (_gate) _live = live;
            live.PartialTranscript += text => Post(() => PartialTranscript?.Invoke(text));
            live.Failed += _ => Post(() => StatusChanged?.Invoke("Realtime failed — falling back"));
            AudioChunkResampler? resampler = recorder.CapturesTargetFormat
                ? null
                : new AudioChunkResampler(recorder.CaptureFormat.SampleRate, recorder.CaptureFormat.Channels);
            recorder.AudioChunk += chunk =>
            {
                var pcm16k = resampler?.Convert(chunk) ?? chunk;
                if (pcm16k is { Length: > 0 }) live.AppendAudio(pcm16k);
            };
            _ = Task.Run(async () =>
            {
                try { await live.StartAsync(CancellationToken.None); }
                catch (Exception) { /* session still covered by the WAV fallback */ }
            });
        }

        try
        {
            recorder.Start();
        }
        catch (Exception ex)
        {
            // NAudio throws MmException (no device, device busy) rather than
            // InvalidOperationException — any start failure must unwind the session.
            CancelCore();
            Post(() => ErrorOccurred?.Invoke($"Failed to start recording: {ex.Message}"));
            return;
        }

        // Remember the paste destination: if focus moves elsewhere mid-dictation
        // (a stray click), the paste is restored to the window dictation started in.
        _targetWindow = GetForegroundWindow();

        Post(() => RecordingStarted?.Invoke(mode));
    }

    private void End()
    {
        AudioRecorder? recorder;
        LiveTranscriptionService? live;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            recorder = _recorder;
            _recorder = null;
            live = _live;
            _live = null;
            if (_sessionState == DictationSessionState.None && recorder == null) return;
            _sessionState = DictationSessionState.None;
            _isProcessing = true;
            cts = _sessionCts; // stays reachable so Escape can cancel processing
        }
        if (recorder == null || cts == null) return;

        // Both waits happen on the worker: the bounded UIA selection wait must never
        // block the UI thread (it services the keyboard hook).
        var selectionTask = _selectionTask;
        _selectionTask = null;
        var contextTask = _contextTask;
        _contextTask = null;

        var ct = cts.Token;
        _ = Task.Run(async () =>
        {
            var settings = _settings();

            // Resolve the edit-mode intent from the snapshot task (bounded wait).
            string selection = "";
            try
            {
                if (selectionTask != null && selectionTask.Wait(TimeSpan.FromMilliseconds(750)))
                {
                    selection = selectionTask.Result;
                }
            }
            catch (Exception) { }
            var editIntent = !string.IsNullOrWhiteSpace(selection);
            lock (_gate)
            {
                _selectionSnapshot = selection;
                _editIntent = editIntent;
            }

            try
            {
                var wavPath = recorder.Stop();
                if (wavPath == null)
                {
                    // Cancelled (watchdog/escape) or zero captured audio: no upload,
                    // no bogus provider error (Swift: "No audio recorded").
                    if (settings.KeepHistory)
                    {
                        RecordHistory(settings, _lastTranscript, "", "No audio recorded", "none");
                    }
                    Post(() => Cancelled?.Invoke());
                    return;
                }
                await RunProcessingAsync(wavPath, editIntent, settings, ct, contextTask ?? Task.FromResult(""), live);
            }
            catch (OperationCanceledException)
            {
                Post(() => Cancelled?.Invoke());
            }
            catch (ProviderException ex) when (ex.Kind == ProviderFailureKind.Cancelled)
            {
                Post(() => Cancelled?.Invoke());
            }
            catch (ProviderException ex)
            {
                var message = FriendlyErrors.Describe(ex) + $" (live: {live?.DescribeState() ?? "off"})";
                RecordHistory(settings, _lastTranscript, "", $"Error: {message}", "none");
                Post(() => ErrorOccurred?.Invoke(message));
            }
            catch (Exception ex)
            {
                RecordHistory(settings, _lastTranscript, "", $"Error: {ex.Message} (live: {live?.DescribeState() ?? "off"})", "none");
                Post(() => ErrorOccurred?.Invoke($"Unexpected error: {ex.Message}"));
            }
            finally
            {
                lock (_gate) _isProcessing = false;
                recorder.Dispose();
                _ = live?.DisposeAsync();
                cts.Dispose();
                Post(() => Finished?.Invoke());
            }
        });
    }

    private async Task RunProcessingAsync(
        string wavPath, bool editIntent, AppSettings settings, CancellationToken ct,
        Task<string> contextTask, LiveTranscriptionService? live)
    {
        TranscriptionOutcome transcription;
        try
        {
            // Realtime transcript first: when the Live session completed its setup
            // and produced text, it wins — the WAV stays as the fallback only.
            var liveText = "";
            if (live != null && live.SetupCompleted)
            {
                Post(() => StatusChanged?.Invoke("Realtime transcribing…"));
                try
                {
                    // Adaptive: return once the interim went quiet (observed server-side
                    // refinement can take up to ~3 s, but usually settles in ~1 s) —
                    // a fixed long minimum just adds dead paste latency.
                    liveText = await live.FinishAsync(
                        TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(6.0), ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { liveText = ""; }
            }
            if (liveText.Length > 0)
            {
                transcription = new TranscriptionOutcome(
                    liveText, "Gemini (live)",
                    string.IsNullOrWhiteSpace(settings.RealtimeStreamingModel)
                        ? "gemini-3.5-transcribe-live"
                        : settings.RealtimeStreamingModel);
            }
            else
            {
                Post(() => StatusChanged?.Invoke($"Transcribing… (live: {live?.DescribeState() ?? "off"})"));
                transcription = await _router.TranscribeAsync(wavPath, ct);
            }
        }
        finally
        {
            try { File.Delete(wavPath); } catch (Exception) { }
        }

        var raw = transcription.Text.Trim();
        Post(() => StatusChanged?.Invoke($"Transcribed with {transcription.Provider} ({transcription.Model})"));

        // Context is consumed only by LLM cleanup and command mode — Google
        // transcripts skip both, and awaiting the context call here would add
        // pure paste latency (and burn generateContent quota per dictation).
        // It is awaited inside the branches that actually use it.

        var parsing = TextUtil.ParseTranscriptCommands(raw, settings.PressEnterEnabled);
        var text = parsing.Transcript;
        // Bootstrap Paste Again with the press-enter-stripped transcript (Swift parity).
        if (text.Length > 0) _lastTranscript = text;

        string finalText;
        string status;

        // Empty check comes FIRST — an empty raw transcript never reaches command mode
        // (Swift guard at AppState.swift:2515-2517). Enter-only dictations still press
        // Enter (Swift: enterOnlyStatusText + pressEnterWhenShortcutReleased).
        if (string.IsNullOrEmpty(text))
        {
            if (parsing.ShouldPressEnterAfterPaste)
            {
                Post(() => StatusChanged?.Invoke("Pressed Enter"));
                RecordHistory(settings, raw, "", "Pressed Enter", transcription.Provider);
                _paste.PressEnterOnly();
            }
            else
            {
                Post(() => StatusChanged?.Invoke("Nothing to transcribe"));
                RecordHistory(settings, raw, "", "Nothing to transcribe", transcription.Provider);
            }
            return;
        }

        if (editIntent && settings.CommandModeEnabled)
        {
            // Command intent takes priority over voice macros (port of the Swift order).
            if (string.IsNullOrWhiteSpace(_selectionSnapshot))
            {
                Post(() => ErrorOccurred?.Invoke("Select text to transform first."));
                return;
            }
            Post(() => StatusChanged?.Invoke("Transforming selection…"));
            var contextSummary = await contextTask;
            var system = SystemPromptBuilder.BuildCommandModePrompt(settings.OutputLanguage, settings.CustomVocabulary);
            var user = Prompts.CommandModeUserMessage(contextSummary, text, _selectionSnapshot);
            var result = await _router.CompleteWithFallbackAsync(system, user, ct);
            finalText = result.Text != null
                ? TranscriptOutputSanitizer.CommandModeTranscript(result.Text)
                : _selectionSnapshot;
            status = result.Text != null
                ? $"Transformed selection with {result.ProviderUsed}"
                : "Edit failed, selection kept unchanged";
        }
        else if (FindMacro(settings, text) is { } macro)
        {
            finalText = macro.payload;
            status = $"Voice macro used: {macro.command}";
        }
        else if (transcription.Provider.StartsWith("Gemini", StringComparison.Ordinal))
        {
            // Google transcripts paste as-is: the transcribe models (live and
            // record-then-transcribe) already punctuate, and cleanup would only
            // burn the tight free-tier generate-content quota. Per user policy,
            // LLM cleanup stays an option for the Groq fallback mode only.
            finalText = text;
            status = transcription.Provider == "Gemini (live)"
                ? "Realtime Google transcript"
                : $"Google transcript (cleanup skipped; live: {live?.DescribeState() ?? "off"})";
        }
        else if (settings.PreserveExactWording)
        {
            if (string.IsNullOrWhiteSpace(settings.OutputLanguage))
            {
                finalText = text;
                status = "Post-processing skipped (preserve exact wording)";
            }
            else
            {
                Post(() => StatusChanged?.Invoke("Translating…"));
                var result = await _router.CompleteWithFallbackAsync(
                    Prompts.VerbatimTranslationSystemPrompt(settings.OutputLanguage),
                    Prompts.VerbatimTranslationUserMessage(settings.OutputLanguage, text),
                    ct);
                finalText = result.Text != null
                    ? TranscriptOutputSanitizer.VerbatimTranslation(result.Text)
                    : text;
                status = result.Text != null
                    ? $"Translated with {result.ProviderUsed}"
                    : "Translation failed, using raw transcript";
            }
        }
        else if (!settings.CleanupGroqTranscripts)
        {
            finalText = text;
            status = "Cleanup disabled (Groq mode)";
        }
        else
        {
            Post(() => StatusChanged?.Invoke("Cleaning up…"));
            var contextSummary = await contextTask;
            var system = SystemPromptBuilder.BuildCleanupPrompt(
                settings.SystemPrompt, settings.CustomVocabulary, settings.OutputLanguage);
            var user = Prompts.CleanupUserMessage(
                string.IsNullOrEmpty(contextSummary)
                    ? "Could not refresh app context at stop time; using text-only post-processing."
                    : contextSummary,
                text);
            var result = await _router.CompleteWithFallbackAsync(system, user, ct);

            if (result.Text == null)
            {
                finalText = text;
                status = $"Post-processing failed ({result.ErrorDescription}), using raw transcript";
            }
            else
            {
                var cleaned = TranscriptOutputSanitizer.PostProcessedTranscript(result.Text);
                // The guard self-disables when an output language is set (Swift parity,
                // TranscriptTextCore.swift:133) — pass the real language, not "".
                var guardTripped = cleaned.Length > 0 &&
                    TranscriptOutputSanitizer.AppearsToHaveExecutedInstruction(
                        text, cleaned, settings.OutputLanguage);
                if (guardTripped)
                {
                    // The model answered the transcript as an instruction — give the
                    // other provider one chance (Swift suspectedInstructionExecution
                    // fallback) before using raw text.
                    var retry = await _router.CompleteWithFallbackAsync(
                        system, user, ct, excludeProvider: ProviderFromResult(result.ProviderUsed));
                    var retryCleaned = retry.Text != null
                        ? TranscriptOutputSanitizer.PostProcessedTranscript(retry.Text)
                        : "";
                    if (retryCleaned.Length > 0 &&
                        !TranscriptOutputSanitizer.AppearsToHaveExecutedInstruction(
                            text, retryCleaned, settings.OutputLanguage))
                    {
                        cleaned = retryCleaned;
                    }
                    else
                    {
                        cleaned = "";
                    }
                }

                if (cleaned.Length > 0)
                {
                    finalText = cleaned;
                    status = $"Cleaned with {result.ProviderUsed}";
                }
                else if (guardTripped)
                {
                    finalText = text;
                    status = "Post-processing unusable, using raw transcript";
                }
                else
                {
                    // A successful cleanup that answered EMPTY means paste nothing
                    // (Swift: "Nothing to transcribe") — NOT a failure to fall back from.
                    Post(() => StatusChanged?.Invoke("Nothing to transcribe"));
                    RecordHistory(settings, text, "", "Cleaned to EMPTY", transcription.Provider);
                    return;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(finalText))
        {
            if (parsing.ShouldPressEnterAfterPaste)
            {
                // macOS still presses Enter when only "press enter" was dictated.
                Post(() => StatusChanged?.Invoke("Pressed Enter"));
                _paste.PressEnterOnly();
            }
            else
            {
                Post(() => StatusChanged?.Invoke("Nothing to transcribe"));
            }
            return;
        }

        _lastTranscript = finalText;
        Post(() => StatusChanged?.Invoke(status + (parsing.ShouldPressEnterAfterPaste ? "; detected press enter command" : "")));

        RestoreFocusToTarget();
        var pasted = _paste.Paste(
            finalText,
            parsing.ShouldPressEnterAfterPaste,
            settings.PreserveClipboard,
            settings.KeepInClipboardHistory);
        if (!pasted)
        {
            Post(() => ErrorOccurred?.Invoke(
                "Could not paste — the clipboard or the target window refused input (is it elevated?)"));
        }

        if (settings.KeepHistory) _history.Append(new HistoryEntry(
            DateTime.Now,
            _contextAppName ?? "",
            text,
            finalText,
            status,
            transcription.Provider));
    }

    /// <summary>"Gemini (model)" / "Groq (model)" → the provider identifier used for exclusions.</summary>
    private static string? ProviderFromResult(string providerUsed) =>
        providerUsed.StartsWith("Gemini", StringComparison.Ordinal) ? "gemini"
        : providerUsed.StartsWith("Groq", StringComparison.Ordinal) ? "groq"
        : null;

    // --- paste-destination focus restore ---
    private IntPtr _targetWindow;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    /// <summary>
    /// Best-effort restore of the window dictation started in: a stray click during
    /// recording would otherwise redirect the Ctrl+V into whatever window the user
    /// hit. The ALT tap relaxes the foreground lock so a background process may call
    /// SetForegroundWindow; failure leaves focus as-is (paste follows focus, like
    /// the macOS app).
    /// </summary>
    private void RestoreFocusToTarget()
    {
        var target = _targetWindow;
        _targetWindow = IntPtr.Zero;
        if (target == IntPtr.Zero || target == GetForegroundWindow()) return;
        try
        {
            keybd_event(0xA4, 0, 0, IntPtr.Zero);
            keybd_event(0xA4, 0, 2, IntPtr.Zero);
            SetForegroundWindow(target);
            Thread.Sleep(60);
        }
        catch (Exception) { }
    }

    private void RecordHistory(AppSettings settings, string raw, string finalText, string status, string provider)
    {
        if (!settings.KeepHistory) return;
        _history.Append(new HistoryEntry(
            DateTime.Now,
            _contextAppName ?? "",
            raw,
            finalText,
            status,
            provider));
    }

    private async Task<string> CollectContextAsync(AppSettings settings, CancellationToken ct)
    {
        try
        {
            var context = ContextService.Collect(settings.IncludeScreenshot);
            _contextAppName = context.AppName;
            Post(() => TargetAppChanged?.Invoke(context.AppName ?? ""));
            var summary = await _router.SummarizeContextAsync(
                ContextService.BuildMetadata(context), context.JpegBase64, ct);
            return summary ?? "Could not refresh app context at stop time; using text-only post-processing.";
        }
        catch (Exception)
        {
            return "Could not refresh app context at stop time; using text-only post-processing.";
        }
    }

    private static (string command, string payload)? FindMacro(AppSettings settings, string text)
    {
        var normalized = TextUtil.NormalizeForMacro(text);
        if (normalized.Length == 0) return null;
        foreach (var (command, payload) in settings.VoiceMacros.ToArray())
        {
            if (TextUtil.NormalizeForMacro(command) == normalized) return (command, payload);
        }
        return null;
    }

    private void Post(Action action) => _marshal(action);
}

/// <summary>Best-effort selected-text capture via UI Automation (edit-mode snapshot).</summary>
public static class SelectionCapture
{
    public static string GetSelectedText()
    {
        try
        {
            var focused = System.Windows.Automation.AutomationElement.FocusedElement;
            if (focused.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var textPatternObj) &&
                textPatternObj is System.Windows.Automation.TextPattern textPattern)
            {
                var ranges = textPattern.GetSelection();
                if (ranges.Length > 0)
                {
                    var text = ranges[0].GetText(4096);
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
            // No whole-field fallback: macOS uses only an actual selection
            // (AXSelectedText); treating the entire control value as "selected" would
            // let a plain dictation transform and replace a whole document.
        }
        catch (Exception)
        {
            // UIA is best-effort; missing support just disables edit mode for that window.
        }
        return "";
    }
}
