using System.IO;
using NAudio.Wave;

namespace FreeFlow.Services;

/// <summary>
/// Compact port of LiveAudioLevelNormalizer: dB-domain noise-floor/peak tracking with
/// a speech gate and attack/release smoothing, so a quiet mic still shows a lively
/// waveform and a loud one doesn't peg at maximum.
/// </summary>
public sealed class LiveAudioLevelNormalizer
{
    private const double MinDb = -60;
    private const double SpeechGate = 0.12;
    private const double AttackBlend = 0.45;
    private const double ReleaseBlend = 0.12;

    private double _noiseFloorDb = -45;
    private double _peakDb = -15;
    private double _display;

    public void Reset()
    {
        _noiseFloorDb = -45;
        _peakDb = -15;
        _display = 0;
    }

    public double Normalize(double rms)
    {
        var db = 20.0 * Math.Log10(Math.Max(rms, 1e-5));
        if (db < MinDb) db = MinDb;

        // Noise floor tracks down fast, rises slowly; peak ceiling rises fast, decays slowly.
        if (db < _noiseFloorDb) _noiseFloorDb = 0.8 * _noiseFloorDb + 0.2 * db;
        else _noiseFloorDb += 0.02;
        if (db > _peakDb) _peakDb = 0.6 * _peakDb + 0.4 * db;
        else _peakDb -= 0.05;
        if (_peakDb < _noiseFloorDb + 10) _peakDb = _noiseFloorDb + 10;

        var range = Math.Max(6.0, _peakDb - _noiseFloorDb);
        var raw = Math.Clamp((db - _noiseFloorDb - 3) / range, 0.0, 1.0);

        // Speech gate: background hiss stays invisible.
        if (raw < SpeechGate && db < _noiseFloorDb + 8) raw = 0;

        var blend = raw > _display ? AttackBlend : ReleaseBlend;
        _display += (raw - _display) * blend;
        if (_display < 0.005) _display = 0;
        return _display;
    }
}

/// <summary>
/// Microphone capture ported from the macOS AudioRecorder: converts whatever format
/// the device provides down to a 16 kHz / mono / 16-bit PCM WAV (the format the
/// providers expect), reports live input level for the overlay waveform, and fails
/// only when no audio arrives within the first 2 seconds after start (one-shot
/// baseline watchdog, like the Swift one).
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private const int TargetSampleRate = 16000;
    private static readonly WaveFormat TargetFormat = new(TargetSampleRate, 16, 1);

    // MME waveInOpen does not resample, so the ladder must cover the rates devices
    // actually expose. 48 kHz-only USB/Bluetooth headsets are common; without this
    // rung those users could never record at all.
    private static readonly WaveFormat[] CaptureLadder =
    [
        TargetFormat,
        new WaveFormat(48000, 16, 2),
        new WaveFormat(44100, 16, 2),
    ];

    private WaveInEvent? _waveIn;
    private readonly string? _preferredDeviceName;
    private readonly MemoryStream _rawStream = new();
    private readonly object _lock = new();
    private readonly System.Timers.Timer _watchdog;
    private readonly LiveAudioLevelNormalizer _level = new();
    private long _bytesAtWatchdogStart;
    private WaveFormat _captureFormat = TargetFormat;
    private bool _stopRequested;
    private bool _firstAudioFired;
    private bool _rearmed;

    public event Action<double>? LevelChanged;
    public event Action? FirstAudioReceived;
    public event Action<string>? RecordingFailed;

    /// <summary>
    /// Raw captured PCM chunks (device format), raised from the capture thread.
    /// Consumed by the realtime streaming path; only useful when
    /// <see cref="CapturesTargetFormat"/> is true (16 kHz mono).
    /// </summary>
    public event Action<byte[]>? AudioChunk;

    /// <summary>False when the capture ladder had to open a non-16k device format
    /// (48 kHz-only headsets) — realtime streaming then resamples chunks instead.</summary>
    public bool CapturesTargetFormat { get; private set; }

    /// <summary>The format the device was actually opened with (for chunk resampling).</summary>
    public WaveFormat CaptureFormat => _captureFormat;

    /// <param name="preferredDeviceName">
    /// Capture device to pin (see MicDeviceResolver); null/empty follows the
    /// Windows default endpoint, which Bluetooth headset connect/disconnect can
    /// silently flip to a dead or distant microphone.
    /// </param>
    public AudioRecorder(string? preferredDeviceName = null)
    {
        _preferredDeviceName = string.IsNullOrWhiteSpace(preferredDeviceName) ? null : preferredDeviceName;
        _watchdog = new System.Timers.Timer(2000) { AutoReset = false };
        _watchdog.Elapsed += OnWatchdogElapsed;
    }

    public bool IsRecording { get; private set; }

    public void Start()
    {
        if (IsRecording) return;
        lock (_lock)
        {
            _rawStream.SetLength(0);
            _stopRequested = false;
            _firstAudioFired = false;
        }
        _level.Reset();

        // Convert from whatever format the device accepts (AVAudioConverter parity):
        // try the target format first, then the common device-native rates. The
        // preferred device is re-resolved on every start; if it vanished (unplugged
        // interface, AirPods that left) or refuses every format, the system default
        // gets a second pass so dictation keeps working.
        var deviceNumber = ResolvePreferredDeviceNumber();
        Exception? lastError = null;
        var opened = TryOpenLadder(deviceNumber, ref lastError);
        if (!opened && deviceNumber != -1)
            opened = TryOpenLadder(-1, ref lastError);
        if (!opened)
        {
            throw new InvalidOperationException(
                $"Failed to open the microphone ({lastError?.Message ?? "no capture formats available"}).");
        }

        // One-shot watchdog, ported from startBufferWatchdog: it fires once, 2 s after
        // start, and only fails if no buffers arrived since start. A long pause later
        // in the dictation is perfectly normal and must not cancel the recording.
        lock (_lock) _bytesAtWatchdogStart = _rawStream.Length;
        _watchdog.Stop();
        _watchdog.Start();

        IsRecording = true;
    }

    private int ResolvePreferredDeviceNumber()
    {
        if (_preferredDeviceName == null) return -1;
        return MicDeviceResolver.ResolveIndex(_preferredDeviceName, MicDeviceResolver.DeviceNames());
    }

    private bool TryOpenLadder(int deviceNumber, ref Exception? lastError)
    {
        foreach (var format in CaptureLadder)
        {
            try
            {
                OpenCapture(deviceNumber, format);
                _captureFormat = format;
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                // The failed open may have left a device handle behind — release it.
                DisposeCapture();
            }
        }
        CapturesTargetFormat = _captureFormat == TargetFormat;
        return false;
    }

    private void OpenCapture(int deviceNumber, WaveFormat format)
    {
        _waveIn = new WaveInEvent
        {
            // -1 = WAVE_MAPPER: the system default input device, not a hardcoded index.
            DeviceNumber = deviceNumber,
            WaveFormat = format,
            BufferMilliseconds = 50,
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();
    }

    /// <summary>
    /// Stops capture and writes a 16 kHz mono WAV. Returns null when the recording
    /// was already cancelled or captured zero audio — the caller must not upload a
    /// header-only file (Swift: "No audio recorded").
    /// </summary>
    public string? Stop()
    {
        if (!IsRecording) return null; // already cancelled (watchdog/escape): no bogus error
        IsRecording = false;
        _watchdog.Stop();
        _waveIn?.StopRecording();
        lock (_lock) _stopRequested = true;

        long length;
        lock (_lock) length = _rawStream.Length;
        if (length == 0) return null;

        var wavPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        try
        {
            lock (_lock)
            {
                _rawStream.Position = 0;
                if (_captureFormat == TargetFormat)
                {
                    using var writer = new WaveFileWriter(wavPath, TargetFormat);
                    var buffer = new byte[length];
                    _ = _rawStream.Read(buffer, 0, buffer.Length);
                    writer.Write(buffer, 0, buffer.Length);
                }
                else
                {
                    using var rawSource = new RawSourceWaveStream(_rawStream, _captureFormat);
                    using var resampler = new MediaFoundationResampler(rawSource, TargetFormat)
                    {
                        ResamplerQuality = 60,
                    };
                    WaveFileWriter.CreateWaveFile(wavPath, resampler);
                }
            }
        }
        catch (Exception)
        {
            try { File.Delete(wavPath); } catch (Exception) { }
            throw;
        }
        return wavPath;
    }

    /// <summary>Stops capture and discards the audio (cancel path).</summary>
    public void Cancel()
    {
        if (!IsRecording) return;
        IsRecording = false;
        lock (_lock) _stopRequested = true;
        _watchdog.Stop();
        _waveIn?.StopRecording();
    }

    private void OnWatchdogElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!IsRecording) return;
        lock (_lock)
        {
            if (_rawStream.Length != _bytesAtWatchdogStart) return; // buffers are flowing
        }
        // The first capture session after a long device idle can open fine yet
        // deliver nothing. One silent re-open of the capture line wakes the
        // device and keeps the dictation alive; only a second dead window fails
        // the session for real.
        if (!_rearmed)
        {
            _rearmed = true;
            if (RearmCapture()) return;
        }
        Fail("No audio data received from the microphone");
    }

    /// <summary>Re-opens the capture ladder (same policy as <see cref="Start"/>)
    /// and re-arms the watchdog baseline. Returns false when no format opens —
    /// the caller then fails the recording as before.</summary>
    private bool RearmCapture()
    {
        DisposeCapture();
        try
        {
            var deviceNumber = ResolvePreferredDeviceNumber();
            Exception? lastError = null;
            var opened = TryOpenLadder(deviceNumber, ref lastError);
            if (!opened && deviceNumber != -1)
                opened = TryOpenLadder(-1, ref lastError);
            if (!opened) return false;
        }
        catch (Exception)
        {
            return false;
        }
        lock (_lock) _bytesAtWatchdogStart = _rawStream.Length;
        _watchdog.Stop();
        _watchdog.Start();
        return true;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        double rms;
        byte[] chunk;
        lock (_lock)
        {
            if (_stopRequested) return; // ignore stragglers after Stop/Cancel begins
            _rawStream.Write(e.Buffer, 0, e.BytesRecorded);
            rms = ComputeRms(e.Buffer, e.BytesRecorded);
            chunk = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, chunk, e.BytesRecorded);
        }
        AudioChunk?.Invoke(chunk);

        if (!_firstAudioFired && rms > 0)
        {
            // One-shot latch, like the Swift readyFired: fire exactly once per recording.
            _firstAudioFired = true;
            FirstAudioReceived?.Invoke();
        }

        LevelChanged?.Invoke(_level.Normalize(rms));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        DisposeCapture();
        if (e.Exception != null && IsRecording)
        {
            // A real device failure (unplug, driver error) beats the generic watchdog text.
            Fail($"Recording error: {e.Exception.Message}");
        }
    }

    private void Fail(string message)
    {
        if (!IsRecording) return;
        Cancel();
        RecordingFailed?.Invoke(message);
    }

    private static double ComputeRms(byte[] buffer, int count)
    {
        long sum = 0;
        var samples = count / 2;
        if (samples == 0) return 0;
        for (var i = 0; i + 1 < count; i += 2)
        {
            var sample = BitConverter.ToInt16(buffer, i);
            sum += (long)sample * sample;
        }
        return Math.Sqrt(sum / (double)samples) / 32768.0;
    }

    private void DisposeCapture()
    {
        if (_waveIn == null) return;
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.RecordingStopped -= OnRecordingStopped;
        _waveIn.Dispose();
        _waveIn = null;
    }

    public void Dispose()
    {
        // Stop first and tolerate a racing Elapsed on the timer thread.
        _watchdog.Stop();
        try { _watchdog.Dispose(); } catch (Exception) { }
        if (IsRecording) Cancel();
        DisposeCapture();
        _rawStream.Dispose();
    }
}
