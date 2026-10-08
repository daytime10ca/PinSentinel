using System.Globalization;

namespace PinSentinel.Core;

public readonly record struct LogSample(DateTimeOffset Time, double[] Volts, double[] Amps);

/// <summary>How the connector behaved under load on one day.</summary>
public sealed record DayHealth(
    DateOnly Day,
    double LoadedMinutes,
    double[] Share,          // each pin's mean fraction of total current under load
    double?[] Milliohms,     // estimated supply-path resistance per pin; null without enough idle and load data
    double MaxPinAmps,
    double MeanImbalance);

public enum HealthVerdict { NotEnoughData, Stable, Drifting }

public sealed record HealthReport(
    IReadOnlyList<DayHealth> Days,
    HealthVerdict Verdict,
    double[]? ShareDrift,    // percentage points, latest day minus baseline
    double?[]? MilliohmDrift,
    string Summary)
{
    public DayHealth? Latest => Days.Count > 0 ? Days[^1] : null;
    public double LoadedHours => Days.Sum(d => d.LoadedMinutes) / 60;
}

/// <summary>
/// Looks for slow change in how the six pins share the load. A contact that is wearing
/// carries a shrinking share and shows a rising path resistance well before any limit trips.
/// </summary>
public static class HealthAnalyzer
{
    public const double LoadGateAmps = 15.0;
    public const double IdleAmps = 10.0;
    public const double MinLoadedMinutes = 5.0;
    public const int MinDaysForTrend = 3;
    public const int BaselineDays = 5;

    // Starting points, not validated against a failing connector.
    public const double ShareDriftPoints = 1.0;
    public const double MilliohmDriftFraction = 0.30;
    public const double MilliohmDriftMin = 3.0;

    public static DayHealth? AnalyzeDay(DateOnly day, IEnumerable<LogSample> samples)
    {
        const int n = PinFrame.PinCount;
        double[] share = new double[n], loadV = new double[n], loadA = new double[n], idleV = new double[n], idleA = new double[n];
        int loaded = 0, idle = 0;
        double loadedSeconds = 0, maxPin = 0, imbalance = 0;
        DateTimeOffset? previous = null;

        foreach (var s in samples)
        {
            if (s.Amps.Length != n || s.Volts.Length != n) continue;
            double total = s.Amps.Sum();
            double gap = previous is { } p ? Math.Clamp((s.Time - p).TotalSeconds, 0, 10) : 0;
            previous = s.Time;

            if (total >= LoadGateAmps)
            {
                loaded++;
                loadedSeconds += gap;
                double mean = total / n;
                imbalance += s.Amps.Max(a => Math.Abs(a - mean)) / mean;
                maxPin = Math.Max(maxPin, s.Amps.Max());
                for (int i = 0; i < n; i++)
                {
                    share[i] += s.Amps[i] / total;
                    loadV[i] += s.Volts[i];
                    loadA[i] += s.Amps[i];
                }
            }
            else if (total < IdleAmps)
            {
                idle++;
                for (int i = 0; i < n; i++)
                {
                    idleV[i] += s.Volts[i];
                    idleA[i] += s.Amps[i];
                }
            }
        }

        if (loaded == 0) return null;

        var milliohms = new double?[n];
        for (int i = 0; i < n; i++)
        {
            share[i] /= loaded;
            double deltaAmps = loadA[i] / loaded - (idle > 0 ? idleA[i] / idle : 0);
            if (loaded >= 12 && idle >= 12 && deltaAmps >= 3)
                milliohms[i] = Math.Max(0, (idleV[i] / idle - loadV[i] / loaded) / deltaAmps * 1000);
        }
        return new DayHealth(day, loadedSeconds / 60, share, milliohms, maxPin, imbalance / loaded);
    }

    public static HealthReport Report(IEnumerable<DayHealth> allDays)
    {
        var days = allDays.Where(d => d.LoadedMinutes >= MinLoadedMinutes).OrderBy(d => d.Day).ToList();
        if (days.Count < MinDaysForTrend)
        {
            string need = days.Count == 0
                ? "No day has at least 5 minutes under load yet."
                : $"{days.Count} of {MinDaysForTrend} days with load recorded. A trend needs {MinDaysForTrend}.";
            return new HealthReport(days, HealthVerdict.NotEnoughData, null, null, need);
        }

        const int n = PinFrame.PinCount;
        var latest = days[^1];
        var baseline = days.Take(Math.Min(BaselineDays, days.Count - 1)).ToList();

        var shareDrift = new double[n];
        var ohmDrift = new double?[n];
        bool drifting = false;
        for (int i = 0; i < n; i++)
        {
            shareDrift[i] = (latest.Share[i] - baseline.Average(d => d.Share[i])) * 100;
            if (Math.Abs(shareDrift[i]) >= ShareDriftPoints) drifting = true;

            var ohms = baseline.Where(d => d.Milliohms[i] is not null).Select(d => d.Milliohms[i]!.Value).ToList();
            if (latest.Milliohms[i] is { } now && ohms.Count > 0)
            {
                double before = ohms.Average();
                ohmDrift[i] = now - before;
                if (now - before >= MilliohmDriftMin && now - before >= before * MilliohmDriftFraction) drifting = true;
            }
        }

        int worst = Enumerable.Range(0, n).MaxBy(i => Math.Abs(shareDrift[i]));
        double hours = days.Sum(d => d.LoadedMinutes) / 60;
        string summary = string.Create(CultureInfo.InvariantCulture,
            $"{days.Count} days and {hours:F1} h under load. Largest change: pin {worst + 1} share {Math.Round(shareDrift[worst], 1):+0.0;-0.0;0.0} points since baseline.");
        return new HealthReport(days, drifting ? HealthVerdict.Drifting : HealthVerdict.Stable, shareDrift, ohmDrift, summary);
    }

    /// <summary>Reads a PinSentinel CSV log, including one the service still has open.</summary>
    public static IEnumerable<LogSample> ReadCsv(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var inv = CultureInfo.InvariantCulture;
        while (reader.ReadLine() is { } line)
        {
            string[] f = line.Split(',');
            if (f.Length < 13 || !DateTimeOffset.TryParse(f[0], inv, DateTimeStyles.RoundtripKind, out var time)) continue;

            double[] volts = new double[6], amps = new double[6];
            bool ok = true;
            for (int i = 0; i < 6 && ok; i++)
                ok = double.TryParse(f[1 + i], NumberStyles.Float, inv, out volts[i])
                  && double.TryParse(f[7 + i], NumberStyles.Float, inv, out amps[i]);
            if (ok) yield return new LogSample(time, volts, amps);
        }
    }
}
