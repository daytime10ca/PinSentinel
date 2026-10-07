using System.Globalization;

namespace PinSentinel.Core;

/// <summary>Appends frames to one CSV per day and prunes old files.</summary>
public sealed class CsvLog(string directory, int retentionDays = 30) : IDisposable
{
    public const string Header = "time,v1,v2,v3,v4,v5,v6,a1,a2,a3,a4,a5,a6,severity,rules";

    private StreamWriter? _writer;
    private DateOnly _day;

    public static string Format(PinFrame frame, Evaluation eval)
    {
        var inv = CultureInfo.InvariantCulture;
        return string.Join(',',
            [frame.Timestamp.ToString("O", inv),
             .. frame.Pins.Select(p => p.Volts.ToString("F3", inv)),
             .. frame.Pins.Select(p => p.Amps.ToString("F3", inv)),
             eval.Severity.ToString(),
             string.Join(';', eval.Findings.Select(f => f.RuleId))]);
    }

    public void Write(PinFrame frame, Evaluation eval)
    {
        var day = DateOnly.FromDateTime(frame.Timestamp.LocalDateTime);
        if (_writer is null || day != _day)
        {
            _writer?.Dispose();
            Directory.CreateDirectory(directory);
            Prune(day);
            string path = Path.Combine(directory, $"pins-{day:yyyy-MM-dd}.csv");
            bool isNew = !File.Exists(path);
            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
            _day = day;
            if (isNew) _writer.WriteLine(Header);
        }
        _writer.WriteLine(Format(frame, eval));
    }

    private void Prune(DateOnly today)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "pins-*.csv"))
        {
            string stamp = Path.GetFileNameWithoutExtension(file)["pins-".Length..];
            if (DateOnly.TryParseExact(stamp, "yyyy-MM-dd", out var day) && day < today.AddDays(-retentionDays))
                File.Delete(file);
        }
    }

    public void Dispose() => _writer?.Dispose();
}
