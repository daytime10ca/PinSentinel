using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using PinSentinel.Core;

namespace PinSentinel.Service;

/// <summary>Carries out guard decisions on Windows: message box, nvidia-smi, shutdown.exe.</summary>
public sealed class WindowsGuardActions(IOptions<ServiceOptions> options, GuardState state, ILogger<WindowsGuardActions> logger) : IGuardActions
{
    private readonly ServiceOptions _options = options.Value;
    private string? _previousPowerLimit;
    private bool _throttled;
    private bool _throttleApplied;

    /// <summary>Raised before throttle or shutdown so the caller can dump its sample history.</summary>
    public event Action<string>? Incident;

    private string Prefix => state.DryRun ? "[dry run] " : "";

    public void Notify(Severity severity, string message)
    {
        logger.Log(severity == Severity.Warn ? LogLevel.Warning : LogLevel.Error, "{Severity}: {Message}", severity, message);
        ShowMessage($"{Prefix}GPU power connector: {severity}", message);
    }

    public void Throttle(string reason)
    {
        if (_throttled) return;
        _throttled = true;
        Incident?.Invoke($"throttle: {reason}");
        logger.LogError("{Prefix}Throttling GPU: {Reason}", Prefix, reason);
        ShowMessage($"{Prefix}GPU throttled", $"GPU power has been cut because of a connector fault.\n\n{reason}");
        if (state.DryRun) return;
        _throttleApplied = true;

        // The clock lock does most of the work; the lowest power limit on a 5090 is still 400 W.
        _previousPowerLimit ??= NvidiaSmi("--query-gpu=power.limit --format=csv,noheader,nounits")?.Trim();
        string? min = NvidiaSmi("--query-gpu=power.min_limit --format=csv,noheader,nounits")?.Trim();
        if (double.TryParse(min, CultureInfo.InvariantCulture, out double minWatts))
            NvidiaSmi($"-pl {minWatts.ToString(CultureInfo.InvariantCulture)}");
        NvidiaSmi($"-lgc 0,{_options.ThrottleClockMhz}");
    }

    public void ReleaseThrottle()
    {
        if (!_throttled) return;
        _throttled = false;
        logger.LogWarning("{Prefix}Releasing GPU throttle", Prefix);
        // Undo a real throttle even if the guard was disarmed in the meantime.
        if (!_throttleApplied) return;
        _throttleApplied = false;

        NvidiaSmi("-rgc");
        if (_previousPowerLimit is { Length: > 0 } limit) NvidiaSmi($"-pl {limit}");
        _previousPowerLimit = null;
    }

    public bool Shutdown(string reason)
    {
        Incident?.Invoke($"shutdown: {reason}");
        logger.LogCritical("{Prefix}Shutting down: {Reason}", Prefix, reason);
        if (state.DryRun)
        {
            ShowMessage("[dry run] GPU power connector: shutdown", $"PinSentinel would have shut the PC down.\n\n{reason}");
            return false;
        }

        string comment = $"PinSentinel: GPU power connector fault. {reason}";
        if (comment.Length > 500) comment = comment[..500];
        Run("shutdown.exe", $"/s /f /t {_options.ShutdownDelaySeconds} /c \"{comment.Replace('"', '\'')}\"");
        return true;
    }

    /// <summary>Exercises the same notification path a real warning uses.</summary>
    public void ShowTestAlert()
    {
        logger.LogInformation("Test alert requested");
        ShowMessage("PinSentinel test alert", "This is a test. No fault was detected.\n\nA real connector warning will appear like this.");
    }

    private string? NvidiaSmi(string arguments) => Run("nvidia-smi.exe", arguments);

    private string? Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10_000)) process.Kill();
            if (process.ExitCode != 0)
                logger.LogError("{File} {Arguments} exited {Code}: {Output}", file, arguments, process.ExitCode, (output + error).Trim());
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not run {File} {Arguments}", file, arguments);
            return null;
        }
    }

    // WTSSendMessage reaches the logged-on desktop even when this runs as a service in session 0.
    private void ShowMessage(string title, string text)
    {
        try
        {
            uint session = WTSGetActiveConsoleSessionId();
            if (session == 0xFFFFFFFF) return;
            const uint MB_ICONWARNING = 0x30, MB_SYSTEMMODAL = 0x1000, MB_SETFOREGROUND = 0x10000;
            if (!WTSSendMessage(IntPtr.Zero, session, title, title.Length * 2, text, text.Length * 2,
                    MB_ICONWARNING | MB_SYSTEMMODAL | MB_SETFOREGROUND, 300, out _, false))
                logger.LogWarning("Notification failed, Win32 error {Error}", Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not show notification");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSSendMessage(IntPtr server, uint sessionId, string title, int titleBytes,
        string message, int messageBytes, uint style, int timeoutSeconds, out int response, bool wait);
}
