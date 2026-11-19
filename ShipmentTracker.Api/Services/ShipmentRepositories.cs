using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ShipmentTracker.Api.Models;
using ShipmentTracker.Api.Options;
using ShipmentTracker.Api.Services.Nas;

namespace ShipmentTracker.Api.Services;

/// <summary>One <see cref="ShipmentRepository"/> per application of the drop-down (DiagCAM, Calibration, ...), created on first use.</summary>
public sealed class ShipmentRepositories(
    IOptionsMonitor<NasOptions> options,
    INasConnector nas,
    ILoggerFactory loggerFactory,
    IHostEnvironment env,
    NasSettings settings)
{
    private readonly ConcurrentDictionary<string, ShipmentRepository> _repos = new(StringComparer.OrdinalIgnoreCase);
    private bool _hooked;

    /// <summary>After the Settings page changed a folder, forget the listings of the old folders.</summary>
    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        settings.Changed += () => _repos.Clear();
    }

    /// <summary>Result of the start-up check of the root folder (null = fine or not checked yet).</summary>
    public string? RootError { get; set; }
    public bool RootChecked { get; set; }

    public IReadOnlyList<AppInfo> Apps() =>
        options.CurrentValue.EffectiveApplications()
            .Select(a => new AppInfo(a.Id, string.IsNullOrWhiteSpace(a.Name) ? a.Id : a.Name))
            .ToList();

    /// <summary>The repository for an application id (null / empty = the first application). Null when the id is unknown.</summary>
    public ShipmentRepository? Get(string? id)
    {
        Hook();
        var apps = options.CurrentValue.EffectiveApplications();
        var app = string.IsNullOrWhiteSpace(id)
            ? apps.FirstOrDefault()
            : apps.FirstOrDefault(a => a.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (app is null) return null;
        return _repos.GetOrAdd(app.Id, key => new ShipmentRepository(key, options, nas, loggerFactory.CreateLogger("ShipmentRepository"), env.ContentRootPath));
    }

    public string NameOf(string id) => Apps().FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name ?? id;

    public IEnumerable<ShipmentRepository> Loaded() => _repos.Values;
}
