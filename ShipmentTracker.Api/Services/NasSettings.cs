using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ShipmentTracker.Api.Options;

namespace ShipmentTracker.Api.Services;

/// <summary>
/// The NAS settings the whole API uses: what is written in appsettings.json, with the changes saved from the
/// dashboard's Settings page on top (root path + the list of applications / folders).
/// Those changes are kept in <c>data/settings.json</c> (next to the API), so paths can be edited or added after deployment
/// without touching the code or appsettings.json. Passwords are never stored there.
/// </summary>
public sealed class NasSettings : IOptionsMonitor<NasOptions>
{
    private sealed record Saved(string? Path, List<ApplicationOptions>? Applications);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly IConfiguration _config;
    private readonly string _file;
    private readonly object _gate = new();
    private NasOptions? _current;

    public NasSettings(IConfiguration config, IHostEnvironment env)
    {
        _config = config;
        var f = config["Settings:File"];
        if (string.IsNullOrWhiteSpace(f)) f = Path.Combine("data", "settings.json");
        _file = Path.IsPathRooted(f) ? f : Path.Combine(env.ContentRootPath, f);
        ChangeToken.OnChange(() => config.GetReloadToken(), () => { lock (_gate) _current = null; });
    }

    /// <summary>Raised after the settings were saved or reset (the repositories forget what they cached).</summary>
    public event Action? Changed;

    public NasOptions CurrentValue
    {
        get { lock (_gate) return _current ??= Build(); }
    }

    public NasOptions Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<NasOptions, string?> listener) => null;

    /// <summary>True when the Settings page changed something compared to appsettings.json.</summary>
    public bool Customised => File.Exists(_file);

    /// <summary>The settings exactly as appsettings.json / environment variables say (without the saved changes).</summary>
    public NasOptions Defaults() => _config.GetSection(NasOptions.SectionName).Get<NasOptions>() ?? new NasOptions();

    private NasOptions Build()
    {
        var o = Defaults();
        var saved = Load();
        if (saved is null) return o;
        if (!string.IsNullOrWhiteSpace(saved.Path)) o.Path = saved.Path.Trim();
        if (saved.Applications is { Count: > 0 }) o.Applications = saved.Applications.ToArray();
        return o;
    }

    private Saved? Load()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(_file), Json) : null;
        }
        catch
        {
            return null;   // a damaged file must never stop the API: appsettings.json is used instead
        }
    }

    public void Save(string root, IEnumerable<ApplicationOptions> apps)
    {
        var dir = Path.GetDirectoryName(_file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Saved(root.Trim(), apps.ToList()), Json));
        File.Move(tmp, _file, overwrite: true);
        lock (_gate) _current = null;
        Changed?.Invoke();
    }

    /// <summary>Forget the saved changes: appsettings.json is used again.</summary>
    public void Reset()
    {
        if (File.Exists(_file)) File.Delete(_file);
        lock (_gate) _current = null;
        Changed?.Invoke();
    }
}
