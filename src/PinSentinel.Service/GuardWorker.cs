using Microsoft.Extensions.Options;
using PinSentinel.Core;

namespace PinSentinel.Service;

public sealed class GuardWorker(
    IOptions<ServiceOptions> options,
    WindowsGuardActions actions,
    StatusBroadcaster broadcaster,
    ControlServer control,
    GuardState state,
    ILogger<GuardWorker> logger) : BackgroundService
{
    private readonly ServiceOptions _options = options.Value;
    private readonly Queue<string> _recorder = new();

    // NVAPI calls stay on one dedicated thread rather than hopping across the pool.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Run(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken stop)
    {
        string dataDir = _options.ResolveDataDirectory();
        Directory.CreateDirectory(dataDir);
        actions.Incident += reason => DumpRecorder(dataDir, reason);

        var sensor = OpenSensor(stop);
        if (sensor is null) return;

        logger.LogInformation("Monitoring {Card} ({SubSystem}), dry run {DryRun}, data in {Dir}",
            AstralSensor.SupportedCards[sensor.Gpu.SubSystemId], sensor.Gpu.SubSystemText, state.DryRun, dataDir);

        string card = AstralSensor.SupportedCards[sensor.Gpu.SubSystemId];
        broadcaster.Start(stop);
        control.Start(stop);

        using var log = new CsvLog(Path.Combine(dataDir, "logs"), _options.LogRetentionDays);
        var engine = new RuleEngine(_options.Rules);
        var guard = new GuardController(_options.Guard, actions);
        var lastLogged = DateTimeOffset.MinValue;

        while (!stop.IsCancellationRequested)
        {
            try
            {
                var frame = sensor.Read();
                var eval = frame is null ? engine.EvaluateReadFailure() : engine.Evaluate(frame);
                var now = frame?.Timestamp ?? DateTimeOffset.Now;

                if (frame is not null)
                {
                    _recorder.Enqueue(CsvLog.Format(frame, eval));
                    while (_recorder.Count > _options.FlightRecorderSamples) _recorder.Dequeue();

                    if (eval.Severity != Severity.Ok || (now - lastLogged).TotalSeconds >= _options.BaselineLogSeconds)
                    {
                        log.Write(frame, eval);
                        lastLogged = now;
                    }
                }

                state.LastWatts = frame?.Watts ?? 0;
                guard.Process(eval, now);
                broadcaster.Publish(StatusMessage.From(card, frame, eval, guard.IsThrottled || state.TestThrottleActive, state.DryRun, now));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Monitor loop error");
            }

            stop.WaitHandle.WaitOne(_options.PollIntervalMs);
        }

        if (guard.IsThrottled)
            logger.LogWarning("Stopping while the GPU is throttled. The throttle stays on until reboot or 'nvidia-smi -rgc'.");
    }

    // At boot the service can start before the NVIDIA driver is ready.
    private AstralSensor? OpenSensor(CancellationToken stop)
    {
        bool reported = false;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (AstralSensor.Open() is { } sensor) return sensor;
                if (!reported) logger.LogWarning("No supported ROG Astral card found yet, retrying");
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException)
            {
                if (!reported) logger.LogWarning(ex, "NVAPI not ready, retrying");
            }
            reported = true;
            stop.WaitHandle.WaitOne(10_000);
        }
        return null;
    }

    private void DumpRecorder(string dataDir, string reason)
    {
        try
        {
            string dir = Path.Combine(dataDir, "incidents");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"incident-{DateTimeOffset.Now:yyyy-MM-dd_HH-mm-ss}.csv");
            File.WriteAllLines(path, [$"# {reason}", CsvLog.Header, .. _recorder]);
            logger.LogWarning("Wrote incident record {Path}", path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not write incident record");
        }
    }
}
