using FreeFlow.Core;
using FreeFlow.Services;
using FreeFlow.UI;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace FreeFlow;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        bool showSettings = HasFlag(args, "--settings");
        bool forceSetup = HasFlag(args, "--setup");
        bool overlayDemo = HasFlag(args, "--demo-overlay");

        using var mutex = new Mutex(true, "FreeFlow.Windows.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("FreeFlow is already running. Check the system tray.", "FreeFlow",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var settings = SettingsStore.Load();

        if (forceSetup || !settings.HasCompletedSetup)
        {
            using var setup = new SetupForm(settings);
            if (setup.ShowDialog() != DialogResult.OK) return;
            settings.HasCompletedSetup = true;
            SettingsStore.Save(settings);
        }

        if (overlayDemo)
        {
            // Dev helper: cycles the overlay through its states without hotkeys/tray.
            RunOverlayDemo();
            return;
        }

        var tray = new TrayContext(settings);
        if (showSettings) tray.ShowSettingsWindow();
        Application.Run(tray);
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Dev helper: cycles the overlay through recording/status/error states, then exits.</summary>
    private static void RunOverlayDemo()
    {
        var overlay = new OverlayForm();
        overlay.ShowRecording();

        var timer = new WinFormsTimer { Interval = 50 };
        int elapsed = 0;
        timer.Tick += (_, _) =>
        {
            elapsed += timer.Interval;
            if (elapsed <= 2200)
            {
                overlay.UpdateLevel(0.3 + 0.6 * Math.Abs(Math.Sin(elapsed / 220.0)));
            }
            else if (elapsed == 2300) overlay.ShowStatus("Transcribing…");
            else if (elapsed == 3500) overlay.ShowStatus("Polishing with app context…");
            else if (elapsed == 4500) overlay.HideAfterDelay();
            else if (elapsed == 5900) overlay.ShowError("Demo error: Gemini is unreachable (network)");
            else if (elapsed >= 10800)
            {
                timer.Stop();
                overlay.Dispose();
                Application.Exit();
            }
        };
        timer.Start();
        Application.Run(overlay);
    }
}
