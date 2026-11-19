using System.Diagnostics;
using System.Globalization;
using ShipmentTracker.Api.Models;
using ShipmentTracker.Api.Options;
using ShipmentTracker.Api.Services;

namespace ShipmentTracker.Api.Endpoints;

/// <summary>The Settings page: see, edit and add the NAS folders of the applications without touching the code.</summary>
public static class SettingsEndpoints
{
    public sealed record AppRow(string? Id, string? Name, string? Folder);
    public sealed record SaveRequest(string? Root, List<AppRow>? Applications);

    private static readonly char[] BadChars = ['<', '>', '"', '|', '?', '*'];

    public static void MapSettingsEndpoints(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/settings").RequireAuthorization(p => p.RequireRole("Admin"));

        g.MapGet("", (NasSettings settings) => Results.Ok(Describe(settings)));

        g.MapPut("", (SaveRequest req, NasSettings settings) =>
        {
            var root = (req.Root ?? "").Trim();
            var rows = req.Applications ?? [];
            var error = Validate(root, rows);
            if (error is not null) return Results.Json(new { message = error }, statusCode: StatusCodes.Status400BadRequest);

            // keep the ids that exist, make a new unique id for new rows
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var apps = new List<ApplicationOptions>();
            for (var i = 0; i < rows.Count; i++)
            {
                var name = rows[i].Name!.Trim();
                var id = (rows[i].Id ?? "").Trim();
                if (id.Length == 0) id = NasOptions.Slug(name, i);
                var unique = id;
                for (var n = 2; !used.Add(unique); n++) unique = $"{id}-{n}";
                apps.Add(new ApplicationOptions { Id = unique, Name = name, Folder = rows[i].Folder!.Trim() });
            }

            settings.Save(root, apps);
            return Results.Ok(Describe(settings));
        });

        // back to what appsettings.json says
        g.MapDelete("", (NasSettings settings) =>
        {
            settings.Reset();
            return Results.Ok(Describe(settings));
        });

        // Looks at the folder of ONE application (already saved) and tells what the API can see there.
        g.MapPost("/check", async (ShipmentRepositories repos, string? app, CancellationToken ct) =>
        {
            var repo = repos.Get(app);
            if (repo is null) return Results.Json(new { message = $"Unknown application '{app}'. Save the settings first." }, statusCode: StatusCodes.Status404NotFound);

            var sw = Stopwatch.StartNew();
            var path = repo.GetStatus().Folder;
            try
            {
                var (cat, warning) = await repo.GetCatalogAsync(true, ct);
                var files = cat.Files;
                var withDate = files.Count(f => f.Date is not null);
                var withPart = files.Count(f => !string.IsNullOrWhiteSpace(f.Part));
                var hints = new List<string>();
                if (warning is not null) hints.Add("The NAS could not be reached now, so this is the last saved list: " + warning);
                if (files.Count == 0) hints.Add("No .xlsx / .xlsm / .csv file found. Check the folder name and Nas:MaxDepth.");
                else
                {
                    if (withDate < files.Count * 0.5)
                        hints.Add("Most files have no date in their folder or file name. The date filter then uses the day the file was saved.");
                    if (withPart < files.Count * 0.5)
                        hints.Add("The part number is not visible in the folder names. The part filter then has to open the files, which is slower.");
                    if (files.Count > repo.MaxFilesPerLoad)
                        hints.Add($"{files.Count} files in total: choose a part number or a short date range, a single load reads at most {repo.MaxFilesPerLoad} files.");
                }

                return Results.Ok(new
                {
                    appId = repo.AppId,
                    path,
                    reachable = warning is null,
                    error = warning,
                    files = files.Count,
                    withDate,
                    withPart,
                    parts = cat.Parts.Count,
                    minDate = cat.MinDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    maxDate = cat.MaxDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    sample = files.OrderByDescending(f => f.ModifiedTicks).Take(6).Select(f => f.RelativePath).ToList(),
                    hints,
                    ms = sw.ElapsedMilliseconds
                });
            }
            catch (NasUnavailableException ex)
            {
                return Results.Ok(new { appId = repo.AppId, path, reachable = false, error = ex.Message, files = 0, withDate = 0, withPart = 0, parts = 0,
                    minDate = (string?)null, maxDate = (string?)null, sample = new List<string>(), hints = new List<string>(), ms = sw.ElapsedMilliseconds });
            }
        });
    }

    private static object Describe(NasSettings settings)
    {
        var o = settings.CurrentValue;
        return new
        {
            root = o.Path ?? "",
            customised = settings.Customised,
            applications = o.EffectiveApplications().Select(a => new { id = a.Id, name = a.Name, folder = string.IsNullOrWhiteSpace(a.Folder) ? a.Path : a.Folder, fullPath = o.ResolveFolder(a) })
        };
    }

    private static string? Validate(string root, List<AppRow> rows)
    {
        if (rows.Count == 0) return "Add at least one application.";
        if (rows.Count > 60) return "At most 60 applications.";
        if (root.IndexOfAny(BadChars) >= 0) return "The root path has characters that a folder name cannot have.";

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++)
        {
            var n = i + 1;
            var name = (rows[i].Name ?? "").Trim();
            var folder = (rows[i].Folder ?? "").Trim();
            if (name.Length == 0) return $"Row {n}: enter a name.";
            if (name.Length > 60) return $"Row {n}: the name is too long (60 characters at most).";
            if (!names.Add(name)) return $"Two applications are called '{name}'. Names must be different.";
            if (folder.Length == 0) return $"Row {n} ({name}): enter the folder.";
            if (folder.IndexOfAny(BadChars) >= 0) return $"Row {n} ({name}): the folder has characters that a folder name cannot have.";
            var full = folder.StartsWith(@"\\") || folder.StartsWith("//") || folder.Contains(':') || folder.StartsWith('/');
            if (!full && root.Length == 0) return $"Row {n} ({name}): the folder is relative, so the root path above must be filled in.";
        }
        return null;
    }
}
