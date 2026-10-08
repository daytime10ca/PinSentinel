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

    /// <summary>A fault that returns this soon after a release is held throttled until the user releases it.</summary>
    public TimeSpan RetriggerWindow { get; set; } = TimeSpan.FromSeconds(600);
}

/// <summary>
/// Decides what to do about rule findings: warn, then throttle. A fault that comes back
/// soon after the throttle is released is throttled again and held until the user releases it.
/// Shutdown is kept for faults that throttling does not clear and for shutdown-level rules.
/// </summary>
public sealed class GuardController(GuardOptions options, IGuardActions actions)
{
    private enum State { Normal, Throttled, Held, ShutdownIssued }

    private State _state;
    private bool _shutdownPerformed;
    private bool _releaseRequested;
    private DateTimeOffset _throttledAt;
    private DateTimeOffset _lastFault;
    private DateTimeOffset? _lastRelease;
    private readonly HashSet<string> _notified = [];

    public bool IsThrottled => _state is State.Throttled or State.Held;

    /// <summary>Throttled and waiting for the user, because the fault returned after an automatic release.</summary>
    public bool IsHeld => _state == State.Held;

    /// <summary>Asks for a held throttle to be released. Honoured on the next sample, and only if the fault is absent.</summary>
    public void RequestRelease() => _releaseRequested = true;

    public void Process(Evaluation eval, DateTimeOffset now)
    {
        foreach (var f in eval.Findings)
            if (_notified.Add(f.RuleId)) actions.Notify(f.Severity, f.Message);
        _notified.IntersectWith(eval.Findings.Select(f => f.RuleId));

        string reason = string.Join("; ", eval.Findings.Where(f => f.Severity == eval.Severity).Select(f => f.Message));
        bool releaseRequested = _releaseRequested;
        _releaseRequested = false;

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
                    bool returned = _lastRelease is { } released && now - released < options.RetriggerWindow;
                    actions.Throttle(returned
                        ? $"The fault returned after the throttle was released, so GPU power stays cut until you release it from the PinSentinel tray icon. Check the power cable. {reason}"
                        : reason);
                    _state = returned ? State.Held : State.Throttled;
                    _throttledAt = _lastFault = now;
                }
                break;

            case State.Throttled:
            case State.Held:
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
                else if (_state == State.Held ? releaseRequested
                    : now - _throttledAt >= options.ThrottleMinHold && now - _lastFault >= options.ClearTime)
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
