using System.IO;
using System.Text.Json;

namespace FreeFlow.Core;

/// <summary>
/// Persists daily-level rate-limit cooldowns to their own locked JSON file.
/// Deliberately separate from settings.json so background persistence can never
/// serialize the settings object while the UI thread mutates it.
/// </summary>
public static class CooldownStore
{
    private static readonly object FileLock = new();
    private static Dictionary<string, double> _cache = new();
    private static bool _loaded;

    private static string FilePath => Path.Combine(SettingsStore.DirectoryPath, "cooldowns.json");

    public static DateTimeOffset? Read(string key)
    {
        lock (FileLock)
        {
            EnsureLoaded();
            return _cache.TryGetValue(key, out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds((long)unix)
                : null;
        }
    }

    public static void Write(string key, DateTimeOffset expiry)
    {
        lock (FileLock)
        {
            EnsureLoaded();
            _cache[key] = expiry.ToUnixTimeSeconds();
            Persist();
        }
    }

    public static void Clear(string key)
    {
        lock (FileLock)
        {
            EnsureLoaded();
            if (_cache.Remove(key)) Persist();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
            {
                _cache = JsonSerializer.Deserialize<Dictionary<string, double>>(
                    File.ReadAllText(FilePath), JsonOpts.Serializer) ?? new Dictionary<string, double>();
            }
        }
        catch (Exception)
        {
            _cache = new Dictionary<string, double>();
        }
    }

    private static void Persist()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(_cache, JsonOpts.Serializer));
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // Cooldown persistence is best-effort; an unwritable file only costs one extra request.
        }
    }
}
