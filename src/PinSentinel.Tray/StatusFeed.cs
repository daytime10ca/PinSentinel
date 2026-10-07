using System.IO;
using System.IO.Pipes;
using PinSentinel.Core;

namespace PinSentinel.Tray;

interface IStatusFeed
{
    /// <summary>Raised on a background thread. Null means the service connection was lost.</summary>
    event Action<StatusMessage?>? Received;
    void Start(CancellationToken stop);
}

/// <summary>Reads the service's status pipe and reconnects whenever it drops.</summary>
sealed class PipeFeed : IStatusFeed
{
    public event Action<StatusMessage?>? Received;

    public void Start(CancellationToken stop) => _ = Task.Run(() => Run(stop), stop);

    private async Task Run(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", StatusMessage.PipeName, PipeDirection.In, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000, stop);
                using var reader = new StreamReader(pipe);
                while (await reader.ReadLineAsync(stop) is { } line)
                    if (StatusMessage.FromJson(line) is { } message) Received?.Invoke(message);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { }
            catch (OperationCanceledException) { return; }

            Received?.Invoke(null);
            await Task.Delay(2000, stop).ContinueWith(_ => { });
        }
    }
}

/// <summary>Synthetic data for working on the UI without the service or the card.</summary>
sealed class DemoFeed(bool fault) : IStatusFeed
{
    public event Action<StatusMessage?>? Received;

    private readonly RuleEngine _engine = new(new RuleOptions());
    private readonly Random _random = new(5090);
    private DateTimeOffset _time = DateTimeOffset.Now.AddMinutes(-5);
    private int _tick;

    public void Start(CancellationToken stop) => _ = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            Received?.Invoke(Next());
            await Task.Delay(500, stop).ContinueWith(_ => { });
        }
    }, stop);

    public StatusMessage Next()
    {
        _tick++;
        _time += TimeSpan.FromSeconds(0.5);

        // Idle for a while, then a game-like load with slow swings.
        double load = _tick < 60 ? 0.13 : 0.78 + 0.18 * Math.Sin(_tick / 37.0) + 0.04 * Math.Sin(_tick / 5.0);
        double perPin = load * 8.0;
        double[] share = [0.955, 1.003, 1.009, 1.019, 1.011, 1.003];
        if (fault && _tick > 330)
        {
            // Pin 3's neighbour loses contact: its current moves onto pin 3.
            double shift = Math.Min(1, (_tick - 330) / 120.0) * 0.36;
            share[1] -= shift;
            share[2] += shift;
        }

        var pins = new PinReading[PinFrame.PinCount];
        for (int i = 0; i < pins.Length; i++)
        {
            double amps = Math.Round(Math.Max(0, perPin * share[i] + (_random.NextDouble() - 0.5) * 0.12) / 0.02) * 0.02;
            double volts = Math.Round((12.02 - amps * 0.018 - (_random.NextDouble() < 0.3 ? 0.008 : 0)) / 0.008) * 0.008;
            pins[i] = new PinReading(volts, amps);
        }

        var frame = new PinFrame(_time, pins);
        return StatusMessage.From("ROG Astral RTX 5090 OC", frame, _engine.Evaluate(frame), false, true, _time);
    }
}
