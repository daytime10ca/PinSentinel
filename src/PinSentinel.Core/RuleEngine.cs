namespace PinSentinel.Core;

public enum Severity { Ok, Warn, Throttle, Shutdown }

public sealed record Finding(string RuleId, Severity Severity, string Message);

public sealed record Evaluation(Severity Severity, IReadOnlyList<Finding> Findings)
{
    public static readonly Evaluation Ok = new(Severity.Ok, []);
}

/// <summary>A threshold that must be exceeded continuously for Hold before it counts.</summary>
public sealed record Limit(double Threshold, TimeSpan Hold);

public sealed class RuleOptions
{
    /// <summary>Balance, open-pin and voltage rules only apply above this total current.</summary>
    public double LoadGateAmps { get; set; } = 15.0;

    public Limit PinAmpsWarn { get; set; } = new(9.0, TimeSpan.FromSeconds(10));
    public Limit PinAmpsThrottle { get; set; } = new(9.5, TimeSpan.FromSeconds(10));
    public Limit PinAmpsShutdown { get; set; } = new(13.0, TimeSpan.FromSeconds(3));
    public Limit PinAmpsShutdownFast { get; set; } = new(16.0, TimeSpan.FromSeconds(1));

    /// <summary>Largest deviation of any pin from the six-pin mean, as a fraction of the mean.</summary>
    public Limit ImbalanceWarn { get; set; } = new(0.20, TimeSpan.FromSeconds(30));
    public Limit ImbalanceThrottle { get; set; } = new(0.35, TimeSpan.FromSeconds(15));
    public Limit ImbalanceShutdown { get; set; } = new(0.50, TimeSpan.FromSeconds(30));

    public double OpenPinAmps { get; set; } = 0.5;
    public TimeSpan OpenPinWarn { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan OpenPinThrottle { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan OpenPinShutdown { get; set; } = TimeSpan.FromSeconds(45);

    public Limit VoltSpreadWarn { get; set; } = new(0.150, TimeSpan.FromSeconds(30));
    public Limit VoltSpreadThrottle { get; set; } = new(0.250, TimeSpan.FromSeconds(15));

    public int SensorLossWarnReads { get; set; } = 5;
}

/// <summary>
/// Turns a stream of frames into a severity. Holds no hardware dependencies and
/// takes time from the frames, so it can be driven by recorded or synthetic traces.
/// </summary>
public sealed class RuleEngine(RuleOptions options)
{
    private readonly Dictionary<string, DateTimeOffset> _since = [];
    private int _failedReads;

    public Evaluation Evaluate(PinFrame frame)
    {
        _failedReads = 0;
        var findings = new List<Finding>();
        var now = frame.Timestamp;

        int hotPin = IndexOfMax(frame);
        double hotAmps = frame.Pins[hotPin].Amps;
        CheckAmps("pin-amps-warn", Severity.Warn, options.PinAmpsWarn);
        CheckAmps("pin-amps-throttle", Severity.Throttle, options.PinAmpsThrottle);
        CheckAmps("pin-amps-shutdown", Severity.Shutdown, options.PinAmpsShutdown);
        CheckAmps("pin-amps-shutdown-fast", Severity.Shutdown, options.PinAmpsShutdownFast);

        bool loaded = frame.TotalAmps >= options.LoadGateAmps;
        double mean = frame.MeanAmps;

        int worstPin = 0;
        double deviation = 0;
        if (loaded)
        {
            for (int i = 0; i < frame.Pins.Count; i++)
            {
                double d = Math.Abs(frame.Pins[i].Amps - mean) / mean;
                if (d > deviation) (deviation, worstPin) = (d, i);
            }
        }
        string imbalance = $"pin {worstPin + 1} at {frame.Pins[worstPin].Amps:F1} A is {deviation:P0} off the {mean:F1} A mean";
        Check("imbalance-warn", Severity.Warn, deviation > options.ImbalanceWarn.Threshold, options.ImbalanceWarn.Hold, imbalance);
        Check("imbalance-throttle", Severity.Throttle, deviation > options.ImbalanceThrottle.Threshold, options.ImbalanceThrottle.Hold, imbalance);
        Check("imbalance-shutdown", Severity.Shutdown, deviation > options.ImbalanceShutdown.Threshold, options.ImbalanceShutdown.Hold, imbalance);

        int coldPin = IndexOfMin(frame);
        bool open = loaded && frame.Pins[coldPin].Amps < options.OpenPinAmps;
        string openText = $"pin {coldPin + 1} carries {frame.Pins[coldPin].Amps:F2} A while the connector carries {frame.TotalAmps:F1} A";
        Check("open-pin-warn", Severity.Warn, open, options.OpenPinWarn, openText);
        Check("open-pin-throttle", Severity.Throttle, open, options.OpenPinThrottle, openText);
        Check("open-pin-shutdown", Severity.Shutdown, open, options.OpenPinShutdown, openText);

        double spread = loaded ? frame.VoltSpread : 0;
        string spreadText = $"pin voltages differ by {spread * 1000:F0} mV under load";
        Check("volt-spread-warn", Severity.Warn, spread > options.VoltSpreadWarn.Threshold, options.VoltSpreadWarn.Hold, spreadText);
        Check("volt-spread-throttle", Severity.Throttle, spread > options.VoltSpreadThrottle.Threshold, options.VoltSpreadThrottle.Hold, spreadText);

        return Summarize(findings);

        void CheckAmps(string id, Severity severity, Limit limit) =>
            Check(id, severity, hotAmps >= limit.Threshold, limit.Hold,
                $"pin {hotPin + 1} at {hotAmps:F1} A (limit {limit.Threshold:F1} A)");

        void Check(string id, Severity severity, bool condition, TimeSpan hold, string message)
        {
            if (!condition)
            {
                _since.Remove(id);
                return;
            }
            if (!_since.TryGetValue(id, out var start)) _since[id] = start = now;
            if (now - start >= hold) findings.Add(new Finding(id, severity, message));
        }
    }

    /// <summary>Call instead of Evaluate when a read failed.</summary>
    public Evaluation EvaluateReadFailure()
    {
        // Elapsed time is unknown without samples, so in-progress holds restart.
        _since.Clear();
        _failedReads++;
        if (_failedReads < options.SensorLossWarnReads) return Evaluation.Ok;
        return Summarize([new Finding("sensor-loss", Severity.Warn, $"sensor unreadable for {_failedReads} consecutive reads")]);
    }

    private static Evaluation Summarize(List<Finding> findings) =>
        findings.Count == 0 ? Evaluation.Ok : new Evaluation(findings.Max(f => f.Severity), findings);

    private static int IndexOfMax(PinFrame f)
    {
        int best = 0;
        for (int i = 1; i < f.Pins.Count; i++) if (f.Pins[i].Amps > f.Pins[best].Amps) best = i;
        return best;
    }

    private static int IndexOfMin(PinFrame f)
    {
        int best = 0;
        for (int i = 1; i < f.Pins.Count; i++) if (f.Pins[i].Amps < f.Pins[best].Amps) best = i;
        return best;
    }
}
