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
    private bool _testRunning;
    private readonly Lock _hardware = new();

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
        lock (_hardware)
        {
            _throttleApplied = true;
            ApplyHardwareThrottle();
        }
    }

    /// <summary>Returns the commands that failed, empty when the throttle went on cleanly.</summary>
    private List<string> ApplyHardwareThrottle()
    {
        var failed = new List<string>();
        // The clock lock does most of the work; the lowest power limit on a 5090 is still 400 W.
        _previousPowerLimit ??= NvidiaSmi("--query-gpu=power.limit --format=csv,noheader,nounits")?.Trim();
        string? min = NvidiaSmi("--query-gpu=power.min_limit --format=csv,noheader,nounits")?.Trim();
        if (!double.TryParse(min, CultureInfo.InvariantCulture, out double minWatts)) failed.Add("query minimum power limit");
        else if (NvidiaSmi($"-pl {minWatts.ToString(CultureInfo.InvariantCulture)}") is null) failed.Add($"set power limit to {minWatts:F0} W");
        if (NvidiaSmi($"-lgc 0,{_options.ThrottleClockMhz}") is null) failed.Add($"lock GPU clock to {_options.ThrottleClockMhz} MHz");
        return failed;
    }

    private void RemoveHardwareThrottle()
    {
        NvidiaSmi("-rgc");
        if (_previousPowerLimit is { Length: > 0 } limit) NvidiaSmi($"-pl {limit}");
        _previousPowerLimit = null;
    }

    public void ReleaseThrottle()
    {
        if (!_throttled) return;
        _throttled = false;
        logger.LogWarning("{Prefix}Releasing GPU throttle", Prefix);
        // Undo a real throttle even if the guard was disarmed in the meantime.
        lock (_hardware)
        {
            if (!_throttleApplied) return;
            _throttleApplied = false;
            // A running throttle test removes the hardware throttle itself when it ends.
            if (!_testRunning) RemoveHardwareThrottle();
        }
    }

    /// <summary>
    /// Applies the real throttle for a short time regardless of dry run, and reports how far
    /// connector power fell. Proves the throttle works on this card before the guard is armed.
    /// </summary>
    public bool StartThrottleTest()
    {
        lock (_hardware)
        {
            if (_testRunning || _throttleApplied) return false;
            _testRunning = true;
        }
        _ = Task.Run(RunThrottleTest);
        return true;
    }

    private async Task RunThrottleTest()
    {
        const int throttleSeconds = 20, settleSeconds = 10;
        try
        {
            double before = await AverageWatts(5);
            List<string> failed;
            lock (_hardware) failed = ApplyHardwareThrottle();
            state.TestThrottleActive = true;
            await Task.Delay(TimeSpan.FromSeconds(throttleSeconds - settleSeconds));
            double during = await AverageWatts(settleSeconds);

            lock (_hardware) { if (!_throttleApplied) RemoveHardwareThrottle(); }
            state.TestThrottleActive = false;
            await Task.Delay(TimeSpan.FromSeconds(5));
            double after = await AverageWatts(5);

            string verdict = failed.Count > 0 ? "FAILED: " + string.Join("; ", failed.Select(f => "could not " + f))
                : before < 200 ? "INCONCLUSIVE: the GPU was not under enough load. Run it during a game or benchmark."
                : during > before * 0.6 ? "FAILED: power did not fall enough."
                : after < before * 0.8 ? "PARTIAL: power fell, but did not recover after release."
                : "PASSED";
            string result = $"{verdict}\n\nBefore: {before:F0} W\nThrottled: {during:F0} W\nAfter release: {after:F0} W";
            logger.LogWarning("Throttle test: {Result}", result.Replace("\n\n", " ").Replace('\n', ' '));
            ShowMessage("PinSentinel throttle test", result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Throttle test failed");
            lock (_hardware) { if (!_throttleApplied) RemoveHardwareThrottle(); }
        }
        finally
        {
            state.TestThrottleActive = false;
            lock (_hardware) _testRunning = false;
        }
    }

    private async Task<double> AverageWatts(int seconds)
    {
        double sum = 0;
        for (int i = 0; i < seconds * 2; i++)
        {
            await Task.Delay(500);
            sum += state.LastWatts;
        }
        return sum / (seconds * 2);
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
