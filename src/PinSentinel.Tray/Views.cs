using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PinSentinel.Core;

namespace PinSentinel.Tray;

/// <summary>Custom-drawn element whose numbers ease toward new values instead of jumping.</summary>
abstract class AnimatedView : FrameworkElement
{
    private readonly DispatcherTimer _timer;
    private double[] _target = [];

    protected DashboardModel Model { get; }
    protected double[] Shown { get; private set; } = [];

    protected AnimatedView(DashboardModel model)
    {
        Model = model;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Step();
        IsVisibleChanged += (_, _) => { if (!IsVisible) _timer.Stop(); };
    }

    public abstract void Update();

    protected void SetTargets(double[] values)
    {
        _target = values;
        if (Shown.Length != values.Length || !IsVisible)
        {
            Shown = (double[])values.Clone();
            InvalidateVisual();
        }
        else _timer.Start();
    }

    private void Step()
    {
        bool moving = false;
        for (int i = 0; i < Shown.Length; i++)
        {
            double delta = _target[i] - Shown[i];
            if (Math.Abs(delta) < 0.004) Shown[i] = _target[i];
            else { Shown[i] += delta * 0.22; moving = true; }
        }
        InvalidateVisual();
        if (!moving) _timer.Stop();
    }

    protected FormattedText Label(string text, double size, Brush brush, FontFamily? font = null, FontWeight? weight = null) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(font ?? Theme.Sans, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected static void Centered(DrawingContext dc, FormattedText text, double centerX, double y) =>
        dc.DrawText(text, new Point(centerX - text.Width / 2, y));
}

/// <summary>Arc gauge of connector power.</summary>
sealed class PowerGauge(DashboardModel model) : AnimatedView(model)
{
    private const double FullScaleWatts = 600, Radius = 62, Thickness = 9;
    private static readonly Pen s_track = Theme.Pen(Theme.Track, Thickness, round: true);

    protected override Size MeasureOverride(Size availableSize) => new(148, 132);

    public override void Update() => SetTargets([Model.HasPins ? Model.Watts : 0]);

    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(74, 74);
        double watts = Shown.Length > 0 ? Shown[0] : 0;
        var color = !Model.HasPins ? Theme.OfflineColor : Theme.ColorFor(Model.Status!.Severity);

        dc.DrawGeometry(null, s_track, Arc(center, 1));
        double fraction = Math.Clamp(watts / FullScaleWatts, 0, 1);
        if (fraction > 0.005)
            dc.DrawGeometry(null, Theme.Pen(Theme.Gradient(color, new Point(0, 1), new Point(1, 0)), Thickness, round: true), Arc(center, fraction));

        Centered(dc, Label(Model.HasPins ? $"{watts:F0}" : "--", 36, Theme.Text, weight: FontWeights.Light), center.X, center.Y - 30);
        Centered(dc, Label("WATTS", 9.5, Theme.Dim, weight: FontWeights.SemiBold), center.X, center.Y + 16);
        Centered(dc, Label(Model.HasPins ? $"{Model.TotalAmps:F1} A" : "", 11.5, Theme.Dim, Theme.Mono), center.X, center.Y + 40);
    }

    // 240 degree sweep, open at the bottom.
    private static Geometry Arc(Point center, double fraction)
    {
        Point At(double degrees) => new(center.X + Radius * Math.Cos(degrees * Math.PI / 180), center.Y + Radius * Math.Sin(degrees * Math.PI / 180));
        double sweep = 240 * fraction;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(At(150), false, false);
            g.ArcTo(At(150 + sweep), new Size(Radius, Radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>Six capsules, one per pin, with the 9.5 A limit drawn across them.</summary>
sealed class PinColumns(DashboardModel model) : AnimatedView(model)
{
    private const double ScaleAmps = 12, Top = 8, BarHeight = 104, BarWidth = 22;
    private static readonly Pen s_limit = Theme.Pen(Theme.Of(Theme.CriticalColor, 0.75), 1, dash: [3, 3]);
    private static readonly Pen s_mean = Theme.Pen(Theme.Of(Colors.White, 0.85), 1.5, round: true);

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width, Top + BarHeight + 44);

    public override void Update() => SetTargets(Model.HasPins ? Model.Status!.Amps : new double[6]);

    protected override void OnRender(DrawingContext dc)
    {
        if (Shown.Length != 6) return;
        bool live = Model.HasPins;
        double pitch = (ActualWidth - 30) / 6;
        double Y(double amps) => Top + BarHeight * (1 - Math.Clamp(amps / ScaleAmps, 0, 1));

        double limitY = Y(Theme.PinLimitAmps);
        dc.DrawLine(s_limit, new Point(0, limitY), new Point(ActualWidth - 30, limitY));
        dc.DrawText(Label("9.5", 9.5, Theme.Of(Theme.CriticalColor, 0.9), Theme.Mono), new Point(ActualWidth - 24, limitY - 7));

        for (int i = 0; i < 6; i++)
        {
            double centerX = pitch * (i + 0.5), left = centerX - BarWidth / 2, amps = Shown[i];
            var track = new Rect(left, Top, BarWidth, BarHeight);
            var color = live ? Theme.ColorForPin(amps) : Theme.OfflineColor;

            if (live && amps >= Theme.PinWarnAmps)
                dc.DrawRoundedRectangle(Theme.Of(color, 0.16), null, Rect.Inflate(track, 5, 5), 16, 16);
            dc.DrawRoundedRectangle(Theme.Track, null, track, 11, 11);

            dc.PushClip(new RectangleGeometry(track, 11, 11));
            dc.DrawRectangle(Theme.Gradient(color, new Point(0, 1), new Point(0, 0)), null, new Rect(left, Y(amps), BarWidth, Top + BarHeight - Y(amps)));
            dc.Pop();

            if (live && Model.Imbalance is not null)
                dc.DrawLine(s_mean, new Point(left - 3, Y(Model.MeanAmps)), new Point(left + BarWidth + 3, Y(Model.MeanAmps)));

            string value = live ? Model.Status!.Amps[i].ToString("F2", CultureInfo.InvariantCulture) : "-.--";
            Centered(dc, Label(value, 13, live ? Theme.Text : Theme.Dim, Theme.Mono), centerX, Top + BarHeight + 8);
            Centered(dc, Label($"PIN {i + 1}", 9, Theme.Faint, weight: FontWeights.SemiBold), centerX, Top + BarHeight + 27);
        }
    }
}

/// <summary>Per-pin current over the last five minutes. The pin furthest from the mean is picked out.</summary>
sealed class HistoryGraph(DashboardModel model) : AnimatedView(model)
{
    private static readonly Pen s_grid = Theme.Pen(Theme.Hairline, 1);
    private static readonly Pen s_limit = Theme.Pen(Theme.Of(Theme.CriticalColor, 0.75), 1, dash: [3, 3]);
    private static readonly Pen s_line = Theme.Pen(Theme.Of(Theme.AccentB, 0.45), 1);

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width, 84);

    public override void Update() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        const double labelWidth = 30, top = 6, bottom = 4;
        double width = ActualWidth - labelWidth, height = ActualHeight - top - bottom;
        double[][] history = [.. Model.History];
        double scale = Math.Max(12, Math.Ceiling(history.Length == 0 ? 0 : history.Max(s => s.Max()) + 1));
        double Y(double amps) => top + height * (1 - amps / scale);

        foreach (double amps in (double[])[0, 4, 8])
        {
            dc.DrawLine(s_grid, new Point(0, Y(amps)), new Point(width, Y(amps)));
            dc.DrawText(Label($"{amps:F0}", 9.5, Theme.Faint, Theme.Mono), new Point(width + 6, Y(amps) - 7));
        }
        dc.DrawLine(s_limit, new Point(0, Y(Theme.PinLimitAmps)), new Point(width, Y(Theme.PinLimitAmps)));
        if (history.Length < 2) return;

        double[] latest = history[^1];
        double mean = latest.Average();
        int outlier = Enumerable.Range(0, 6).MaxBy(i => Math.Abs(latest[i] - mean));
        bool highlight = latest.Sum() >= DashboardModel.LoadGateAmps && Math.Abs(latest[outlier] - mean) / mean >= 0.10;

        double step = width / (DashboardModel.HistoryCapacity - 1);
        double start = width - step * (history.Length - 1);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, ActualHeight)));
        for (int pass = 0; pass < 2; pass++)
        {
            for (int pin = 0; pin < 6; pin++)
            {
                bool hot = highlight && pin == outlier;
                if (hot != (pass == 1)) continue;

                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(new Point(start, Y(history[0][pin])), false, false);
                    for (int i = 1; i < history.Length; i++)
                        g.LineTo(new Point(start + step * i, Y(history[i][pin])), true, true);
                }
                geometry.Freeze();
                dc.DrawGeometry(null, hot ? Theme.Pen(Theme.Of(Theme.ColorForPin(Math.Max(latest[pin], Theme.PinWarnAmps))), 1.8) : s_line, geometry);
            }
        }
        dc.Pop();
    }
}

/// <summary>Long-term view: each pin's share of the load, its drift since baseline and its path resistance.</summary>
sealed class HealthView(DashboardModel model) : AnimatedView(model)
{
    private const double RowHeight = 28, HeaderHeight = 18;
    private const double BarLeft = 46, BarWidth = 92, BarRangePoints = 2.0;
    private const double Even = 100.0 / 6;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width, HeaderHeight + RowHeight * 6);

    public override void Update() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double shareX = BarLeft + BarWidth + 50, driftX = shareX + 46, ohmX = driftX + 56, sparkLeft = ohmX + 12;
        double sparkWidth = Math.Max(20, ActualWidth - sparkLeft);
        Right(dc, Label("SHARE", 9, Theme.Faint, weight: FontWeights.SemiBold), shareX, 0);
        Right(dc, Label("DRIFT", 9, Theme.Faint, weight: FontWeights.SemiBold), driftX, 0);
        Right(dc, Label("PATH", 9, Theme.Faint, weight: FontWeights.SemiBold), ohmX, 0);
        dc.DrawText(Label("TREND", 9, Theme.Faint, weight: FontWeights.SemiBold), new Point(sparkLeft, 0));

        var report = Model.Health;
        var latest = report?.Latest;
        // One vertical scale for all six sparklines, so a flat pin looks flat next to a moving one.
        double sparkRange = report is { Days.Count: >= 2 }
            ? Math.Max(0.004, Enumerable.Range(0, 6).Max(p => report.Days.Max(d => d.Share[p]) - report.Days.Min(d => d.Share[p])))
            : 1;
        var hairline = Theme.Pen(Theme.Hairline, 1);

        for (int i = 0; i < 6; i++)
        {
            double y = HeaderHeight + i * RowHeight, mid = y + RowHeight / 2;
            dc.DrawText(Label($"PIN {i + 1}", 10, Theme.Dim, weight: FontWeights.SemiBold), new Point(0, mid - 7));

            // Diverging bar: centre is an even one-sixth share.
            double centre = BarLeft + BarWidth / 2;
            dc.DrawRoundedRectangle(Theme.Track, null, new Rect(BarLeft, mid - 3, BarWidth, 6), 3, 3);
            dc.DrawLine(hairline, new Point(centre, mid - 7), new Point(centre, mid + 7));
            if (latest is null)
            {
                Right(dc, Label("-", 12, Theme.Faint, Theme.Mono), shareX, mid - 8);
                continue;
            }

            double share = latest.Share[i] * 100;
            double? drift = report!.ShareDrift?[i];
            var color = Math.Abs(drift ?? 0) >= HealthAnalyzer.ShareDriftPoints ? Theme.WarnColor : Theme.AccentA;
            double extent = Math.Clamp((share - Even) / BarRangePoints, -1, 1) * BarWidth / 2;
            dc.DrawRoundedRectangle(Theme.Of(color), null, new Rect(Math.Min(centre, centre + extent), mid - 3, Math.Max(2, Math.Abs(extent)), 6), 3, 3);

            Right(dc, Label($"{share:F1}%", 12, Theme.Text, Theme.Mono), shareX, mid - 8);
            Right(dc, Label(drift is { } d ? Math.Round(d, 1).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) : "-", 11.5, drift is null ? Theme.Faint : Theme.Of(color), Theme.Mono), driftX, mid - 8);
            Right(dc, Label(latest.Milliohms[i] is { } r ? $"{r:F0} mΩ" : "-", 11.5, Theme.Dim, Theme.Mono), ohmX, mid - 8);

            var days = report.Days;
            if (days.Count >= 2)
            {
                double middle = (days.Min(day => day.Share[i]) + days.Max(day => day.Share[i])) / 2;
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    for (int k = 0; k < days.Count; k++)
                    {
                        var point = new Point(sparkLeft + sparkWidth * k / (days.Count - 1),
                            mid - 16 * (days[k].Share[i] - middle) / sparkRange);
                        if (k == 0) g.BeginFigure(point, false, false); else g.LineTo(point, true, true);
                    }
                }
                geometry.Freeze();
                dc.DrawGeometry(null, Theme.Pen(Theme.Of(color, 0.9), 1.3, round: true), geometry);
            }
        }
    }

    private static void Right(DrawingContext dc, FormattedText text, double rightX, double y) =>
        dc.DrawText(text, new Point(rightX - text.Width, y));
}
