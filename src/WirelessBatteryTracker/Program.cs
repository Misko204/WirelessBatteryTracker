using System.Text;
using Microsoft.Toolkit.Uwp.Notifications;

namespace BatteryTray;

static class Program
{
    const string MutexName = @"Local\WirelessBatteryTracker.SingleInstance";
    const string ShowEventName = @"Local\WirelessBatteryTracker.ShowSettings";
    const string ExitEventName = @"Local\WirelessBatteryTracker.Exit";
    const string FlyoutEventName = @"Local\WirelessBatteryTracker.Flyout";

    /// <summary>"--exit" asks a running instance to close cleanly (removing its tray icons). Used by the installer.</summary>
    static int RequestExit()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ExitEventName);
            ev.Set();
        }
        catch { return 0; } // not running
        // Wait for the running instance to release its single-instance mutex.
        for (int i = 0; i < 50; i++)
        {
            try { using var m = Mutex.OpenExisting(MutexName); }
            catch (WaitHandleCannotBeOpenedException) { return 0; }
            Thread.Sleep(100);
        }
        return 1;
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--diag")) return RunDiagnostics();
        if (args.Length == 2 && args[0] == "--make-assets")
        {
            // Build-time helper: app.ico and installer images from Assets/logo.png.
            LogoRenderer.SaveIco(Path.Combine(args[1], "app.ico"));
            LogoRenderer.SaveWizardImages(args[1]);
            return 0;
        }
        if (args.Contains("--exit")) return RequestExit();

        using var mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running (e.g. started again from the Start menu, or a toast was clicked): open Settings,
            // or the battery flyout with --flyout.
            try { using var ev = EventWaitHandle.OpenExisting(args.Contains("--flyout") ? FlyoutEventName : ShowEventName); ev.Set(); } catch { }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("UI exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Fatal: " + e.ExceptionObject);

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        // Install the UI-thread dispatcher before anything captures SynchronizationContext.Current
        // (the taskbar strip and device monitor post their events back through it).
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var app = new TrayApp();
        var ui = SynchronizationContext.Current!;
        ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => ui.Post(_ => app.OpenSettings(), null), null, -1, false);
        ToastNotificationManagerCompat.OnActivated += _ => ui.Post(_ => app.OpenSettings(), null);
        using var flyoutEvent = new EventWaitHandle(false, EventResetMode.AutoReset, FlyoutEventName);
        ThreadPool.RegisterWaitForSingleObject(flyoutEvent, (_, _) => ui.Post(_ => app.ShowFlyoutAtTaskbar(), null), null, -1, false);
        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => ui.Post(_ => app.ExitThread(), null), null, -1, true);

        // Opened from the Start menu by hand (not at sign-in): show Settings so the user sees something happen.
        if (args.Contains("--settings")) ui.Post(_ => app.OpenSettings(), null);

        Application.Run(app);
        return 0;
    }

    /// <summary>"--diag" polls every source once and writes diag.txt plus a sample icons.png.</summary>
    static int RunDiagnostics()
    {
        Providers.LogitechHidppProvider.Verbose = true;
        var sb = new StringBuilder();
        var monitor = new DeviceMonitor(() => new AppSettings());
        // First poll starts the AirPods advert listener; give it time to hear broadcasts, then poll again.
        monitor.PollOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
        Thread.Sleep(10000);
        var result = monitor.PollOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
        sb.AppendLine($"Polled sources: {string.Join(", ", result.PolledSources)}");
        foreach (var r in result.Readings)
            sb.AppendLine($"{r.Source,-12} {r.Id,-16} {r.Name,-32} kind={r.Kind} level={r.Level?.ToString() ?? "-"} connected={r.IsConnected} charging={r.IsCharging?.ToString() ?? "?"} {r.Detail}");
        if (result.Readings.Count == 0) sb.AppendLine("No devices found.");
        Directory.CreateDirectory(SettingsStore.Folder);
        File.WriteAllText(Path.Combine(SettingsStore.Folder, "diag.txt"), sb.ToString());

        // Sample sheet: fallback square tray icons (4x) and the app tile at small sizes.
        var samples = new (DeviceKind kind, int? lvl, bool conn, bool chg)[]
        {
            (DeviceKind.Mouse, 30, true, false), (DeviceKind.Keyboard, 55, true, false),
            (DeviceKind.Headphones, 100, true, false), (DeviceKind.Controller, 15, true, true), (DeviceKind.Mouse, 30, false, false),
        };
        int sz = IconRenderer.IconSize, scale = 4, cell = sz * scale;
        using var sheet = new Bitmap(samples.Length * (cell + 12) + 400, Math.Max(cell, 256) + 16);
        using (var g = Graphics.FromImage(sheet))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.Clear(Color.FromArgb(28, 28, 28));
            for (int i = 0; i < samples.Length; i++)
            {
                var s = samples[i];
                using var ic = IconRenderer.RenderCombined(s.kind, s.lvl, s.conn, s.chg, 20);
                using var b = ic.Icon.ToBitmap();
                g.DrawImage(b, i * (cell + 12) + 8, 8, cell, cell);
            }
            int x0 = samples.Length * (cell + 12) + 16;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            foreach (var t in new[] { 16, 24, 32, 48, 128 })
            {
                using var tile = LogoRenderer.RenderTile(t);
                g.DrawImage(tile, x0, 8, t, t);
                x0 += t + 10;
            }
        }
        sheet.Save(Path.Combine(SettingsStore.Folder, "icons.png"));

        // Taskbar strip preview at 125% scaling, dark and light taskbar, first item hovered.
        var strip = new[]
        {
            new StripItem("a", DeviceKind.Mouse, 30, true, false, 20),
            new StripItem("b", DeviceKind.Keyboard, 55, true, false, 20),
            new StripItem("c", DeviceKind.Headphones, 80, true, true, 20),
            new StripItem("d", DeviceKind.Controller, 15, true, false, 20),
            new StripItem("e", DeviceKind.Mouse, 100, false, false, 20),
        };
        using var dark = TaskbarStrip.Preview(strip, true, 1.25f, 60, false, hover: 0);
        using var lightBar = TaskbarStrip.Preview(strip, true, 1.25f, 60, true);
        using var both = new Bitmap(dark.Width, dark.Height * 2);
        using (var g = Graphics.FromImage(both)) { g.DrawImage(dark, 0, 0); g.DrawImage(lightBar, 0, dark.Height); }
        both.Save(Path.Combine(SettingsStore.Folder, "strip.png"));
        return 0;
    }
}
