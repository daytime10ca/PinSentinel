using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinSentinel.Core;

/// <summary>One line of JSON per sample, sent by the service to any connected UI.</summary>
public sealed record StatusMessage
{
    public const string PipeName = "PinSentinel.Status";

    public DateTimeOffset Time { get; init; }
    public string Card { get; init; } = "";
    public bool SensorOk { get; init; }
    public double[] Volts { get; init; } = [];
    public double[] Amps { get; init; } = [];
    public Severity Severity { get; init; }
    public Finding[] Findings { get; init; } = [];
    public bool Throttled { get; init; }
    public bool DryRun { get; init; }

    private static readonly JsonSerializerOptions s_json = new() { Converters = { new JsonStringEnumConverter() } };

    public static StatusMessage From(string card, PinFrame? frame, Evaluation eval, bool throttled, bool dryRun, DateTimeOffset now) => new()
    {
        Time = now,
        Card = card,
        SensorOk = frame is not null,
        Volts = frame?.Pins.Select(p => p.Volts).ToArray() ?? [],
        Amps = frame?.Pins.Select(p => p.Amps).ToArray() ?? [],
        Severity = eval.Severity,
        Findings = [.. eval.Findings],
        Throttled = throttled,
        DryRun = dryRun,
    };

    public string ToJson() => JsonSerializer.Serialize(this, s_json);

    public static StatusMessage? FromJson(string line)
    {
        try { return JsonSerializer.Deserialize<StatusMessage>(line, s_json); }
        catch (JsonException) { return null; }
    }
}
