namespace Remco;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var single = new Mutex(true, "UOR-RC.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("UOR-RC is already running (look in the system tray).",
                "UOR-RC", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Per-monitor DPI awareness: cursor positions and window rectangles are real pixels,
        // so the laser / pen lands exactly where you touch on the phone.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // a small "starting…" window, so clicking the exe always shows something at once
        var splash = new SplashForm();
        splash.Show();
        Application.DoEvents();

        var main = new MainForm(Settings.Load());
        main.Shown += (_, _) =>
        {
            try { splash.Close(); splash.Dispose(); } catch { }
            main.Activate();
        };
        Application.Run(main);
    }
}
