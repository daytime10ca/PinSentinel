using PinSentinel.Core;

namespace PinSentinel.Tests;

public class HealthAnalyzerTests
{
    private static readonly double[] Even = [1, 1, 1, 1, 1, 1];

    /// <summary>Ten minutes idle then twenty minutes at ~45 A, sampled every 5 s.</summary>
    private static DayHealth Day(int dayIndex, double[] weights, double pin3ExtraMilliohms = 0)
    {
        var day = new DateOnly(2026, 10, 1).AddDays(dayIndex);
        var start = new DateTimeOffset(day.ToDateTime(new TimeOnly(20, 0)), TimeSpan.Zero);
        double weightSum = weights.Sum();
        var samples = new List<LogSample>();
        for (int i = 0; i < 360; i++)
        {
            double total = i < 120 ? 6.0 : 45.0;
            double[] amps = [.. weights.Select(w => total * w / weightSum)];
            double[] volts = [.. amps.Select((a, pin) => 12.02 - a * (0.018 + (pin == 2 ? pin3ExtraMilliohms / 1000 : 0)))];
            samples.Add(new LogSample(start.AddSeconds(i * 5), volts, amps));
        }
        return HealthAnalyzer.AnalyzeDay(day, samples)!;
    }

    [Fact]
    public void DaySummarisesLoadedSamplesOnly()
    {
        var day = Day(0, [0.95, 1, 1, 1.02, 1.02, 1.01]);
        Assert.Equal(20, day.LoadedMinutes, 0);
        Assert.Equal(0.95 / 6.0, day.Share[0], 3);
        Assert.Equal(1.0, day.Share.Sum(), 6);
        Assert.Equal(18, day.Milliohms[0]!.Value, 1);
    }

    [Fact]
    public void DayWithoutLoadIsIgnored()
    {
        var idle = Enumerable.Range(0, 100).Select(i => new LogSample(DateTimeOffset.UnixEpoch.AddSeconds(i * 5), [12, 12, 12, 12, 12, 12], Even));
        Assert.Null(HealthAnalyzer.AnalyzeDay(new DateOnly(2026, 10, 1), idle));
    }

    [Fact]
    public void TrendNeedsThreeDays()
    {
        var report = HealthAnalyzer.Report([Day(0, Even), Day(1, Even)]);
        Assert.Equal(HealthVerdict.NotEnoughData, report.Verdict);
        Assert.Equal(2, report.Days.Count);
    }

    [Fact]
    public void SteadySharingIsStable()
    {
        var report = HealthAnalyzer.Report(Enumerable.Range(0, 10).Select(i => Day(i, [0.95, 1, 1.01, 1.02, 1.01, 1])));
        Assert.Equal(HealthVerdict.Stable, report.Verdict);
        Assert.All(report.ShareDrift!, d => Assert.InRange(d, -0.05, 0.05));
    }

    [Fact]
    public void ShrinkingShareIsDrift()
    {
        var days = Enumerable.Range(0, 10).Select(i => Day(i, [1, 1, 1 - i * 0.012, 1, 1, 1]));
        var report = HealthAnalyzer.Report(days);
        Assert.Equal(HealthVerdict.Drifting, report.Verdict);
        Assert.True(report.ShareDrift![2] < -1.0);
        Assert.Contains("pin 3", report.Summary);
    }

    [Fact]
    public void RisingResistanceAloneIsDrift()
    {
        var days = Enumerable.Range(0, 6).Select(i => Day(i, Even, pin3ExtraMilliohms: i == 5 ? 8 : 0));
        var report = HealthAnalyzer.Report(days);
        Assert.Equal(HealthVerdict.Drifting, report.Verdict);
        Assert.Equal(8, report.MilliohmDrift![2]!.Value, 1);
    }

    [Fact]
    public void ReadsBackTheServiceLogFormat()
    {
        string path = Path.GetTempFileName();
        try
        {
            var frame = new PinFrame(new DateTimeOffset(2026, 10, 6, 23, 7, 11, TimeSpan.FromHours(-4)),
                [new(11.88, 7.44), new(11.872, 7.78), new(11.872, 7.76), new(11.88, 7.74), new(11.88, 7.68), new(11.88, 7.58)]);
            File.WriteAllLines(path, [CsvLog.Header, CsvLog.Format(frame, Evaluation.Ok), "garbage,line"]);

            var sample = Assert.Single(HealthAnalyzer.ReadCsv(path));
            Assert.Equal(frame.Timestamp, sample.Time);
            Assert.Equal(7.44, sample.Amps[0]);
            Assert.Equal(11.872, sample.Volts[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
