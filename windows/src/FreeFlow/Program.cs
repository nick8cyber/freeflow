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

        if (overlayDemo)
        {
            // Dev helper: cycles the overlay through its states without hotkeys/tray.
            // Runs before the single-instance check so it can be captured while the
            // real app lives in the tray.
            RunOverlayDemo();
            return;
        }

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

        var tray = new TrayContext(settings);
        if (showSettings) tray.ShowSettingsWindow();
        Application.Run(tray);
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Dev helper: cycles the overlay through recording (with a synthetic
    /// transcript), status and error states, then exits. Text is invented — never
    /// a real dictation.</summary>
    private static void RunOverlayDemo()
    {
        var overlay = new OverlayForm();
        overlay.ShowRecording();
        overlay.UpdateTargetApp("Telegram");

        var timer = new WinFormsTimer { Interval = 50 };
        int elapsed = 0;
        timer.Tick += (_, _) =>
        {
            elapsed += timer.Interval;
            if (elapsed <= 2600)
            {
                if (elapsed == 900)
                {
                    overlay.UpdateTranscript("The quick brown fox jumps over the lazy dog. " +
                        "Pack my box with five dozen liquor jugs. Sphinx of black quartz, judge my vow.");
                }
                overlay.UpdateLevel(0.3 + 0.6 * Math.Abs(Math.Sin(elapsed / 220.0)));
            }
            else if (elapsed == 2700) overlay.ShowStatus("Transcribing…");
            else if (elapsed == 4200) overlay.ShowStatus("Polishing with app context…");
            else if (elapsed == 5600) overlay.HideAfterDelay();
            else if (elapsed == 7600) overlay.ShowError("Demo error: Gemini is unreachable (network)");
            else if (elapsed >= 12200)
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
