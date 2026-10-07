using PinSentinel.Core;

namespace PinSentinel.Service;

public sealed class ServiceOptions
{
    /// <summary>
    /// When true the guard logs and notifies what it would do, but never throttles
    /// or shuts down. Leave on until a baseline has been logged on this card.
    /// </summary>
    public bool DryRun { get; set; } = true;

    public int PollIntervalMs { get; set; } = 500;

    /// <summary>Logs, CSVs and incident dumps. Defaults to %ProgramData%\PinSentinel.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Healthy samples are logged this often; anything abnormal is always logged.</summary>
    public int BaselineLogSeconds { get; set; } = 5;

    public int LogRetentionDays { get; set; } = 30;

    /// <summary>Samples kept in memory and dumped to disk on throttle or shutdown.</summary>
    public int FlightRecorderSamples { get; set; } = 240;

    /// <summary>GPU clock ceiling applied while throttled, via nvidia-smi --lock-gpu-clocks.</summary>
    public int ThrottleClockMhz { get; set; } = 500;

    /// <summary>Delay before Windows powers off, so the reason stays on screen briefly.</summary>
    public int ShutdownDelaySeconds { get; set; } = 10;

    public RuleOptions Rules { get; set; } = new();
    public GuardOptions Guard { get; set; } = new();

    public string ResolveDataDirectory() => DataDirectory is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PinSentinel");
}
