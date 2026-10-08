using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PinSentinel.Service;

/// <summary>
/// Settings that can change while the service runs. The armed state set from the tray
/// is kept in state.json in the data directory and overrides DryRun from appsettings.json.
/// </summary>
public sealed class GuardState
{
    private sealed record Persisted(bool DryRun);

    private readonly string _path;
    private readonly ILogger<GuardState> _logger;
    private volatile bool _dryRun;

    public GuardState(IOptions<ServiceOptions> options, ILogger<GuardState> logger)
    {
        _logger = logger;
        _path = Path.Combine(options.Value.ResolveDataDirectory(), "state.json");
        _dryRun = options.Value.DryRun;
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_path)) is { } saved)
                _dryRun = saved.DryRun;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read {Path}, using DryRun={DryRun}", _path, _dryRun);
        }
    }

    public bool DryRun => _dryRun;

    public void SetDryRun(bool dryRun)
    {
        _dryRun = dryRun;
        _logger.LogWarning("Guard {State}", dryRun ? "disarmed (dry run)" : "armed");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new Persisted(dryRun)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save {Path}; the change lasts until the service restarts", _path);
        }
    }
}
