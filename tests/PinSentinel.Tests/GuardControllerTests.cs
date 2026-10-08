using PinSentinel.Core;

namespace PinSentinel.Tests;

public class GuardControllerTests
{
    private sealed class FakeActions(bool performShutdown = true) : IGuardActions
    {
        public List<string> Calls { get; } = [];
        public void Notify(Severity severity, string message) => Calls.Add($"notify:{severity}");
        public void Throttle(string reason) => Calls.Add("throttle");
        public void ReleaseThrottle() => Calls.Add("release");
        public bool Shutdown(string reason) { Calls.Add("shutdown"); return performShutdown; }
    }

    private static readonly Evaluation Warn = Eval(Severity.Warn);
    private static readonly Evaluation Throttle = Eval(Severity.Throttle);
    private static readonly Evaluation Shutdown = Eval(Severity.Shutdown);

    private static Evaluation Eval(Severity s) => new(s, [new Finding($"rule-{s}", s, "test")]);

    private readonly FakeActions _actions = new();
    private readonly GuardController _guard;
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public GuardControllerTests() => _guard = new GuardController(new GuardOptions(), _actions);

    private void Run(double seconds, Evaluation eval, GuardController? guard = null)
    {
        for (int i = 0; i < seconds * 2; i++)
        {
            (guard ?? _guard).Process(eval, _now);
            _now += TimeSpan.FromSeconds(0.5);
        }
    }

    [Fact]
    public void WarningNotifiesOnceAndTakesNoAction()
    {
        Run(60, Warn);
        Assert.Equal(["notify:Warn"], _actions.Calls);
    }

    [Fact]
    public void WarningNotifiesAgainAfterClearing()
    {
        Run(5, Warn);
        Run(5, Evaluation.Ok);
        Run(5, Warn);
        Assert.Equal(2, _actions.Calls.Count(c => c == "notify:Warn"));
    }

    [Fact]
    public void ThrottleThatClearsTheFaultIsReleasedAfterHold()
    {
        Run(1, Throttle);
        Run(118, Evaluation.Ok);
        Assert.True(_guard.IsThrottled);
        Run(2, Evaluation.Ok);
        Assert.False(_guard.IsThrottled);
        Assert.Equal(["notify:Throttle", "throttle", "release"], _actions.Calls);
    }

    [Fact]
    public void FaultThatSurvivesThrottlingShutsDown()
    {
        Run(9.5, Throttle);
        Assert.DoesNotContain("shutdown", _actions.Calls);
        Run(1, Throttle);
        Assert.Single(_actions.Calls, c => c == "shutdown");
    }

    /// <summary>Throttles, lets the throttle release, then brings the fault straight back.</summary>
    private void ReturnAfterRelease()
    {
        Run(1, Throttle);
        Run(130, Evaluation.Ok);
        Run(1, Throttle);
    }

    [Fact]
    public void FaultReturningSoonAfterReleaseIsHeldNotShutDown()
    {
        ReturnAfterRelease();
        Assert.True(_guard.IsHeld);
        Run(3600, Evaluation.Ok);
        Assert.True(_guard.IsThrottled);
        Assert.DoesNotContain("shutdown", _actions.Calls);
        Assert.Equal(1, _actions.Calls.Count(c => c == "release"));
    }

    [Fact]
    public void HeldThrottleIsReleasedOnRequestOnceTheFaultIsGone()
    {
        ReturnAfterRelease();
        Run(5, Evaluation.Ok);
        _guard.RequestRelease();
        Run(1, Evaluation.Ok);
        Assert.False(_guard.IsThrottled);
        Assert.Equal(2, _actions.Calls.Count(c => c == "release"));
    }

    [Fact]
    public void ReleaseRequestIsIgnoredOutsideAHold()
    {
        Run(1, Throttle);
        _guard.RequestRelease();
        Run(5, Evaluation.Ok);
        Assert.True(_guard.IsThrottled);
        Assert.False(_guard.IsHeld);
    }

    [Fact]
    public void HeldFaultThatSurvivesThrottlingStillShutsDown()
    {
        ReturnAfterRelease();
        Run(9, Throttle);
        Assert.DoesNotContain("shutdown", _actions.Calls);
        _guard.RequestRelease();
        Run(1, Throttle);
        Assert.Single(_actions.Calls, c => c == "shutdown");
    }

    [Fact]
    public void ShutdownSeverityWhileHeldShutsDown()
    {
        ReturnAfterRelease();
        Run(1, Shutdown);
        Assert.Equal("shutdown", _actions.Calls[^1]);
    }

    [Fact]
    public void FaultReturningMuchLaterThrottlesAgain()
    {
        Run(1, Throttle);
        Run(130, Evaluation.Ok);
        Run(700, Evaluation.Ok);
        Run(1, Throttle);
        Assert.DoesNotContain("shutdown", _actions.Calls);
        Assert.True(_guard.IsThrottled);
    }

    [Fact]
    public void ShutdownSeverityThrottlesAndShutsDownOnce()
    {
        Run(5, Shutdown);
        Assert.Equal(["notify:Shutdown", "throttle", "shutdown"], _actions.Calls);
    }

    [Fact]
    public void DryRunRearmsOnceTheFaultClears()
    {
        var actions = new FakeActions(performShutdown: false);
        var guard = new GuardController(new GuardOptions(), actions);
        Run(2, Shutdown, guard);
        Run(2, Evaluation.Ok, guard);
        Run(2, Shutdown, guard);
        Assert.Equal(2, actions.Calls.Count(c => c == "shutdown"));
    }
}
