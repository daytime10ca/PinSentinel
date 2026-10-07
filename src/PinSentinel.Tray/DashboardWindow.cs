using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PinSentinel.Core;

namespace PinSentinel.Tray;

/// <summary>The flyout shown from the tray icon.</summary>
sealed class DashboardWindow : Window
{
    private readonly DashboardModel _model;
    private readonly PinBarsView _bars;
    private readonly HistoryGraph _graph;
    private readonly TextBlock _title = Text(15, Theme.Text, FontWeights.SemiBold);
    private readonly TextBlock _pillText = Text(11, Theme.Window, FontWeights.Bold);
    private readonly Border _pill;
    private readonly TextBlock _findings = Text(11.5, Theme.Warn);
    private readonly TextBlock _footer = Text(10.5, Theme.Dim);
    private readonly Dictionary<string, TextBlock> _tiles = [];

    public Border Root { get; }
    public bool HideWhenDeactivated { get; set; } = true;

    public DashboardWindow(DashboardModel model)
    {
        _model = model;
        _bars = new PinBarsView(model);
        _graph = new HistoryGraph(model);

        Title = "PinSentinel";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        FontFamily = Theme.Sans;
        UseLayoutRounding = true;

        _pill = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 2, 9, 3), Child = _pillText, VerticalAlignment = VerticalAlignment.Center };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(_pill, Dock.Right);
        header.Children.Add(_pill);
        var titles = new StackPanel();
        titles.Children.Add(_title);
        var subtitle = Text(11, Theme.Dim);
        subtitle.Text = "12V-2x6 connector, per-pin current";
        titles.Children.Add(subtitle);
        header.Children.Add(titles);

        _findings.TextWrapping = TextWrapping.Wrap;
        _findings.Margin = new Thickness(0, 8, 0, 0);

        var tiles = new UniformGrid { Columns = 4, Margin = new Thickness(-3, 12, -3, 6) };
        foreach (string name in (string[])["Connector", "Total", "Imbalance", "V spread", "GPU temp", "GPU load", "VRAM", "Fan",
                                           "Peak pin", "Peak power", "Peak imbal.", "Energy"])
            tiles.Children.Add(Tile(name));

        var graphCaption = Text(10.5, Theme.Dim);
        graphCaption.Text = "PER-PIN CURRENT (A), LAST 5 MIN";
        graphCaption.Margin = new Thickness(0, 6, 0, 4);
        _footer.Margin = new Thickness(0, 8, 0, 0);
        _footer.TextWrapping = TextWrapping.Wrap;

        var stack = new StackPanel();
        foreach (UIElement element in (UIElement[])[header, _bars, _findings, tiles, graphCaption, _graph, _footer])
            stack.Children.Add(element);

        Root = new Border
        {
            Background = Theme.Window,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Child = stack,
        };
        Content = Root;

        Deactivated += (_, _) => { if (HideWhenDeactivated) Hide(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Refresh();
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

    /// <summary>Pulls the current model state into the controls. Cheap enough to call on every sample.</summary>
    public void Refresh()
    {
        var status = _model.Status;
        _title.Text = status?.Card is { Length: > 0 } card ? card : "PinSentinel";

        (string text, Brush brush) = !_model.Connected ? ("NO SERVICE", Theme.Offline)
            : !_model.HasPins ? ("NO SENSOR", Theme.Warn)
            : status!.Throttled ? ("THROTTLED", Theme.Throttle)
            : (status.Severity.ToString().ToUpperInvariant(), Theme.For(status.Severity));
        _pillText.Text = text;
        _pill.Background = brush;

        var findings = status?.Findings ?? [];
        // The same condition usually trips several levels at once; show only the worst.
        _findings.Text = string.Join("\n", findings.Where(f => f.Severity == status!.Severity).Select(f => f.Message).Distinct());
        _findings.Foreground = Theme.For(status?.Severity ?? Severity.Ok);
        _findings.Visibility = _model.Connected && findings.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool live = _model.HasPins;
        Set("Connector", live ? $"{_model.Watts:F0} W" : "-");
        Set("Total", live ? $"{_model.TotalAmps:F1} A" : "-");
        Set("Imbalance", live && _model.Imbalance is { } imbalance ? $"{imbalance:P1}" : "-",
            _model.Imbalance >= 0.35 ? Theme.Throttle : _model.Imbalance >= 0.20 ? Theme.Warn : null);
        Set("V spread", live ? $"{_model.SpreadMillivolts:F0} mV" : "-");

        var gpu = _model.Gpu;
        Set("GPU temp", gpu is null ? "-" : $"{gpu.TempC} °C");
        Set("GPU load", gpu is null ? "-" : $"{gpu.LoadPercent} %");
        Set("VRAM", gpu is null ? "-" : $"{gpu.VramUsedGb:F1} GB");
        Set("Fan", gpu is null ? "-" : $"{gpu.FanPercent} %");

        Set("Peak pin", _model.PeakPinAmps > 0 ? $"{_model.PeakPinAmps:F2} A" : "-", _model.PeakPinAmps > 0 ? Theme.ForPinAmps(_model.PeakPinAmps) : null);
        Set("Peak power", _model.PeakWatts > 0 ? $"{_model.PeakWatts:F0} W" : "-");
        Set("Peak imbal.", _model.PeakImbalance > 0 ? $"{_model.PeakImbalance:P1}" : "-");
        Set("Energy", _model.EnergyWh >= 1000 ? $"{_model.EnergyWh / 1000:F2} kWh" : $"{_model.EnergyWh:F0} Wh");

        _footer.Text = !_model.Connected ? "Waiting for the PinSentinel service..."
            : status!.DryRun ? "Dry run: the guard warns but will not throttle or shut down. Peaks are since this app started."
            : "Armed: the guard will throttle and shut down on a fault. Peaks are since this app started.";

        _bars.InvalidateVisual();
        _graph.InvalidateVisual();
    }

    private void Set(string tile, string value, Brush? brush = null)
    {
        _tiles[tile].Text = value;
        _tiles[tile].Foreground = brush ?? Theme.Text;
    }

    private Border Tile(string name)
    {
        var caption = Text(9.5, Theme.Dim);
        caption.Text = name.ToUpperInvariant();
        var value = Text(14, Theme.Text, FontWeights.SemiBold);
        value.FontFamily = Theme.Mono;
        _tiles[name] = value;

        var stack = new StackPanel();
        stack.Children.Add(caption);
        stack.Children.Add(value);
        return new Border
        {
            Background = Theme.Panel,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 6, 6),
            Margin = new Thickness(3),
            Child = stack,
        };
    }

    private static TextBlock Text(double size, Brush brush, FontWeight? weight = null) =>
        new() { FontSize = size, Foreground = brush, FontWeight = weight ?? FontWeights.Normal };
}
