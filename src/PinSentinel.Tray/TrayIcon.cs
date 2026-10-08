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

        // Redraw only when the picture would change; each redraw allocates a GDI icon.
        int[] heights = live ? [.. status!.Amps.Select(a => (int)Math.Round(Math.Clamp(a / 10, 0, 1) * 26))] : [0, 0, 0, 0, 0, 0];
        int[] levels = live ? [.. status!.Amps.Select(a => a >= Theme.PinLimitAmps ? 2 : a >= Theme.PinWarnAmps ? 1 : 0)] : [0, 0, 0, 0, 0, 0];
        string key = $"{_model.Connected}{live}{severity}{string.Join(',', heights)}{string.Join(',', levels)}";
        if (key != _iconKey)
        {
            _iconKey = key;
            SetIcon(Draw(heights, levels, live, severity));
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

    private static Drawing.Bitmap Draw(int[] heights, int[] levels, bool live, Severity severity)
    {
        var bitmap = new Drawing.Bitmap(32, 32);
        using var g = Drawing.Graphics.FromImage(bitmap);
        g.Clear(Drawing.Color.Transparent);

        var ok = Drawing.ColorTranslator.FromHtml("#5EEAD4");
        var warn = Drawing.ColorTranslator.FromHtml("#FBBF24");
        var bad = Drawing.ColorTranslator.FromHtml("#F87171");
        var offline = Drawing.ColorTranslator.FromHtml("#6B7280");
        var overall = severity switch { Severity.Ok => ok, Severity.Warn => warn, _ => bad };

        for (int i = 0; i < 6; i++)
        {
            int height = Math.Max(3, heights[i]);
            var color = !live ? offline : levels[i] == 2 ? bad : levels[i] == 1 ? warn : overall;
            using var brush = new Drawing.SolidBrush(color);
            g.FillRectangle(brush, 1 + i * 5, 29 - height, 4, height);
        }
        using var baseline = new Drawing.SolidBrush(live ? overall : offline);
        g.FillRectangle(baseline, 1, 30, 29, 2);
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
