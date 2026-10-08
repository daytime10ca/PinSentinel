using System.Windows;
using System.Windows.Media;
using PinSentinel.Core;

namespace PinSentinel.Tray;

static class Theme
{
    public static readonly Color Base = Rgb("#0E1014");
    public static readonly Brush Solid = Frozen(new SolidColorBrush(Base));
    /// <summary>Tint laid over the system acrylic backdrop.</summary>
    public static readonly Brush Tint = Frozen(new SolidColorBrush(Color.FromArgb(0xB4, Base.R, Base.G, Base.B)));

    public static readonly Brush Text = Brush("#F2F4F7");
    public static readonly Brush Dim = Brush("#F2F4F7", 0.56);
    public static readonly Brush Faint = Brush("#F2F4F7", 0.42);
    public static readonly Brush Hairline = Brush("#FFFFFF", 0.09);
    public static readonly Brush Track = Brush("#FFFFFF", 0.07);
    public static readonly Brush Surface = Brush("#FFFFFF", 0.06);

    public static readonly Color AccentA = Rgb("#5EEAD4");
    public static readonly Color AccentB = Rgb("#7DD3FC");
    public static readonly Color WarnColor = Rgb("#FBBF24");
    public static readonly Color ThrottleColor = Rgb("#FB923C");
    public static readonly Color CriticalColor = Rgb("#F87171");
    public static readonly Color OfflineColor = Rgb("#6B7280");

    public static readonly Brush Accent = Frozen(new SolidColorBrush(AccentA));
    public static readonly Brush Warn = Frozen(new SolidColorBrush(WarnColor));
    public static readonly Brush Critical = Frozen(new SolidColorBrush(CriticalColor));

    public static readonly FontFamily Sans = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public const double PinLimitAmps = 9.5;
    public const double PinWarnAmps = 9.0;

    public static Color ColorFor(Severity severity) => severity switch
    {
        Severity.Ok => AccentA,
        Severity.Warn => WarnColor,
        Severity.Throttle => ThrottleColor,
        _ => CriticalColor,
    };

    public static Color ColorForPin(double amps) =>
        amps >= PinLimitAmps ? CriticalColor : amps >= PinWarnAmps ? WarnColor : AccentA;

    public static Brush Of(Color color, double opacity = 1) =>
        Frozen(new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), color.R, color.G, color.B)));

    /// <summary>Two-stop gradient; healthy elements run teal to sky, others lighten their own colour.</summary>
    public static Brush Gradient(Color color, Point start, Point end)
    {
        Color to = color == AccentA ? AccentB : Color.FromRgb(Lift(color.R), Lift(color.G), Lift(color.B));
        return Frozen(new LinearGradientBrush(color, to, start, end));
        static byte Lift(byte c) => (byte)(c + (255 - c) * 0.35);
    }

    public static Pen Pen(Brush brush, double thickness, bool round = false, double[]? dash = null)
    {
        var pen = new Pen(brush, thickness);
        if (round) pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;
        if (dash is not null) pen.DashStyle = new DashStyle(dash, 0);
        pen.Freeze();
        return pen;
    }

    private static Color Rgb(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Brush Brush(string hex, double opacity = 1) => Of(Rgb(hex), opacity);
    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
}
