using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PinSentinel.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PinSentinel.Tray;

/// <summary>Notification-area icon: six live bars, a tooltip, alerts and a menu.</summary>
sealed class TrayIcon : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PinSentinel";

    private readonly DashboardModel _model;
    private readonly Forms.NotifyIcon _icon = new() { Visible = true, Text = "PinSentinel" };
    private Drawing.Icon? _current;
    private string _iconKey = "";
    private Severity _lastSeverity;

    public TrayIcon(DashboardModel model, Action toggleWindow, Action exit)
    {
        _model = model;
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) toggleWindow(); };

        var startup = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = StartsWithWindows };
        startup.CheckedChanged += (_, _) => StartsWithWindows = startup.Checked;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => toggleWindow());
        menu.Items.Add("Open log folder", null, (_, _) => OpenLogs());
        menu.Items.Add("Run throttle test (20 s)...", null, async (_, _) => await ThrottleTest());
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        Update();
    }

    public void Update()
    {
        var status = _model.Status;
        bool live = _model.HasPins;
        var severity = live ? status!.Severity : Severity.Ok;

        // Square-root scale, so idle currents still show as visible bars.
        // Redraw only when the picture would change; each redraw allocates a GDI icon.
        int[] heights = live ? [.. status!.Amps.Select(a => 4 + (int)Math.Round(Math.Sqrt(Math.Clamp(a / 10, 0, 1)) * 18))] : [4, 4, 4, 4, 4, 4];
        double hottest = live ? status!.Amps.Max() : 0;
        int level = !live ? -1
            : severity >= Severity.Throttle || hottest >= Theme.PinLimitAmps ? 2
            : severity == Severity.Warn || hottest >= Theme.PinWarnAmps ? 1 : 0;
        string key = $"{level}:{string.Join(',', heights)}";
        if (key != _iconKey)
        {
            _iconKey = key;
            SetIcon(Draw(heights, level));
        }

        string text = !_model.Connected ? "PinSentinel: service not running"
            : !live ? "PinSentinel: sensor not readable"
            : $"PinSentinel: {severity}\n{_model.Watts:F0} W, max pin {_model.MaxAmps:F2} A";
        if (_icon.Text != text) _icon.Text = text;

        if (live && severity > _lastSeverity)
        {
            string message = string.Join("\n", status!.Findings.Where(f => f.Severity == severity).Select(f => f.Message));
            _icon.ShowBalloonTip(10_000, $"GPU power connector: {severity}", message,
                severity == Severity.Warn ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Error);
        }
        _lastSeverity = severity;
    }

    /// <summary>
    /// A solid tile in the status colour with the six pin bars cut out in dark. The filled tile
    /// stays visible on any taskbar and gives the whole icon cell something to click.
    /// </summary>
    internal static Drawing.Bitmap Draw(int[] heights, int level)
    {
        var bitmap = new Drawing.Bitmap(32, 32);
        using var g = Drawing.Graphics.FromImage(bitmap);
        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Drawing.Color.Transparent);

        var color = Drawing.ColorTranslator.FromHtml(level switch { 0 => "#5EEAD4", 1 => "#FBBF24", 2 => "#F87171", _ => "#9CA3AF" });
        using var tile = new Drawing.Drawing2D.GraphicsPath();
        const int d = 12;
        tile.AddArc(0, 0, d, d, 180, 90);
        tile.AddArc(31 - d, 0, d, d, 270, 90);
        tile.AddArc(31 - d, 31 - d, d, d, 0, 90);
        tile.AddArc(0, 31 - d, d, d, 90, 90);
        tile.CloseFigure();
        using var fill = new Drawing.SolidBrush(color);
        g.FillPath(fill, tile);

        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.None;
        using var bars = new Drawing.SolidBrush(Drawing.ColorTranslator.FromHtml("#0E1014"));
        for (int i = 0; i < 6; i++)
            g.FillRectangle(bars, 5 + i * 4, 27 - heights[i], 3, heights[i]);
        return bitmap;
    }

    private void SetIcon(Drawing.Bitmap bitmap)
    {
        using (bitmap)
        {
            var next = Drawing.Icon.FromHandle(bitmap.GetHicon());
            _icon.Icon = next;
            if (_current is not null) DestroyIcon(_current.Handle);
            _current = next;
        }
    }

    private static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is not null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunValue, false);
        }
    }

    private async Task ThrottleTest()
    {
        var answer = Forms.MessageBox.Show(
            "This cuts GPU power for 20 seconds, then restores it, to prove the throttle works on this card.\n\n" +
            "Start a game or benchmark first so the GPU is under load. Expect a heavy frame-rate drop while it runs. " +
            "The result appears in a message when it finishes.",
            "PinSentinel throttle test", Forms.MessageBoxButtons.OKCancel, Forms.MessageBoxIcon.Warning);
        if (answer != Forms.DialogResult.OK) return;

        string? reply = await ControlClient.Send("throttle-test");
        if (reply != "ok")
            _icon.ShowBalloonTip(8000, "Throttle test not started", reply ?? "The PinSentinel service is not reachable.", Forms.ToolTipIcon.Warning);
    }

    private static void OpenLogs()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PinSentinel");
        if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_current is not null) DestroyIcon(_current.Handle);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
