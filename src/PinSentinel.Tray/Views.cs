using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PinSentinel.Tray;

abstract class ModelView(DashboardModel model) : FrameworkElement
{
    protected DashboardModel Model { get; } = model;

    protected FormattedText Label(string text, double size, Brush brush, FontFamily? font = null, FontWeight? weight = null) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(font ?? Theme.Sans, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}

/// <summary>One row per pin: current bar with the 9.5 A limit and the six-pin mean marked.</summary>
sealed class PinBarsView(DashboardModel model) : ModelView(model)
{
    private const double RowHeight = 26;
    private const double ScaleAmps = 12;
    private static readonly Pen s_limit = FrozenPen(Theme.Shutdown, 1.5);
    private static readonly Pen s_mean = FrozenPen(Theme.Text, 1);

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width, RowHeight * 6);

    protected override void OnRender(DrawingContext dc)
    {
        const double barLeft = 52, textWidth = 132;
        double barWidth = Math.Max(10, ActualWidth - barLeft - textWidth);
        bool live = Model.HasPins;
        double[] amps = live ? Model.Status!.Amps : new double[6];
        double[] volts = live ? Model.Status!.Volts : new double[6];

        for (int i = 0; i < 6; i++)
        {
            double y = i * RowHeight, barY = y + 8, barHeight = 10;
            dc.DrawEllipse(Theme.Pins[i], null, new Point(5, y + 13), 3.5, 3.5);
            dc.DrawText(Label($"PIN {i + 1}", 11, Theme.Dim, weight: FontWeights.SemiBold), new Point(14, y + 6));

            dc.DrawRoundedRectangle(Theme.Track, null, new Rect(barLeft, barY, barWidth, barHeight), 3, 3);
            double fill = barWidth * Math.Clamp(amps[i] / ScaleAmps, 0, 1);
            if (fill > 1)
                dc.DrawRoundedRectangle(live ? Theme.ForPinAmps(amps[i]) : Theme.Offline, null, new Rect(barLeft, barY, fill, barHeight), 3, 3);

            double limitX = barLeft + barWidth * Theme.PinLimitAmps / ScaleAmps;
            dc.DrawLine(s_limit, new Point(limitX, barY - 3), new Point(limitX, barY + barHeight + 3));
            if (live && Model.Imbalance is not null)
            {
                double meanX = barLeft + barWidth * Math.Clamp(Model.MeanAmps / ScaleAmps, 0, 1);
                dc.DrawLine(s_mean, new Point(meanX, barY - 2), new Point(meanX, barY + barHeight + 2));
            }

            string value = live ? $"{amps[i],5:F2} A" : "  -.-- A";
            dc.DrawText(Label(value, 13, Theme.Text, Theme.Mono), new Point(barLeft + barWidth + 8, y + 4));

            string detail = !live ? "" : Model.PathMilliohms[i] is { } r ? $"{volts[i]:F2}V {r,2:F0}mΩ" : $"{volts[i]:F2}V";
            var detailText = Label(detail, 10.5, Theme.Dim, Theme.Mono);
            dc.DrawText(detailText, new Point(ActualWidth - detailText.Width, y + 6.5));
        }
    }

    internal static Pen FrozenPen(Brush brush, double thickness, DashStyle? dash = null)
    {
        var pen = new Pen(brush, thickness) { DashStyle = dash ?? DashStyles.Solid };
        pen.Freeze();
        return pen;
    }
}

/// <summary>Per-pin current over the last five minutes.</summary>
sealed class HistoryGraph(DashboardModel model) : ModelView(model)
{
    private static readonly Pen s_grid = PinBarsView.FrozenPen(Theme.Grid, 1);
    private static readonly Pen s_limit = PinBarsView.FrozenPen(Theme.Shutdown, 1, new DashStyle([4, 3], 0));
    private static readonly Pen[] s_pins = [.. Theme.Pins.Select(b => PinBarsView.FrozenPen(b, 1.2))];

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width, 112);

    protected override void OnRender(DrawingContext dc)
    {
        const double labelWidth = 30, top = 6, bottom = 4;
        double width = ActualWidth - labelWidth, height = ActualHeight - top - bottom;
        double[][] history = [.. Model.History];
        double scale = Math.Max(12, Math.Ceiling(history.Length == 0 ? 0 : history.Max(s => s.Max()) + 1));
        double Y(double amps) => top + height * (1 - amps / scale);

        dc.DrawRoundedRectangle(Theme.Panel, null, new Rect(0, 0, width, ActualHeight), 6, 6);
        foreach (double amps in (double[])[0, 4, 8])
        {
            dc.DrawLine(s_grid, new Point(0, Y(amps)), new Point(width, Y(amps)));
            dc.DrawText(Label($"{amps:F0}", 10, Theme.Dim, Theme.Mono), new Point(width + 6, Y(amps) - 7));
        }
        dc.DrawLine(s_limit, new Point(0, Y(Theme.PinLimitAmps)), new Point(width, Y(Theme.PinLimitAmps)));
        dc.DrawText(Label("9.5", 10, Theme.Shutdown, Theme.Mono), new Point(width + 6, Y(Theme.PinLimitAmps) - 7));

        if (history.Length < 2) return;
        double step = width / (DashboardModel.HistoryCapacity - 1);
        double start = width - step * (history.Length - 1);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, ActualHeight)));
        for (int pin = 0; pin < 6; pin++)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(start, Y(history[0][pin])), false, false);
                for (int i = 1; i < history.Length; i++)
                    g.LineTo(new Point(start + step * i, Y(history[i][pin])), true, true);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, s_pins[pin], geometry);
        }
        dc.Pop();
    }
}
