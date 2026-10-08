using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using PinSentinel.Core;

namespace PinSentinel.Tray;

/// <summary>The flyout shown from the tray icon: a Live tab and a Health tab over an acrylic backdrop.</summary>
sealed class DashboardWindow : Window
{
    private readonly DashboardModel _model;
    private readonly AnimatedView[] _views;
    private readonly TextBlock _title = Text(14, Theme.Text, FontWeights.SemiBold);
    private readonly TextBlock _state = Text(11.5, Theme.Dim);
    private readonly Border _banner;
    private readonly TextBlock _bannerText = Text(11.5, Theme.Text);
    private readonly Dictionary<string, TextBlock> _stats = [];
    private readonly StackPanel _live = new(), _health = new();
    private readonly Border _liveTab, _healthTab;
    private readonly TextBlock _verdict = Text(26, Theme.Text, FontWeights.Light);
    private readonly TextBlock _healthSummary = Text(11.5, Theme.Dim);
    private readonly Border _switch, _knob;
    private readonly TextBlock _switchLabel = Text(11.5, Theme.Dim);
    private DateTime _healthLoaded = DateTime.MinValue;

    public Border Root { get; }
    public bool HideWhenDeactivated { get; set; } = true;
    /// <summary>False for demo and screenshot runs, where there are no logs or service to talk to.</summary>
    public bool UseService { get; init; } = true;

    public DashboardWindow(DashboardModel model, bool opaque = false)
    {
        _model = model;
        Title = "PinSentinel";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        FontFamily = Theme.Sans;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            GlassFrameThickness = new Thickness(-1),
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });

        var gauge = new PowerGauge(model);
        var pins = new PinColumns(model) { Margin = new Thickness(0, 14, 0, 0) };
        var graph = new HistoryGraph(model);
        var healthView = new HealthView(model) { Margin = new Thickness(0, 16, 0, 0) };
        _views = [gauge, pins, graph, healthView];

        // Header: title and one-line state on the left, tab switch on the right.
        var titles = new StackPanel();
        titles.Children.Add(_title);
        titles.Children.Add(_state);
        _liveTab = Tab("Live", () => Select(health: false));
        _healthTab = Tab("Health", () => Select(health: true));
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        tabs.Children.Add(_liveTab);
        tabs.Children.Add(_healthTab);
        var tabPill = new Border { Background = Theme.Surface, CornerRadius = new CornerRadius(9), Padding = new Thickness(2), Child = tabs, VerticalAlignment = VerticalAlignment.Top };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(tabPill, Dock.Right);
        header.Children.Add(tabPill);
        header.Children.Add(titles);

        _bannerText.TextWrapping = TextWrapping.Wrap;
        _banner = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 9), Margin = new Thickness(0, 0, 0, 14), Child = _bannerText };

        // Live tab.
        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(26, 0, 0, 10) };
        side.Children.Add(Stat("Imbalance", 22));
        side.Children.Add(Stat("Hottest pin", 22));
        side.Children.Add(Stat("Voltage spread", 22));
        var hero = new DockPanel();
        DockPanel.SetDock(gauge, Dock.Left);
        hero.Children.Add(gauge);
        hero.Children.Add(side);
        foreach (UIElement element in (UIElement[])[hero, pins, Caption("Per-pin current, last 5 min"), graph, Rule(),
                                                    Row("GPU temp", "GPU load", "VRAM", "Fan")])
            _live.Children.Add(element);

        // Health tab.
        _healthSummary.TextWrapping = TextWrapping.Wrap;
        _healthSummary.Margin = new Thickness(0, 2, 0, 0);
        foreach (UIElement element in (UIElement[])[Caption("Connector health", topMargin: 0), _verdict, _healthSummary, healthView, Rule(),
                                                    Caption("Since this app started", topMargin: 0),
                                                    Row("Peak pin", "Peak power", "Peak imbalance", "Energy")])
            _health.Children.Add(element);

        // Footer: armed switch and test alert.
        _knob = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(3, 0, 3, 0) };
        _switch = new Border { Width = 36, Height = 20, CornerRadius = new CornerRadius(10), Child = _knob, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        _switch.MouseLeftButtonDown += (_, e) => { e.Handled = true; ToggleArmed(); };
        _switchLabel.Margin = new Thickness(10, 0, 0, 0);
        _switchLabel.VerticalAlignment = VerticalAlignment.Center;
        var test = Text(11.5, Theme.Accent);
        test.Text = "Send test alert";
        test.Cursor = Cursors.Hand;
        test.VerticalAlignment = VerticalAlignment.Center;
        test.MouseLeftButtonDown += async (_, e) =>
        {
            e.Handled = true;
            test.Text = await ControlClient.Send("test") == "ok" ? "Test alert sent" : "Service not reachable";
        };
        var footer = new DockPanel();
        DockPanel.SetDock(test, Dock.Right);
        footer.Children.Add(test);
        footer.Children.Add(_switch);
        footer.Children.Add(_switchLabel);

        var stack = new StackPanel();
        foreach (UIElement element in (UIElement[])[header, _banner, _live, _health, Rule(), footer])
            stack.Children.Add(element);

        Root = new Border
        {
            Background = opaque ? Theme.Solid : Theme.Tint,
            CornerRadius = new CornerRadius(opaque ? 10 : 0),
            Padding = new Thickness(20, 18, 20, 16),
            Child = stack,
        };
        Content = Root;

        SourceInitialized += (_, _) => ApplyBackdrop();
        Deactivated += (_, _) => { if (HideWhenDeactivated) Hide(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Select(health: false);
    }

    public void Toggle()
    {
        if (IsVisible) { Hide(); return; }
        Refresh();
        Show();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
        Activate();
    }

    public void Select(bool health)
    {
        _live.Visibility = health ? Visibility.Collapsed : Visibility.Visible;
        _health.Visibility = health ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (tab, active) in ((Border, bool)[])[(_liveTab, !health), (_healthTab, health)])
        {
            tab.Background = active ? Theme.Of(Colors.White, 0.12) : Brushes.Transparent;
            ((TextBlock)tab.Child).Foreground = active ? Theme.Text : Theme.Dim;
        }
        Refresh();
    }

    /// <summary>Pulls the current model state into the controls. Cheap enough to call on every sample.</summary>
    public void Refresh()
    {
        var status = _model.Status;
        bool live = _model.HasPins;
        var severity = status?.Severity ?? Severity.Ok;
        _title.Text = status?.Card is { Length: > 0 } card ? card : "PinSentinel";

        (string text, Color color) = !_model.Connected ? ("Waiting for the service", Theme.OfflineColor)
            : !live ? ("Sensor not readable", Theme.WarnColor)
            : status!.Throttled ? ("GPU throttled", Theme.ThrottleColor)
            : severity == Severity.Ok ? ("All pins healthy", Theme.AccentA)
            : (severity == Severity.Warn ? "Warning" : severity == Severity.Throttle ? "Fault: throttle" : "Fault: shutdown", Theme.ColorFor(severity));
        _state.Text = "●  " + text;
        _state.Foreground = Theme.Of(color);

        // The same condition usually trips several levels at once; show only the worst.
        var findings = live ? status!.Findings.Where(f => f.Severity == severity).Select(f => Capitalise(f.Message)).Distinct().ToList() : [];
        _banner.Visibility = findings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _banner.Background = Theme.Of(color, 0.14);
        _bannerText.Text = string.Join("\n", findings);

        Set("Imbalance", live && _model.Imbalance is { } imbalance ? $"{imbalance:P1}" : "-",
            _model.Imbalance >= 0.35 ? Theme.Critical : _model.Imbalance >= 0.20 ? Theme.Warn : null);
        Set("Hottest pin", live ? $"{_model.MaxAmps:F2} A" : "-", live && _model.MaxAmps >= Theme.PinWarnAmps ? Theme.Of(Theme.ColorForPin(_model.MaxAmps)) : null);
        Set("Voltage spread", live ? $"{_model.SpreadMillivolts:F0} mV" : "-");

        var gpu = _model.Gpu;
        Set("GPU temp", gpu is null ? "-" : $"{gpu.TempC}°C");
        Set("GPU load", gpu is null ? "-" : $"{gpu.LoadPercent}%");
        Set("VRAM", gpu is null ? "-" : $"{gpu.VramUsedGb:F1} GB");
        Set("Fan", gpu is null ? "-" : $"{gpu.FanPercent}%");

        Set("Peak pin", _model.PeakPinAmps > 0 ? $"{_model.PeakPinAmps:F2} A" : "-", _model.PeakPinAmps >= Theme.PinWarnAmps ? Theme.Of(Theme.ColorForPin(_model.PeakPinAmps)) : null);
        Set("Peak power", _model.PeakWatts > 0 ? $"{_model.PeakWatts:F0} W" : "-");
        Set("Peak imbalance", _model.PeakImbalance > 0 ? $"{_model.PeakImbalance:P1}" : "-");
        Set("Energy", _model.EnergyWh >= 1000 ? $"{_model.EnergyWh / 1000:F2} kWh" : $"{_model.EnergyWh:F0} Wh");

        RefreshHealth();

        bool armed = _model.Connected && status is { DryRun: false };
        _switch.Background = armed ? Theme.Accent : Theme.Of(Colors.White, 0.16);
        _knob.HorizontalAlignment = armed ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _switchLabel.Text = !_model.Connected ? "Service offline" : armed ? "Armed: throttles and shuts down" : "Dry run: warnings only";

        foreach (var view in _views) view.Update();
    }

    private void RefreshHealth()
    {
        if (UseService && _health.IsVisible && (DateTime.Now - _healthLoaded).TotalSeconds > 60)
        {
            _healthLoaded = DateTime.Now;
            _ = LoadHealth();
        }

        var report = _model.Health;
        (string verdict, Color color) = report?.Verdict switch
        {
            HealthVerdict.Stable => ("Stable", Theme.AccentA),
            HealthVerdict.Drifting => ("Drifting", Theme.WarnColor),
            HealthVerdict.NotEnoughData => ("Building baseline", Colors.White),
            _ => ("Reading logs", Colors.White),
        };
        _verdict.Text = verdict;
        _verdict.Foreground = Theme.Of(color);
        _healthSummary.Text = report?.Summary ?? "";
    }

    private async Task LoadHealth()
    {
        _model.Health = await HealthLoader.Load();
        Refresh();
    }

    private async void ToggleArmed()
    {
        if (!UseService || !_model.Connected || _model.Status is not { } status) return;

        string command = "disarm";
        if (status.DryRun)
        {
            HideWhenDeactivated = false;
            var answer = MessageBox.Show(this,
                "Arm PinSentinel?\n\nWhen armed, a connector fault will cut GPU power and, if the fault persists, force Windows to shut down. Unsaved work in open programs will be lost.",
                "PinSentinel", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            HideWhenDeactivated = true;
            if (answer != MessageBoxResult.OK) return;
            command = "arm";
        }
        if (await ControlClient.Send(command) != "ok") _switchLabel.Text = "Service not reachable";
    }

    // Windows 11 acrylic behind the tint, with system-drawn rounded corners and shadow.
    private void ApplyBackdrop()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        Set(20, 1); // DWMWA_USE_IMMERSIVE_DARK_MODE
        Set(33, 2); // DWMWA_WINDOW_CORNER_PREFERENCE = round
        Set(38, 3); // DWMWA_SYSTEMBACKDROP_TYPE = transient (acrylic)
        void Set(int attribute, int value) => DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void Set(string stat, string value, Brush? brush = null)
    {
        _stats[stat].Text = value;
        _stats[stat].Foreground = brush ?? Theme.Text;
    }

    /// <summary>A borderless label over value pair.</summary>
    private StackPanel Stat(string name, double valueSize = 16)
    {
        var caption = Text(9.5, Theme.Faint, FontWeights.SemiBold);
        caption.Text = name.ToUpperInvariant();
        var value = Text(valueSize, Theme.Text, valueSize > 18 ? FontWeights.Light : FontWeights.Normal);
        _stats[name] = value;
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, valueSize > 18 ? 6 : 0) };
        stack.Children.Add(caption);
        stack.Children.Add(value);
        return stack;
    }

    private UniformGrid Row(params string[] names)
    {
        var grid = new UniformGrid { Columns = names.Length };
        foreach (string name in names) grid.Children.Add(Stat(name));
        return grid;
    }

    private static TextBlock Caption(string text, double topMargin = 10)
    {
        var caption = Text(9.5, Theme.Faint, FontWeights.SemiBold);
        caption.Text = text.ToUpperInvariant();
        caption.Margin = new Thickness(0, topMargin, 0, 4);
        return caption;
    }

    private static Border Rule() => new() { Height = 1, Background = Theme.Hairline, Margin = new Thickness(0, 12, 0, 12) };

    private static Border Tab(string text, Action select)
    {
        var label = Text(11.5, Theme.Dim, FontWeights.SemiBold);
        label.Text = text;
        var tab = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 4, 12, 5), Child = label, Cursor = Cursors.Hand };
        tab.MouseLeftButtonDown += (_, e) => { e.Handled = true; select(); };
        return tab;
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static TextBlock Text(double size, Brush brush, FontWeight? weight = null) =>
        new() { FontSize = size, Foreground = brush, FontWeight = weight ?? FontWeights.Normal };
}
