namespace ShipmentTracker.Api.Options;

/// <summary>Where the Excel reports live and how to log in to that server ("Nas" section of appsettings).</summary>
public sealed class NasOptions
{
    public const string SectionName = "Nas";

    /// <summary>
    /// ROOT folder that contains all application folders, e.g.
    /// \\mfgshare.e-consystems.net\Production\...\Temp e-con Systems Daily Test Report - Server
    /// The API also logs in to the share of this path.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>The applications of the dashboard's drop-down. Each one has a folder (relative to the root) with its Excel reports.</summary>
    public ApplicationOptions[] Applications { get; set; } = [];

    /// <summary>Account that can read the share. Use DOMAIN\user, user@domain or just user (+ Domain). Empty = use the Windows account the API runs as.</summary>
    public string? Username { get; set; }

    /// <summary>Password of that account. Keep it OUT of appsettings.json: use an environment variable or user-secrets.</summary>
    public string? Password { get; set; }

    /// <summary>Optional domain / NAS host name, used when Username has no domain part.</summary>
    public string? Domain { get; set; }

    public bool Recursive { get; set; } = true;
    public int MaxDepth { get; set; } = 8;
    public string[] Extensions { get; set; } = [".xlsx", ".xlsm", ".csv"];

    /// <summary>The folder listing of an application ("catalog": parts, dates, files) is reused for this many seconds.</summary>
    public int CatalogCacheSeconds { get; set; } = 120;

    /// <summary>How many folders are listed on the NAS at the same time.</summary>
    public int ListingParallelism { get; set; } = 8;

    /// <summary>How many Excel files are read at the same time.</summary>
    public int MaxParallelFiles { get; set; } = 6;

    /// <summary>A single "Load data" may read at most this many files. Above it the user is asked to narrow the part / dates.</summary>
    public int MaxFilesPerLoad { get; set; } = 800;

    /// <summary>Folder (on the API server) where read reports are kept as compressed files, so they are never read twice. Relative = next to the API.</summary>
    public string CacheDirectory { get; set; } = "cache";

    /// <summary>
    /// false (default): "Load data" always needs a From and To date, and at most <see cref="MaxRangeDays"/> days at a time.
    /// Loading ALL data of an application (lakhs of rows) would not answer in time. Set true only for small folders.
    /// </summary>
    public bool AllowAllData { get; set; } = false;

    /// <summary>The longest date range of one load, in days (used while AllowAllData is false).</summary>
    public int MaxRangeDays { get; set; } = 31;

    /// <summary>The table gets at most this many rows (the numbers on top always count everything selected). 0 = no limit.</summary>
    public int MaxTableRows { get; set; } = 50000;

    /// <summary>The reports of the last N days are read in the background so "Load data" is instant. 0 = off.</summary>
    public int WarmDays { get; set; } = 14;

    /// <summary>How often (minutes) the background reader looks for new or changed reports.</summary>
    public int WarmIntervalMinutes { get; set; } = 5;

    /// <summary>Stop waiting for an unreachable server after this many seconds.</summary>
    public int NetworkTimeoutSeconds { get; set; } = 20;

    /// <summary>The applications to offer: the configured list, or one default entry for the root folder.</summary>
    public IReadOnlyList<ApplicationOptions> EffectiveApplications() =>
        Applications.Where(a => !string.IsNullOrWhiteSpace(a.Folder) || !string.IsNullOrWhiteSpace(a.Path)).ToList() is { Count: > 0 } list
            ? list.Select((a, i) => a with { Id = string.IsNullOrWhiteSpace(a.Id) ? Slug(a.Name, i) : a.Id.Trim() }).ToList()
            : [new ApplicationOptions { Id = "default", Name = "NAS", Folder = "" }];

    /// <summary>Full path of an application's folder: a relative Folder is added to the root, a full \\server\... path is used as it is.</summary>
    public string ResolveFolder(ApplicationOptions app)
    {
        var f = (!string.IsNullOrWhiteSpace(app.Folder) ? app.Folder : app.Path).Trim();
        var root = (Path ?? "").Trim();
        if (f.Length == 0) return root;
        if (f.StartsWith(@"\\") || f.StartsWith("//") || f.Contains(':') || f.StartsWith('/')) return f;   // already a full path
        return System.IO.Path.Combine(root.TrimEnd('\\', '/'), f.TrimStart('\\', '/'));
    }

    /// <summary>A copy of these settings that reads another folder.</summary>
    public NasOptions WithPath(string path)
    {
        var copy = (NasOptions)MemberwiseClone();
        copy.Path = path;
        return copy;
    }

    public static string Slug(string? name, int index)
    {
        var s = new string((name ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return s.Length > 0 ? s : $"app{index + 1}";
    }
}

/// <summary>One entry of the application drop-down.</summary>
public sealed record ApplicationOptions
{
    /// <summary>Short id used in the URL (e.g. "diagcam"). Optional: made from the name when empty.</summary>
    public string Id { get; init; } = "";

    /// <summary>Name shown in the drop-down.</summary>
    public string Name { get; init; } = "";

    /// <summary>Folder of this application, relative to Nas:Path (e.g. DiagCAM\DiagCAM). A full \\server\share\... path also works.</summary>
    public string Folder { get; init; } = "";

    /// <summary>Old name of Folder (still accepted).</summary>
    public string Path { get; init; } = "";
}
