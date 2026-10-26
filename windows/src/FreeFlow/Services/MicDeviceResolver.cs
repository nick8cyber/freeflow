using NAudio.Wave;

namespace FreeFlow.Services;

/// <summary>
/// Resolves the stored preferred microphone name to an MME device index at
/// recording time. MME indices are unstable (devices come and go), so settings
/// store the name (macOS pins selected_microphone_id for the same reason) and
/// every recording start re-resolves it. Falls back to the system default
/// (WAVE_MAPPER, index -1) when the preference is empty or no longer present.
/// </summary>
public static class MicDeviceResolver
{
    public const string SystemDefaultLabel = "System default";

    /// <summary>Pure lookup over device names in MME index order; -1 = system default.</summary>
    public static int ResolveIndex(string? preferredName, IReadOnlyList<string> deviceNames)
    {
        if (string.IsNullOrWhiteSpace(preferredName)) return -1;
        for (var i = 0; i < deviceNames.Count; i++)
            if (deviceNames[i].Equals(preferredName, StringComparison.OrdinalIgnoreCase)) return i;
        // MME truncates ProductName to 31 chars — tolerate either side being cut.
        for (var i = 0; i < deviceNames.Count; i++)
        {
            if (deviceNames[i].Length == 0) continue;
            if (deviceNames[i].Contains(preferredName, StringComparison.OrdinalIgnoreCase) ||
                preferredName.Contains(deviceNames[i], StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>Live MME capture device names, index-aligned with WaveInEvent device numbers.</summary>
    public static List<string> DeviceNames()
    {
        var names = new List<string>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
            names.Add(WaveInEvent.GetCapabilities(i).ProductName);
        return names;
    }
}
