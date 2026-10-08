using System.IO;
using System.IO.Pipes;
using PinSentinel.Core;

namespace PinSentinel.Tray;

/// <summary>Sends a one-line command to the service and returns its reply, or null if it could not be reached.</summary>
static class ControlClient
{
    public static async Task<string?> Send(string command)
    {
        try
        {
            using var timeout = new CancellationTokenSource(4000);
            await using var pipe = new NamedPipeClientStream(".", StatusMessage.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2000, timeout.Token);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await writer.WriteLineAsync(command.AsMemory(), timeout.Token);
            return await reader.ReadLineAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Builds the health report from the service's daily CSV logs. Finished days are analysed once.</summary>
static class HealthLoader
{
    private static readonly Dictionary<string, DayHealth?> s_finishedDays = [];

    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PinSentinel", "logs");

    public static Task<HealthReport> Load() => Task.Run(() =>
    {
        var days = new List<DayHealth>();
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (Directory.Exists(LogDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(LogDirectory, "pins-*.csv"))
            {
                string stamp = Path.GetFileNameWithoutExtension(file)["pins-".Length..];
                if (!DateOnly.TryParseExact(stamp, "yyyy-MM-dd", out var day)) continue;
                try
                {
                    DayHealth? health;
                    lock (s_finishedDays)
                    {
                        if (day >= today || !s_finishedDays.TryGetValue(file, out health))
                        {
                            health = HealthAnalyzer.AnalyzeDay(day, HealthAnalyzer.ReadCsv(file));
                            if (day < today) s_finishedDays[file] = health;
                        }
                    }
                    if (health is not null) days.Add(health);
                }
                catch (IOException) { }
            }
        }
        return HealthAnalyzer.Report(days);
    });
}
