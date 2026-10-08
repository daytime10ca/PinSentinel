using PinSentinel.Core;

namespace PinSentinel.Tray;

/// <summary>Everything the UI shows: the latest sample, a short history and session statistics.</summary>
sealed class DashboardModel
{
    public const int HistoryCapacity = 600; // 5 minutes at 2 Hz
    public const double LoadGateAmps = 15.0;

    public StatusMessage? Status { get; private set; }
    public bool Connected { get; private set; }
    public bool HasPins => Connected && Status is { SensorOk: true, Amps.Length: PinFrame.PinCount };

    public Queue<double[]> History { get; } = new();
    public GpuStats? Gpu { get; private set; }
    public HealthReport? Health { get; set; }

    public double TotalAmps { get; private set; }
    public double MeanAmps => TotalAmps / PinFrame.PinCount;
    public double MaxAmps { get; private set; }
    public double Watts { get; private set; }
    public double SpreadMillivolts { get; private set; }
    /// <summary>Largest pin deviation from the mean; null below the load gate where it means nothing.</summary>
    public double? Imbalance { get; private set; }

    public double PeakPinAmps { get; private set; }
    public double PeakWatts { get; private set; }
    public double PeakImbalance { get; private set; }
    public double EnergyWh { get; private set; }

    /// <summary>
    /// Rough resistance of each pin's supply path in milliohms, from how far its voltage
    /// falls between idle and load. Includes PSU droop, so compare pins rather than trust the absolute value.
    /// </summary>
    public double?[] PathMilliohms { get; } = new double?[PinFrame.PinCount];

    private readonly (double Volts, double Amps)?[] _idle = new (double, double)?[PinFrame.PinCount];
    private DateTimeOffset? _lastTime;
    private int _samples;

    public void Disconnected() => Connected = false;

    public void Apply(StatusMessage status)
    {
        Status = status;
        Connected = true;
        if (_samples++ % 2 == 0) Gpu = Nvml.Read();
        if (!HasPins) return;

        double[] amps = status.Amps, volts = status.Volts;
        TotalAmps = amps.Sum();
        MaxAmps = amps.Max();
        Watts = amps.Zip(volts, (a, v) => a * v).Sum();
        SpreadMillivolts = (volts.Max() - volts.Min()) * 1000;

        bool loaded = TotalAmps >= LoadGateAmps;
        Imbalance = loaded ? amps.Max(a => Math.Abs(a - MeanAmps)) / MeanAmps : null;

        PeakPinAmps = Math.Max(PeakPinAmps, MaxAmps);
        PeakWatts = Math.Max(PeakWatts, Watts);
        if (Imbalance is { } imbalance) PeakImbalance = Math.Max(PeakImbalance, imbalance);

        if (_lastTime is { } last)
            EnergyWh += Watts * Math.Clamp((status.Time - last).TotalSeconds, 0, 5) / 3600;
        _lastTime = status.Time;

        UpdateResistance(amps, volts);

        History.Enqueue(amps);
        while (History.Count > HistoryCapacity) History.Dequeue();
    }

    private void UpdateResistance(double[] amps, double[] volts)
    {
        const double Smoothing = 0.05;
        for (int i = 0; i < amps.Length; i++)
        {
            if (amps[i] < 1.7)
            {
                _idle[i] = _idle[i] is { } idle
                    ? (idle.Volts + (volts[i] - idle.Volts) * Smoothing, idle.Amps + (amps[i] - idle.Amps) * Smoothing)
                    : (volts[i], amps[i]);
            }
            else if (_idle[i] is { } idle && amps[i] - idle.Amps >= 3.0)
            {
                double milliohms = Math.Max(0, (idle.Volts - volts[i]) / (amps[i] - idle.Amps) * 1000);
                PathMilliohms[i] = PathMilliohms[i] is { } previous ? previous + (milliohms - previous) * Smoothing : milliohms;
            }
        }
    }
}
