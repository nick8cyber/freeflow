using FreeFlow.Services;
using Xunit;

namespace FreeFlow.Tests;

public class MicDeviceResolverTests
{
    // Synthetic MME list — real ProductNames are truncated to 31 chars by waveInOpen.
    private static readonly string[] Devices =
    [
        "Микрофон (Logi C310 HD WebCam)",
        "Stereo Mix (Realtek High Defini",
        "Микрофон (Scarlett Solo 4th Gen",
    ];

    [Fact]
    public void EmptyPreferenceReturnsSystemDefault()
    {
        Assert.Equal(-1, MicDeviceResolver.ResolveIndex(null, Devices));
        Assert.Equal(-1, MicDeviceResolver.ResolveIndex("", Devices));
        Assert.Equal(-1, MicDeviceResolver.ResolveIndex("   ", Devices));
    }

    [Fact]
    public void ExactMatchIsCaseInsensitive()
    {
        Assert.Equal(1, MicDeviceResolver.ResolveIndex("stereo mix (realtek high defini", Devices));
    }

    [Fact]
    public void StoredFullNameMatchesTruncatedMmeName()
    {
        // Settings store the full device name; MME reports a 31-char prefix of it.
        Assert.Equal(2, MicDeviceResolver.ResolveIndex("Микрофон (Scarlett Solo 4th Gen USB)", Devices));
    }

    [Fact]
    public void UnknownDeviceFallsBackToSystemDefault()
    {
        // A Bluetooth headset that left the machine must not break dictation.
        Assert.Equal(-1, MicDeviceResolver.ResolveIndex("AirPods Pro (Никита)", Devices));
    }
}
