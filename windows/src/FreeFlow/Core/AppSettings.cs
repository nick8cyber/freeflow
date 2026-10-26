using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeFlow.Core;

namespace FreeFlow.Core;

[Flags]
public enum HotkeyMods
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A global shortcut binding: a virtual-key code plus modifier flags.</summary>
public readonly record struct HotkeyBinding(int VirtualKey, HotkeyMods Mods)
{
    public bool IsEnabled => VirtualKey != 0;
    public string Describe() => HotkeyNaming.Describe(VirtualKey, Mods);
}

/// <summary>
/// Application settings, persisted as JSON at %APPDATA%\FreeFlow\settings.json.
/// API keys are DPAPI-encrypted at rest (CurrentUser scope) — the Windows analog
/// of the macOS app's protected .settings file.
/// </summary>
public sealed class AppSettings
{
    // --- Providers ---
    public string GeminiApiKey { get; set; } = "";
    public string GroqApiKey { get; set; } = "";
    public string GeminiBaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    public string GroqBaseUrl { get; set; } = "https://api.groq.com/openai/v1";

    // --- Models ---
    public string TranscriptionModelGemini { get; set; } = "gemini-3.5-transcribe";
    public string TranscriptionModelGroq { get; set; } = "whisper-large-v3";
    public string PostProcessingModelGemini { get; set; } = "gemini-3.5-flash-lite";
    public string PostProcessingModelGroq { get; set; } = "openai/gpt-oss-20b";
    public string PostProcessingFallbackModelGroq { get; set; } = "qwen/qwen3.6-27b";
    public string ContextModelGemini { get; set; } = "gemini-3.5-flash-lite";

    // --- Shortcuts (virtual-key codes; 0 = disabled). Default: hold CapsLock to talk,
    // Ctrl+CapsLock toggles — the Windows analog of hold-Fn / tap-Cmd-Fn. ---
    public int HoldHotkeyVk { get; set; } = 0x14;
    public HotkeyMods HoldHotkeyMods { get; set; } = HotkeyMods.None;
    public int ToggleHotkeyVk { get; set; } = 0x14;
    public HotkeyMods ToggleHotkeyMods { get; set; } = HotkeyMods.Ctrl;
    public int PasteAgainHotkeyVk { get; set; } = 0;
    public HotkeyMods PasteAgainHotkeyMods { get; set; } = HotkeyMods.None;
    public bool SuppressBoundKey { get; set; } = true;

    // --- Behavior ---
    // Preferred capture device. Empty = follow the Windows default endpoint, which
    // is a lottery when Bluetooth headsets connect and leave; a name pins the input
    // (macOS parity: selected_microphone_id). Resolved to an index at recording time.
    public string MicDeviceName { get; set; } = "";
    // Gemini Live streaming (native BidiGenerateContent WebSocket): the transcript
    // arrives while the key is held and has its own, much larger free-tier quota
    // than the 25-requests-per-day generateContent bucket. On any Live failure the
    // session falls back to record-then-transcribe.
    public bool RealtimeStreamingEnabled { get; set; } = true;
    public string RealtimeStreamingModel { get; set; } = "gemini-3.5-transcribe-live";
    // LLM cleanup applies to Groq-mode transcripts only (Google transcripts paste
    // as-is per user policy).
    public bool CleanupGroqTranscripts { get; set; } = true;
    public bool ContextEnabled { get; set; } = true;
    public bool IncludeScreenshot { get; set; } = false;
    public bool PreserveClipboard { get; set; } = true;
    public bool KeepHistory { get; set; } = true;
    public bool KeepInClipboardHistory { get; set; } = false;
    public bool AlertSounds { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool PressEnterEnabled { get; set; } = true;
    public bool PreserveExactWording { get; set; } = false;
    public bool CommandModeEnabled { get; set; } = false;
    public string TranscriptionLanguage { get; set; } = "";
    public string OutputLanguage { get; set; } = "";
    public string CustomVocabulary { get; set; } = "";
    public string CustomSystemPrompt { get; set; } = "";
    public string CustomContextPrompt { get; set; } = "";
    public Dictionary<string, string> VoiceMacros { get; set; } = new();

    // --- Timeouts ---
    public int TranscriptionTimeoutSeconds { get; set; } = 20;
    public int PostProcessingTimeoutSeconds { get; set; } = 20;
    public int ContextTimeoutSeconds { get; set; } = 20;

    // --- Misc ---
    public bool HasCompletedSetup { get; set; } = false;

    /// <summary>
    /// Spoken (transcription) language options — macOS parity, 30 entries.
    /// TranscriptionLanguage stores the ISO code (Whisper/Gemini language fields need it).
    /// </summary>
    public static readonly (string Code, string Display)[] TranscriptionLanguages =
    {
        ("", "Auto-detect"), ("en", "English"), ("es", "Spanish"), ("fr", "French"),
        ("de", "German"), ("it", "Italian"), ("pt", "Portuguese"), ("nl", "Dutch"),
        ("ru", "Russian"), ("ja", "Japanese"), ("ko", "Korean"), ("zh", "Chinese"),
        ("ar", "Arabic"), ("hi", "Hindi"), ("tr", "Turkish"), ("pl", "Polish"),
        ("uk", "Ukrainian"), ("sv", "Swedish"), ("no", "Norwegian"), ("da", "Danish"),
        ("fi", "Finnish"), ("cs", "Czech"), ("el", "Greek"), ("he", "Hebrew"),
        ("vi", "Vietnamese"), ("th", "Thai"), ("id", "Indonesian"), ("ro", "Romanian"),
        ("hu", "Hungarian"), ("ca", "Catalan"),
    };

    /// <summary>
    /// Output-language options — macOS parity. OutputLanguage stores the display NAME
    /// (cleanup/command/verbatim prompts embed the value verbatim), "" = auto-detect.
    /// </summary>
    public static readonly string[] OutputLanguageNames =
    {
        "Auto-detect", "English", "Spanish", "French", "German", "Italian", "Portuguese",
        "Chinese (Simplified)", "Chinese (Traditional)", "Japanese", "Russian",
    };

    /// <summary>Anything outside the catalog falls back to Auto-detect (port of normalizeTranscriptionLanguage).</summary>
    internal static string NormalizeModel(string? value, string fallback)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? fallback : trimmed;
    }

    internal static string NormalizeTranscriptionLanguage(string? value) =>
        value != null && TranscriptionLanguages.Any(t => t.Code == value) ? value : "";

    private static readonly Dictionary<string, string> LegacyOutputLanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["es"] = "Spanish", ["fr"] = "French", ["de"] = "German",
        ["it"] = "Italian", ["pt"] = "Portuguese", ["zh"] = "Chinese (Simplified)",
        ["zh-cn"] = "Chinese (Simplified)", ["zh-tw"] = "Chinese (Traditional)",
        ["ja"] = "Japanese", ["ru"] = "Russian",
    };

    /// <summary>
    /// One-time migration: OutputLanguage used to store ISO codes; map them to display
    /// names. Values that are neither a current name nor a legacy code become "".
    /// </summary>
    internal static string MigrateOutputLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (OutputLanguageNames.Contains(value)) return value;
        return LegacyOutputLanguageCodes.TryGetValue(value, out var name) ? name : "";
    }

    [JsonIgnore]
    public string SystemPrompt =>
        string.IsNullOrWhiteSpace(CustomSystemPrompt) ? Prompts.DefaultSystemPrompt : CustomSystemPrompt;

    [JsonIgnore]
    public string ContextPrompt =>
        string.IsNullOrWhiteSpace(CustomContextPrompt) ? Prompts.DefaultContextPrompt : CustomContextPrompt;
}

public static class SettingsStore
{
    private const string DpapiPrefix = "dpapi:";
    private static readonly object FileLock = new();

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeFlow");

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        lock (FileLock)
        {
            try
            {
                if (!File.Exists(FilePath)) return new AppSettings();
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts.Serializer) ?? new AppSettings();
                settings.GeminiApiKey = Unprotect(settings.GeminiApiKey);
                settings.GroqApiKey = Unprotect(settings.GroqApiKey);
                // One-line migration: installs before the Gemini 3.5 Transcribe switch stored
                // the old generic chat model as the (unchanged) default.
                if (settings.TranscriptionModelGemini == "gemini-2.5-flash")
                    settings.TranscriptionModelGemini = "gemini-3.5-transcribe";
                // Same for the cleanup/context defaults that predate the 3.5 generation.
                if (settings.PostProcessingModelGemini == "gemini-2.5-flash-lite")
                    settings.PostProcessingModelGemini = "gemini-3.5-flash-lite";
                if (settings.ContextModelGemini == "gemini-2.5-flash-lite")
                    settings.ContextModelGemini = "gemini-3.5-flash-lite";
                // Language migrations: transcription codes are normalized against the
                // macOS 30-language catalog; output language now stores display names.
                settings.TranscriptionLanguage = AppSettings.NormalizeTranscriptionLanguage(settings.TranscriptionLanguage);
                settings.TranscriptionModelGemini = AppSettings.NormalizeModel(settings.TranscriptionModelGemini, "gemini-3.5-transcribe");
                settings.PostProcessingModelGemini = AppSettings.NormalizeModel(settings.PostProcessingModelGemini, "gemini-3.5-flash-lite");
                settings.ContextModelGemini = AppSettings.NormalizeModel(settings.ContextModelGemini, "gemini-3.5-flash-lite");
                settings.TranscriptionModelGroq = AppSettings.NormalizeModel(settings.TranscriptionModelGroq, "whisper-large-v3");
                settings.PostProcessingModelGroq = AppSettings.NormalizeModel(settings.PostProcessingModelGroq, "openai/gpt-oss-20b");
                settings.PostProcessingFallbackModelGroq = AppSettings.NormalizeModel(settings.PostProcessingFallbackModelGroq, "qwen/qwen3.6-27b");
                settings.OutputLanguage = AppSettings.MigrateOutputLanguage(settings.OutputLanguage);
                return settings;
            }
            catch (Exception)
            {
                // A corrupt settings file must not take the app down; start fresh.
                return new AppSettings();
            }
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(DirectoryPath);
            var copy = Clone(settings);
            copy.GeminiApiKey = Protect(settings.GeminiApiKey);
            copy.GroqApiKey = Protect(settings.GroqApiKey);
            var json = JsonSerializer.Serialize(copy, JsonOpts.Serializer);
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, FilePath, overwrite: true);
        }
    }

    private static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, JsonOpts.Serializer), JsonOpts.Serializer)!;

    private static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return DpapiPrefix + Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return stored;
        try
        {
            var bytes = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception)
        {
            // Undecryptable (e.g. moved between user profiles) — treat as absent.
            return "";
        }
    }
}

/// <summary>Human-readable names for virtual keys and modifier combos.</summary>
public static class HotkeyNaming
{
    public static string Describe(int vk, HotkeyMods mods)
    {
        if (vk == 0) return "Disabled";
        var parts = new List<string>();
        if (mods.HasFlag(HotkeyMods.Ctrl)) parts.Add("Ctrl");
        if (mods.HasFlag(HotkeyMods.Alt)) parts.Add("Alt");
        if (mods.HasFlag(HotkeyMods.Shift)) parts.Add("Shift");
        if (mods.HasFlag(HotkeyMods.Win)) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    public static string KeyName(int vk) => vk switch
    {
        0x14 => "Caps Lock",
        0x91 => "Scroll Lock",
        0x2D => "Insert",
        0x13 => "Pause",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0xA2 => "Left Ctrl",
        0xA3 => "Right Ctrl",
        0xA4 => "Left Alt",
        0xA5 => "Right Alt",
        0xA0 => "Left Shift",
        0xA1 => "Right Shift",
        0x5B => "Left Win",
        0x5C => "Right Win",
        _ => ((char)vk).ToString(),
    };
}
