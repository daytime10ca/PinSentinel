using PinSentinel.Core;

namespace PinSentinel.Tests;

public class RuleEngineTests
{
    private readonly RuleEngine _engine = new(new RuleOptions());
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    private static PinFrame Frame(DateTimeOffset t, double[] amps, double[]? volts = null) =>
        new(t, amps.Select((a, i) => new PinReading(volts?[i] ?? 12.0, a)).ToArray());

    /// <summary>Feeds the same readings at 2 Hz for the given time and returns the last evaluation.</summary>
    private Evaluation Run(double seconds, double[] amps, double[]? volts = null)
    {
        Evaluation result = Evaluation.Ok;
        for (int i = 0; i <= seconds * 2; i++)
        {
            result = _engine.Evaluate(Frame(_now, amps, volts));
            _now += TimeSpan.FromSeconds(0.5);
        }
        return result;
    }

    [Fact]
    public void BalancedFullLoadIsOk() =>
        Assert.Equal(Severity.Ok, Run(120, [8.0, 8.1, 7.9, 8.0, 8.05, 7.95]).Severity);

    [Fact]
    public void IdleNoiseDoesNotTriggerImbalance() =>
        Assert.Equal(Severity.Ok, Run(120, [0.2, 1.4, 1.0, 1.0, 0.1, 1.1]).Severity);

    [Fact]
    public void OvercurrentNeedsToPersist()
    {
        double[] amps = [9.6, 8.5, 8.5, 8.5, 8.5, 8.5];
        Assert.Equal(Severity.Ok, Run(9, amps).Severity);
        Assert.Equal(Severity.Throttle, Run(1, amps).Severity);
    }

    [Fact]
    public void BriefSpikeResetsTheHold()
    {
        double[] hot = [9.6, 8.5, 8.5, 8.5, 8.5, 8.5];
        double[] normal = [8.0, 8.0, 8.0, 8.0, 8.0, 8.0];
        Run(8, hot);
        Run(1, normal);
        Assert.Equal(Severity.Ok, Run(8, hot).Severity);
    }

    [Fact]
    public void SevereOvercurrentShutsDownFast()
    {
        var result = Run(1, [16.5, 6.0, 6.0, 6.0, 6.0, 6.0]);
        Assert.Equal(Severity.Shutdown, result.Severity);
        Assert.Contains(result.Findings, f => f.RuleId == "pin-amps-shutdown-fast");
    }

    [Fact]
    public void PartialLoadImbalanceIsCaughtBelowAbsoluteLimits()
    {
        // 25 A total (~300 W): no pin near 9 A, but pin 1 is far off its share.
        double[] amps = [8.0, 3.4, 3.4, 3.4, 3.4, 3.4];
        Assert.Equal(Severity.Ok, Run(14, amps).Severity);
        var result = Run(1, amps);
        Assert.Equal(Severity.Throttle, result.Severity);
        Assert.Contains(result.Findings, f => f.RuleId == "imbalance-throttle");
        Assert.Equal(Severity.Shutdown, Run(15, amps).Severity);
    }

    [Fact]
    public void ModerateImbalanceOnlyWarns()
    {
        var result = Run(60, [5.2, 4.0, 4.0, 4.0, 4.0, 4.0]);
        Assert.Equal(Severity.Warn, result.Severity);
    }

    [Fact]
    public void OpenPinEscalates()
    {
        double[] amps = [0.0, 6.0, 6.0, 6.0, 6.0, 6.0];
        Assert.Contains(Run(5, amps).Findings, f => f.RuleId == "open-pin-warn");
        Assert.Contains(Run(10, amps).Findings, f => f.RuleId == "open-pin-throttle");
        Assert.Equal(Severity.Shutdown, Run(30, amps).Severity);
    }

    [Fact]
    public void VoltageSpreadUnderLoad()
    {
        double[] amps = [6.0, 6.0, 6.0, 6.0, 6.0, 6.0];
        Assert.Equal(Severity.Warn, Run(30, amps, [11.80, 11.98, 11.98, 11.98, 11.98, 11.98]).Severity);
        Assert.Equal(Severity.Throttle, Run(15, amps, [11.70, 11.98, 11.98, 11.98, 11.98, 11.98]).Severity);
    }

    [Fact]
    public void ClearsWhenLoadDrops()
    {
        Run(60, [8.0, 3.4, 3.4, 3.4, 3.4, 3.4]);
        Assert.Equal(Severity.Ok, Run(1, [1.5, 0.5, 0.5, 0.5, 0.5, 0.5]).Severity);
    }

    [Fact]
    public void SensorLossWarnsAfterConsecutiveFailures()
    {
        for (int i = 0; i < 4; i++) Assert.Equal(Severity.Ok, _engine.EvaluateReadFailure().Severity);
        Assert.Equal(Severity.Warn, _engine.EvaluateReadFailure().Severity);

        Assert.Equal(Severity.Ok, Run(0, [1, 1, 1, 1, 1, 1]).Severity);
        Assert.Equal(Severity.Ok, _engine.EvaluateReadFailure().Severity);
    }
}
