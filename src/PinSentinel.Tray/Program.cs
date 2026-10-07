using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PinSentinel.Core;

namespace PinSentinel.Tray;

static class Program
{
    // --demo [fault]            synthetic data instead of the service
    // --screenshot <file.png>   render the demo dashboard to a PNG and exit
    [STAThread]
    static int Main(string[] args)
    {
        bool fault = args.Contains("fault");
        int shot = Array.IndexOf(args, "--screenshot");
        var model = new DashboardModel();

        if (shot >= 0 && shot + 1 < args.Length)
        {
            var demo = new DemoFeed(fault);
            for (int i = 0; i < 520; i++) model.Apply(demo.Next());
            Render(new DashboardWindow(model) { HideWhenDeactivated = false }, args[shot + 1]);
            return 0;
        }

        using var mutex = new Mutex(true, "PinSentinel.Tray", out bool first);
        if (!first) return 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new DashboardWindow(model);
        using var stop = new CancellationTokenSource();
        using var tray = new TrayIcon(model, window.Toggle, app.Shutdown);

        IStatusFeed feed = args.Contains("--demo") ? new DemoFeed(fault) : new PipeFeed();
        feed.Received += message => app.Dispatcher.BeginInvoke(() =>
        {
            if (message is null) model.Disconnected(); else model.Apply(message);
            tray.Update();
            if (window.IsVisible) window.Refresh();
        });
        feed.Start(stop.Token);

        int code = app.Run();
        stop.Cancel();
        return code;
    }

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
