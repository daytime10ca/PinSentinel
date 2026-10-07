namespace PinSentinel.Core;

public interface IGuardActions
{
    void Notify(Severity severity, string message);

    /// <summary>Cuts GPU power. Must be safe to call repeatedly.</summary>
    void Throttle(string reason);

    void ReleaseThrottle();

    /// <summary>Returns false when no shutdown was actually started (dry run).</summary>
    bool Shutdown(string reason);
}

public sealed class GuardOptions
{
    /// <summary>A fault still present this long after throttling forces a shutdown.</summary>
    public TimeSpan ThrottleGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The throttle stays on at least this long.</summary>
    public TimeSpan ThrottleMinHold { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Time without a throttle-level fault before the throttle is released.</summary>
    public TimeSpan ClearTime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>A fault that returns this soon after a release is treated as persistent.</summary>
    public TimeSpan RetriggerWindow { get; set; } = TimeSpan.FromSeconds(600);
}

/// <summary>
/// Decides what to do about rule findings: warn, throttle, then shut down if
/// throttling does not clear the fault or the fault keeps coming back.
/// </summary>
public sealed class GuardController(GuardOptions options, IGuardActions actions)
{
    private enum State { Normal, Throttled, ShutdownIssued }

    private State _state;
    private bool _shutdownPerformed;
    private DateTimeOffset _throttledAt;
    private DateTimeOffset _lastFault;
    private DateTimeOffset? _lastRelease;
    private readonly HashSet<string> _notified = [];

    public bool IsThrottled => _state == State.Throttled;

    public void Process(Evaluation eval, DateTimeOffset now)
    {
        foreach (var f in eval.Findings)
            if (_notified.Add(f.RuleId)) actions.Notify(f.Severity, f.Message);
        _notified.IntersectWith(eval.Findings.Select(f => f.RuleId));

        string reason = string.Join("; ", eval.Findings.Where(f => f.Severity == eval.Severity).Select(f => f.Message));

        switch (_state)
        {
            case State.Normal:
                if (eval.Severity == Severity.Shutdown)
                {
                    actions.Throttle(reason);
                    Shutdown(reason);
                }
                else if (eval.Severity == Severity.Throttle)
                {
                    actions.Throttle(reason);
                    if (_lastRelease is { } released && now - released < options.RetriggerWindow)
                    {
                        Shutdown($"fault returned after the throttle was released: {reason}");
                        break;
                    }
                    _state = State.Throttled;
                    _throttledAt = _lastFault = now;
                }
                break;

            case State.Throttled:
                if (eval.Severity == Severity.Shutdown)
                {
                    Shutdown(reason);
                }
                else if (eval.Severity == Severity.Throttle)
                {
                    _lastFault = now;
                    if (now - _throttledAt >= options.ThrottleGrace)
                        Shutdown($"fault persists despite throttling: {reason}");
                }
                else if (now - _throttledAt >= options.ThrottleMinHold && now - _lastFault >= options.ClearTime)
                {
                    actions.ReleaseThrottle();
                    _state = State.Normal;
                    _lastRelease = now;
                }
                break;

            case State.ShutdownIssued:
                // Only reachable for long in a dry run; rearm once the fault has gone.
                if (!_shutdownPerformed && eval.Severity == Severity.Ok)
                {
                    actions.ReleaseThrottle();
                    _state = State.Normal;
                    _lastRelease = null;
                }
                break;
        }
    }

    private void Shutdown(string reason)
    {
        _state = State.ShutdownIssued;
        _shutdownPerformed = actions.Shutdown(reason);
    }
}
