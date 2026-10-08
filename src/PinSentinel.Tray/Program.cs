using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PinSentinel.Core;

namespace PinSentinel.Tray;

static class Program
{
    // --demo [fault]            synthetic data instead of the service
    // --show                    open the dashboard at startup
    // --icon <file.png>         render the tray icon states to a PNG and exit
    // --screenshot <file.png>   render the demo dashboard to a PNG and exit (add 'health' for that tab)
    [STAThread]
    static int Main(string[] args)
    {
        bool fault = args.Contains("fault");
        int shot = Array.IndexOf(args, "--screenshot");
        var model = new DashboardModel();

        int icon = Array.IndexOf(args, "--icon");
        if (icon >= 0 && icon + 1 < args.Length)
        {
            // Tray icon states side by side: idle, gaming load, warning, fault, offline.
            using var sheet = new System.Drawing.Bitmap(5 * 40, 32);
            using var g = System.Drawing.Graphics.FromImage(sheet);
            (int[] Heights, int Level)[] states = [([10, 10, 10, 10, 10, 10], 0), ([18, 19, 19, 20, 19, 19], 0), ([20, 21, 22, 21, 21, 21], 1), ([20, 15, 22, 21, 21, 21], 2), ([4, 4, 4, 4, 4, 4], -1)];
            for (int i = 0; i < states.Length; i++)
            {
                using var state = TrayIcon.Draw(states[i].Heights, states[i].Level);
                g.DrawImage(state, i * 40, 0);
            }
            sheet.Save(args[icon + 1]);
            return 0;
        }

        if (shot >= 0 && shot + 1 < args.Length)
        {
            var source = new DemoFeed(fault);
            for (int i = 0; i < 520; i++) model.Apply(source.Next());
            model.Health = DemoFeed.Health(fault);
            var preview = new DashboardWindow(model, opaque: true) { HideWhenDeactivated = false, UseService = false };
            preview.Select(health: args.Contains("health"));
            Render(preview, args[shot + 1]);
            return 0;
        }

        bool demo = args.Contains("--demo");
        using var mutex = new Mutex(true, demo ? "PinSentinel.Tray.Demo" : "PinSentinel.Tray", out bool first);
        if (!first) return 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (demo) model.Health = DemoFeed.Health(fault);

        // The dashboard only exists while it is open, so the idle tray app carries no window or render state.
        DashboardWindow? window = null;
        var closedAt = DateTime.MinValue;
        void Toggle()
        {
            if (window is not null) { window.Close(); return; }
            // Clicking the tray icon to dismiss the flyout deactivates it first; do not reopen on that same click.
            if ((DateTime.UtcNow - closedAt).TotalMilliseconds < 300) return;

            window = new DashboardWindow(model) { UseService = !demo };
            window.Closed += (_, _) =>
            {
                window = null;
                closedAt = DateTime.UtcNow;
                app.Dispatcher.BeginInvoke(TrimMemory, DispatcherPriority.ApplicationIdle);
            };
            window.ShowNearTray();
        }

        using var stop = new CancellationTokenSource();
        using var tray = new TrayIcon(model, Toggle, app.Shutdown);

        IStatusFeed feed = demo ? new DemoFeed(fault) : new PipeFeed();
        feed.Received += message => app.Dispatcher.BeginInvoke(() =>
        {
            if (message is null) model.Disconnected(); else model.Apply(message);
            tray.Update();
            window?.Refresh();
        });
        feed.Start(stop.Token);
        if (args.Contains("--show")) app.Dispatcher.BeginInvoke(Toggle);
        app.Dispatcher.BeginInvoke(TrimMemory, DispatcherPriority.ApplicationIdle);

        int code = app.Run();
        stop.Cancel();
        return code;
    }

    /// <summary>Returns what the closed dashboard and startup used to the system.</summary>
    private static void TrimMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);

    private static void Render(DashboardWindow window, string path)
    {
        window.ShowActivated = false;
        window.Left = -10000;
        window.Show();
        window.Refresh();
        window.UpdateLayout();

        const double scale = 2;
        var root = window.Root;
        var bitmap = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        window.Close();
    }
}
