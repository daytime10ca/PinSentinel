using System.Windows.Media;
using PinSentinel.Core;

namespace PinSentinel.Tray;

static class Theme
{
    public static readonly Brush Window = Frozen("#14161B");
    public static readonly Brush Panel = Frozen("#1C1F26");
    public static readonly Brush Border = Frozen("#2A2E37");
    public static readonly Brush Track = Frozen("#262A33");
    public static readonly Brush Text = Frozen("#E8EAED");
    public static readonly Brush Dim = Frozen("#8B93A1");
    public static readonly Brush Grid = Frozen("#2A2E37");

    public static readonly Brush Ok = Frozen("#3DDC84");
    public static readonly Brush Warn = Frozen("#FFB020");
    public static readonly Brush Throttle = Frozen("#FF7A1A");
    public static readonly Brush Shutdown = Frozen("#FF4D4D");
    public static readonly Brush Offline = Frozen("#6B7280");

    public static readonly Brush[] Pins =
    [
        Frozen("#4FC3F7"), Frozen("#81C784"), Frozen("#FFD54F"),
        Frozen("#FF8A65"), Frozen("#BA68C8"), Frozen("#F06292"),
    ];

    public static readonly FontFamily Sans = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public const double PinLimitAmps = 9.5;
    public const double PinWarnAmps = 9.0;

    public static Brush For(Severity severity) => severity switch
    {
        Severity.Ok => Ok,
        Severity.Warn => Warn,
        Severity.Throttle => Throttle,
        _ => Shutdown,
    };

    public static Brush ForPinAmps(double amps) =>
        amps >= PinLimitAmps ? Shutdown : amps >= PinWarnAmps ? Warn : Ok;

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
