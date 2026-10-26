using System.IO;
using System.Text.Json;
using FreeFlow.Core;

namespace FreeFlow.Services;

public sealed record HistoryEntry(
    DateTime Timestamp,
    string AppName,
    string RawTranscript,
    string FinalText,
    string Status,
    string ProviderUsed);

/// <summary>Local pipeline history (last 20 entries, text only — audio is never stored).</summary>
public sealed class HistoryStore
{
    private const int MaxEntries = 20;

    private static string FilePath => Path.Combine(SettingsStore.DirectoryPath, "history.json");

    public List<HistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<HistoryEntry>>(json, JsonOpts.Serializer) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public void Append(HistoryEntry entry)
    {
        try
        {
            var entries = Load();
            entries.Insert(0, entry);
            if (entries.Count > MaxEntries) entries = entries.Take(MaxEntries).ToList();
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(entries, JsonOpts.Serializer));
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // History is best-effort and must never break dictation.
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception)
        {
        }
    }
}
