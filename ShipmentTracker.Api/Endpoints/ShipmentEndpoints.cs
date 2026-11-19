using Microsoft.Extensions.Options;
using System.Globalization;
using ShipmentTracker.Api.Models;
using ShipmentTracker.Api.Options;
using ShipmentTracker.Api.Services;

namespace ShipmentTracker.Api.Endpoints;

public static class ShipmentEndpoints
{
    /// <summary>Loading ALL data (lakhs of rows) does not answer in time, so a load needs a date range of limited length.</summary>
    private static string? RangeProblem(NasOptions o, DateOnly? from, DateOnly? to)
    {
        if (o.AllowAllData) return null;
        if (from is null || to is null) return "Choose a From and To date. Loading all the data of an application is switched off (Nas:AllowAllData), because it can be lakhs of rows.";
        if (to < from) return "The To date is before the From date.";
        var days = to.Value.DayNumber - from.Value.DayNumber + 1;
        if (o.MaxRangeDays > 0 && days > o.MaxRangeDays)
            return $"Choose at most {o.MaxRangeDays} days at a time (you chose {days}). The limit is Nas:MaxRangeDays.";
        return null;
    }

    public static void MapShipmentEndpoints(this IEndpointRouteBuilder api)
    {
        // Open endpoint for monitoring: is the API up, and can it see the NAS?
        api.MapGet("/health", (ShipmentRepositories repos) =>
        {
            var apps = repos.Loaded().Select(r => r.GetStatus()).ToList();
            return Results.Ok(new
            {
                status = "ok",
                nasConnected = repos.RootChecked && repos.RootError is null && apps.All(a => a.LastError is null),
                lastScanUtc = apps.Max(a => a.LastScanUtc),
                lastError = repos.RootError ?? apps.Select(a => a.LastError).FirstOrDefault(e => e is not null),
                applications = apps.Select(a => new { id = a.AppId, connected = a.Connected, files = a.FileCount, lastError = a.LastError })
            });
        }).AllowAnonymous();

        // The drop-down: which applications exist.
        api.MapGet("/apps", (ShipmentRepositories repos) => Results.Ok(repos.Apps())).RequireAuthorization();

        // What exists in one application's folder (parts, first and last date). Lists folders only: no Excel file is read.
        // ?app=diagcam   ?refresh=true  lists the NAS again instead of using the cached listing
        api.MapGet("/catalog", async (ShipmentRepositories repos, IOptionsMonitor<NasOptions> nas, string? app, bool? refresh, CancellationToken ct) =>
        {
            var repo = repos.Get(app);
            if (repo is null) return Results.Json(new { message = $"Unknown application '{app}'." }, statusCode: StatusCodes.Status404NotFound);
            try
            {
                var (cat, warning) = await repo.GetCatalogAsync(refresh == true, ct);
                var status = repo.GetStatus();
                return Results.Ok(new CatalogResponse(
                    repo.AppId, repos.NameOf(repo.AppId), status.Folder, cat.Files.Count, cat.Parts,
                    cat.MinDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    cat.MaxDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    cat.BuiltAtUtc, warning, nas.CurrentValue.AllowAllData ? 0 : nas.CurrentValue.MaxRangeDays));
            }
            catch (NasUnavailableException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization();

        // STEP A of "Load data": only the numbers (total / pass / fail / other, per day, top failing parts). Small and quick to send.
        api.MapGet("/summary", async (ShipmentRepositories repos, IOptionsMonitor<NasOptions> nas, string? app, string? part, string? from, string? to, bool? refresh, CancellationToken ct) =>
        {
            var repo = repos.Get(app);
            if (repo is null) return Results.Json(new { message = $"Unknown application '{app}'." }, statusCode: StatusCodes.Status404NotFound);

            static DateOnly? Day(string? s) =>
                DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            if (!string.IsNullOrWhiteSpace(from) && Day(from) is null || !string.IsNullOrWhiteSpace(to) && Day(to) is null)
                return Results.Json(new { message = "Dates must look like 2026-10-31." }, statusCode: StatusCodes.Status400BadRequest);

            if (RangeProblem(nas.CurrentValue, Day(from), Day(to)) is { } rangeProblem)
                return Results.Json(new { message = rangeProblem }, statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var r = await repo.SummaryAsync(new ShipmentQuery(part, Day(from), Day(to)), refresh == true, ct);
                var status = repo.GetStatus();
                return Results.Ok(new SummaryResponse(
                    repo.AppId, repos.NameOf(repo.AppId), r.Version, DateTime.UtcNow, status.Folder, status.FolderName,
                    r.FileCount, r.SheetCount, r.SkippedSheets, r.Errors, r.Skipped, r.Warning,
                    r.Total, r.Pass, r.Fail, r.Other, r.PartCount, r.Days, r.TopFails));
            }
            catch (TooManyFilesException ex)
            {
                return Results.Json(new { message = ex.Message, files = ex.Count, limit = ex.Max }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            catch (NasUnavailableException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization();

        var group = api.MapGroup("/shipments").RequireAuthorization();

        // The rows for ONE application + part number + date range. Only the files that can match are read.
        // ?app=diagcam  &part=NeduCAM25  &from=2026-10-01  &to=2026-10-31  &refresh=true
        // If nothing changed since the caller's last request, answers 304 (no body).
        group.MapGet("", async (HttpContext http, ShipmentRepositories repos, IOptionsMonitor<NasOptions> nas, string? app, string? part, string? from, string? to, bool? refresh, CancellationToken ct) =>
        {
            var repo = repos.Get(app);
            if (repo is null) return Results.Json(new { message = $"Unknown application '{app}'." }, statusCode: StatusCodes.Status404NotFound);

            static DateOnly? Day(string? s) =>
                DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            if (!string.IsNullOrWhiteSpace(from) && Day(from) is null || !string.IsNullOrWhiteSpace(to) && Day(to) is null)
                return Results.Json(new { message = "Dates must look like 2026-10-31." }, statusCode: StatusCodes.Status400BadRequest);

            if (RangeProblem(nas.CurrentValue, Day(from), Day(to)) is { } rangeProblem)
                return Results.Json(new { message = rangeProblem }, statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var result = await repo.QueryAsync(new ShipmentQuery(part, Day(from), Day(to)), refresh == true, ct);

                var etag = $"\"{result.Version}\"";
                http.Response.Headers.ETag = etag;
                http.Response.Headers.CacheControl = "no-cache";
                if (http.Request.Headers.IfNoneMatch.Any(v => v is not null && v.Replace("W/", "").Trim() == etag))
                    return Results.StatusCode(StatusCodes.Status304NotModified);

                var status = repo.GetStatus();
                return Results.Ok(new ShipmentsResponse(
                    repo.AppId, repos.NameOf(repo.AppId), result.Version, DateTime.UtcNow, status.Folder, status.FolderName,
                    result.FileCount, result.SheetCount, result.SkippedSheets, result.Errors, result.Skipped, result.Warning, result.Records,
                    result.TotalRows, result.Truncated));
            }
            catch (TooManyFilesException ex)
            {
                return Results.Json(new { message = ex.Message, files = ex.Count, limit = ex.Max }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            catch (NasUnavailableException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/status", (ShipmentRepositories repos, string? app) =>
            repos.Get(app) is { } r ? Results.Ok(r.GetStatus()) : Results.NotFound(new { message = $"Unknown application '{app}'." }));
    }
}
