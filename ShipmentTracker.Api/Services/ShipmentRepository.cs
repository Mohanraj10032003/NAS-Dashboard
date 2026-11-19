using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ShipmentTracker.Api.Models;
using ShipmentTracker.Api.Options;
using ShipmentTracker.Api.Services.Excel;
using ShipmentTracker.Api.Services.Nas;

namespace ShipmentTracker.Api.Services;

/// <summary>One file on the NAS, with what its PATH tells about it (part number, date).</summary>
public sealed record CatalogFile(string FullPath, string RelativePath, long ModifiedTicks, long Length, DateOnly? Date, string? Part);

/// <summary>The folder listing of one application: which files exist, which parts, which dates. No Excel file is opened for it.</summary>
public sealed record Catalog(DateTime BuiltAtUtc, IReadOnlyList<CatalogFile> Files, IReadOnlyList<string> Parts, DateOnly? MinDate, DateOnly? MaxDate);

public sealed record ShipmentQuery(string? Part, DateOnly? From, DateOnly? To);

public sealed record SummaryResult(
    string Version, int FileCount, int SheetCount, int SkippedSheets,
    IReadOnlyList<string> Errors, IReadOnlyList<string> Skipped, string? Warning,
    int Total, int Pass, int Fail, int Other, int PartCount,
    IReadOnlyList<DayCount> Days, IReadOnlyList<PartFails> TopFails);

public sealed record QueryResult(
    string Version, int FileCount, int SheetCount, int SkippedSheets,
    IReadOnlyList<string> Errors, IReadOnlyList<string> Skipped, string? Warning, IReadOnlyList<ShipmentRecordDto> Records,
    int TotalRows, bool Truncated);

/// <summary>
/// ONE application folder of the NAS. Two steps, so the dashboard stays fast:
///   1. Catalog  - list the folders once (cached for a few minutes) -> fills the Part number / date drop-downs.
///   2. Query    - when the user presses "Load data": pick only the files that match the part + dates (decided from the
///                 folder names), read just those (re-reading only files that changed) and return their rows.
/// </summary>
public sealed class ShipmentRepository(
    string appId,
    IOptionsMonitor<NasOptions> optionsMonitor,
    INasConnector nas,
    ILogger logger,
    string contentRoot)
{
    private sealed record CachedFile(long ModifiedTicks, long Length, ParsedFile Parsed);

    private readonly ConcurrentDictionary<string, CachedFile> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);   // one reader per file
    // files that cannot be read (damaged, cut off, still being saved): remembered with their date + size, so they are not tried again and again
    private readonly ConcurrentDictionary<string, (long Ticks, long Length, string Why)> _unreadable = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private DateTime _retryListingAfterUtc = DateTime.MinValue;
    private DiskCache? _disk;
    private string? _diskFolder;
    private Catalog? _catalog;
    private string? _lastError;

    public string AppId { get; } = appId;
    public int MaxFilesPerLoad => Options.MaxFilesPerLoad;

    /// <summary>The on-disk copy of everything read so far (null when switched off with an empty CacheDirectory).</summary>
    private DiskCache? Disk
    {
        get
        {
            var dir = Options.CacheDirectory;
            if (string.IsNullOrWhiteSpace(dir)) return null;
            var full = Path.Combine(Path.IsPathRooted(dir) ? dir : Path.Combine(contentRoot, dir), AppId);
            if (_disk is null || _diskFolder != full) { _disk = new DiskCache(full, logger); _diskFolder = full; }
            return _disk;
        }
    }

    /// <summary>The shared settings, pointed at THIS application's folder (read fresh, so config edits apply).</summary>
    private NasOptions Options
    {
        get
        {
            var all = optionsMonitor.CurrentValue;
            var app = all.EffectiveApplications().FirstOrDefault(a => a.Id.Equals(AppId, StringComparison.OrdinalIgnoreCase));
            return all.WithPath(app is null ? all.Path : all.ResolveFolder(app));
        }
    }

    public NasStatus GetStatus()
    {
        var path = Options.Path ?? "";
        return new NasStatus(
            AppId: AppId,
            Connected: _catalog is not null && _lastError is null,
            Folder: path,
            FolderName: FolderName(path),
            LastScanUtc: _catalog?.BuiltAtUtc,
            LastError: _lastError,
            FileCount: _catalog?.Files.Count ?? 0,
            RecordCount: _cache.Values.Sum(c => c.Parsed.Sheets.Sum(s => s.Rows.Count)),
            Scanning: _catalogLock.CurrentCount == 0);
    }

    // ------------------------------------------------------------------ 1. catalog

    /// <summary>The folder listing. Reused while it is fresh; <paramref name="force"/> lists the NAS again.</summary>
    public async Task<(Catalog Catalog, string? Warning)> GetCatalogAsync(bool force, CancellationToken ct)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(0, Options.CatalogCacheSeconds));
        if (!force && _catalog is { } fresh && (DateTime.UtcNow - fresh.BuiltAtUtc < ttl || (_lastError is not null && DateTime.UtcNow < _retryListingAfterUtc)))
            return (fresh, _lastError);

        await _catalogLock.WaitAsync(ct);
        try
        {
            // somebody else may have refreshed it while we waited
            if (!force && _catalog is { } again && (DateTime.UtcNow - again.BuiltAtUtc < ttl || (_lastError is not null && DateTime.UtcNow < _retryListingAfterUtc)))
                return (again, _lastError);

            try
            {
                var opt = Options;
                await nas.EnsureConnectedAsync(opt, ct);
                var files = await Task.Run(() => ListFiles(opt, ct), ct);
                _catalog = BuildCatalog(files);
                _lastError = null;
                Disk?.SaveCatalog(opt.Path ?? "", _catalog.BuiltAtUtc, files);   // so the list of reports is also known when the NAS is not reachable

                // forget cached files that no longer exist
                var alive = files.Select(f => f.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var key in _cache.Keys.Where(k => !alive.Contains(k)).ToList()) _cache.TryRemove(key, out _);
                Disk?.Prune(alive);

                logger.LogInformation("Catalog [{App}]: {Files} file(s), {Parts} part(s)", AppId, files.Count, _catalog.Parts.Count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _retryListingAfterUtc = DateTime.UtcNow.AddSeconds(60);   // do not wait for the NAS timeout on every click
                logger.LogWarning("NAS listing failed ({App}): {Message}", AppId, ex.Message);
                if (_catalog is null)
                {
                    // NAS not reachable: use the list saved on disk, so the reports that were read earlier can still be shown
                    if (Disk?.TryLoadCatalog(Options.Path ?? "") is { } saved)
                    {
                        _catalog = BuildCatalog(saved.Files);
                        _catalog = _catalog with { BuiltAtUtc = saved.BuiltAtUtc };
                        logger.LogInformation("Catalog [{App}]: using the copy saved on {Date:u} ({Files} files)", AppId, saved.BuiltAtUtc, saved.Files.Count);
                    }
                    else throw ex as NasUnavailableException ?? new NasUnavailableException(ex.Message, ex);
                }
            }
            return (_catalog!, _lastError);
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    private static Catalog BuildCatalog(List<CatalogFile> files)
    {
        var parts = files.Select(f => f.Part).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var dates = files.Where(f => f.Date is not null).Select(f => f.Date!.Value).ToList();
        // files without a date in their path: use the day they were saved
        var fallback = files.Where(f => f.Date is null).Select(f => DateOnly.FromDateTime(new DateTime(f.ModifiedTicks, DateTimeKind.Utc).ToLocalTime()));
        var all = dates.Concat(fallback).ToList();
        return new Catalog(DateTime.UtcNow, files, parts, all.Count > 0 ? all.Min() : null, all.Count > 0 ? all.Max() : null);
    }

    // ------------------------------------------------------------------ 2. query

    private sealed record Prepared(List<CatalogFile> Selected, List<string> Errors, List<string> Skipped, string? Warning, int Changed);

    /// <summary>Picks the files that can match the query and makes sure each one is read (re-reading only changed files).</summary>
    private async Task<Prepared> PrepareAsync(ShipmentQuery q, bool force, CancellationToken ct)
    {
        var (catalog, warning) = await GetCatalogAsync(force, ct);
        var opt = Options;

        var selected = catalog.Files.Where(f => Matches(f, q)).OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        if (selected.Count > opt.MaxFilesPerLoad) throw new TooManyFilesException(selected.Count, opt.MaxFilesPerLoad);

        var errors = new ConcurrentBag<string>();
        var skipped = new ConcurrentBag<string>();
        var changed = 0;

        await Parallel.ForEachAsync(
            selected,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, opt.MaxParallelFiles), CancellationToken = ct },
            async (file, token) =>
            {
                bool Fresh() => _cache.TryGetValue(file.FullPath, out var hit) && hit.ModifiedTicks == file.ModifiedTicks && hit.Length == file.Length;
                if (Fresh()) return;   // unchanged since the last read
                if (_unreadable.TryGetValue(file.FullPath, out var bad) && bad.Ticks == file.ModifiedTicks && bad.Length == file.Length)
                {
                    skipped.Add($"{file.RelativePath}: {bad.Why}");
                    return;   // known to be unreadable and it has not changed since
                }

                // one reader per file (a user and the background reader never read the same file twice)
                var gate = _gates.GetOrAdd(file.FullPath, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(token);
                try
                {
                    if (Fresh()) return;

                    // 1) the copy on this server's disk (read before, same date + size on the NAS)
                    var disk = Disk;
                    if (disk?.TryLoad(file.FullPath, file.ModifiedTicks, file.Length) is { } saved)
                    {
                        _cache[file.FullPath] = new CachedFile(file.ModifiedTicks, file.Length, saved);
                        return;
                    }

                    // 2) the NAS
                    var (parsed, problem) = await ReadFileAsync(file, token);
                    if (parsed is null)
                    {
                        // damaged / cut off / still being saved: leave it out quietly, it is tried again when the file changes
                        _unreadable[file.FullPath] = (file.ModifiedTicks, file.Length, problem!);
                        skipped.Add($"{file.RelativePath}: {problem}");
                        logger.LogDebug("Skipped {File}: {Problem}", file.RelativePath, problem);
                        return;
                    }
                    _unreadable.TryRemove(file.FullPath, out _);
                    _cache[file.FullPath] = new CachedFile(file.ModifiedTicks, file.Length, parsed);
                    Interlocked.Increment(ref changed);
                    if (disk is not null) await Task.Run(() => disk.Save(file.FullPath, file.ModifiedTicks, file.Length, parsed), CancellationToken.None);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _cache.TryRemove(file.FullPath, out _);   // forget it so the next load tries again
                    errors.Add($"{file.RelativePath}: {Friendly(ex)}");
                    logger.LogWarning("Could not read {File}: {Message}", file.RelativePath, ex.Message);
                }
                finally
                {
                    gate.Release();
                }
            });

        return new Prepared(selected, errors.OrderBy(e => e).ToList(), skipped.OrderBy(e => e).ToList(), warning, changed);
    }

    /// <summary>The (cached) rows of one file that match the query, with the sheet they came from.</summary>
    private IEnumerable<(ParsedRow Row, string Source)> RowsOf(CatalogFile f, ShipmentQuery q, Action<bool>? sheetSeen = null)
    {
        if (!_cache.TryGetValue(f.FullPath, out var cached)) yield break;
        foreach (var sheet in cached.Parsed.Sheets)
        {
            sheetSeen?.Invoke(sheet.Skipped);
            if (sheet.Skipped) continue;
            var source = $"{f.RelativePath} › {sheet.Name}";
            foreach (var row in sheet.Rows)
                if (RowMatches(row, f, q)) yield return (row, source);
        }
    }

    private string VersionOf(ShipmentQuery q, Prepared p)
    {
        var sig = new StringBuilder();
        sig.Append(AppId).Append('|').Append(q.Part).Append('|').Append(q.From).Append('|').Append(q.To).Append('|');
        foreach (var f in p.Selected) sig.Append(f.FullPath).Append('|').Append(f.ModifiedTicks).Append('|').Append(f.Length).Append(';');
        foreach (var e in p.Errors) sig.Append(e).Append(';');
        foreach (var e in p.Skipped) sig.Append(e).Append(';');
        // The version only changes when the selected content changes, so the browser can skip unchanged downloads.
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sig.ToString())))[..16].ToLowerInvariant();
    }

    /// <summary>Background job: reads the reports of the last <paramref name="days"/> days that are new or changed.</summary>
    public async Task WarmAsync(int days, CancellationToken ct)
    {
        var from = DateOnly.FromDateTime(DateTime.Now).AddDays(-days);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var p = await PrepareAsync(new ShipmentQuery(null, from, null), true, ct);
            if (p.Changed > 0 || p.Errors.Count > 0)
                logger.LogInformation("Warm-up [{App}]: {Files} recent file(s), {Changed} read from the NAS, {Errors} problem(s), {Ms} ms",
                    AppId, p.Selected.Count, p.Changed, p.Errors.Count, sw.ElapsedMilliseconds);
        }
        catch (TooManyFilesException ex)
        {
            logger.LogWarning("Warm-up [{App}] skipped: {Message}. Lower Nas:WarmDays.", AppId, ex.Message);
        }
    }

    /// <summary>
    /// STEP A of "Load data": reads the matching reports and returns only the NUMBERS (total, pass, fail, other, per-day, top failing parts).
    /// The rows themselves are not sent: that is step B (<see cref="QueryAsync"/>), which finds the files already read.
    /// </summary>
    public async Task<SummaryResult> SummaryAsync(ShipmentQuery q, bool force, CancellationToken ct)
    {
        var p = await PrepareAsync(q, force, ct);

        int pass = 0, fail = 0, other = 0, sheets = 0, skipped = 0;
        var byDay = new SortedDictionary<string, (int Pass, int Fail)>(StringComparer.Ordinal);
        var byPart = new Dictionary<string, (int Fails, int Total)>(StringComparer.Ordinal);
        var parts = new HashSet<string>(StringComparer.Ordinal);

        foreach (var f in p.Selected)
            foreach (var (row, _) in RowsOf(f, q, sk => { sheets++; if (sk) skipped++; }))
            {
                var r = row.Result;
                if (r == "PASS") pass++; else if (r == "FAIL") fail++; else other++;

                if (row.Date is { } d)
                {
                    var key = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var cur = byDay.TryGetValue(key, out var v) ? v : (0, 0);
                    byDay[key] = (cur.Item1 + (r == "PASS" ? 1 : 0), cur.Item2 + (r == "FAIL" ? 1 : 0));
                }
                if (row.Part.Length > 0)
                {
                    parts.Add(row.Part);
                    var cur = byPart.TryGetValue(row.Part, out var v) ? v : (0, 0);
                    byPart[row.Part] = (cur.Item1 + (r == "FAIL" ? 1 : 0), cur.Item2 + 1);
                }
            }

        var days = byDay.TakeLast(14).Select(kv => new DayCount(kv.Key, kv.Value.Pass, kv.Value.Fail)).ToList();
        var top = byPart.Where(kv => kv.Value.Fails > 0)
            .Select(kv => new PartFails(kv.Key, kv.Value.Fails, kv.Value.Total))
            .OrderByDescending(t => t.Fails).ThenByDescending(t => (double)t.Fails / t.Total)
            .Take(5).ToList();

        logger.LogInformation("Summary [{App}] part={Part} {From}..{To}: {Files} file(s), {Changed} read, {Total} row(s)",
            AppId, q.Part ?? "*", q.From, q.To, p.Selected.Count, p.Changed, pass + fail + other);

        return new SummaryResult(VersionOf(q, p), p.Selected.Count, sheets, skipped, p.Errors, p.Skipped, p.Warning,
            pass + fail + other, pass, fail, other, parts.Count, days, top);
    }

    /// <summary>STEP B of "Load data": the table rows (the files are normally already read by step A).</summary>
    public async Task<QueryResult> QueryAsync(ShipmentQuery q, bool force, CancellationToken ct)
    {
        var p = await PrepareAsync(q, force, ct);

        var records = new List<ShipmentRecordDto>();
        var id = 0;
        var sheets = 0;
        var skipped = 0;

        var limit = Options.MaxTableRows > 0 ? Options.MaxTableRows : int.MaxValue;
        var total = 0;
        foreach (var f in p.Selected)
            foreach (var (row, source) in RowsOf(f, q, sk => { sheets++; if (sk) skipped++; }))
            {
                if (++total > limit) continue;   // keep counting, but the table gets only the first rows
                records.Add(new ShipmentRecordDto(
                    ++id,
                    row.Part,
                    row.Date?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    row.Result,
                    row.ResultRaw,
                    source,
                    row.Extra));
            }

        logger.LogInformation("Query [{App}] part={Part} {From}..{To}: {Files} file(s), {Changed} read, {Rows} row(s)",
            AppId, q.Part ?? "*", q.From, q.To, p.Selected.Count, p.Changed, records.Count);

        return new QueryResult(VersionOf(q, p), p.Selected.Count, sheets, skipped, p.Errors, p.Skipped, p.Warning, records, total, total > limit);
    }

    /// <summary>Can this file hold rows for the query? Decided from its path only (no file is opened).</summary>
    private static bool Matches(CatalogFile f, ShipmentQuery q)
    {
        // the part number can be a folder name or part of the file name
        if (!string.IsNullOrWhiteSpace(q.Part) && f.Part is not null
            && !f.Part.Contains(q.Part.Trim(), StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(f.RelativePath.Replace('\\', '/')).Contains(q.Part.Trim(), StringComparison.OrdinalIgnoreCase)) return false;

        // date from the folder / file name, else the day the file was saved (a file is never saved BEFORE its data)
        var day = f.Date ?? DateOnly.FromDateTime(new DateTime(f.ModifiedTicks, DateTimeKind.Utc).ToLocalTime());
        if (q.From is { } from && day < from) return false;
        if (f.Date is not null && q.To is { } to && day > to) return false;
        return true;
    }

    private static bool RowMatches(ParsedRow row, CatalogFile f, ShipmentQuery q)
    {
        // the folder name already chose the part; only check the row itself when the path did not say
        if (!string.IsNullOrWhiteSpace(q.Part) && f.Part is null
            && !row.Part.Contains(q.Part.Trim(), StringComparison.OrdinalIgnoreCase)) return false;

        if (row.Date is { } d)
        {
            var day = DateOnly.FromDateTime(d);
            if (q.From is { } from && day < from) return false;
            if (q.To is { } to && day > to) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ file system

    /// <summary>Lists the application folder level by level, several folders at a time (a NAS answers many small requests slowly one by one).</summary>
    private static List<CatalogFile> ListFiles(NasOptions opt, CancellationToken ct)
    {
        var extensions = new HashSet<string>(opt.Extensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase);
        var root = opt.Path.Trim();
        var found = new ConcurrentBag<CatalogFile>();
        var level = new List<(DirectoryInfo Dir, string Relative, int Depth)> { (new DirectoryInfo(root), "", 0) };

        try
        {
            while (level.Count > 0)
            {
                var next = new ConcurrentBag<(DirectoryInfo, string, int)>();
                Parallel.ForEach(level, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, opt.ListingParallelism), CancellationToken = ct }, item =>
                {
                    List<FileSystemInfo> entries;
                    try { entries = item.Dir.EnumerateFileSystemInfos().ToList(); }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        if (item.Depth == 0) throw new NasUnavailableException($"Cannot read folder '{root}': {ex.Message}", ex);
                        return; // unreadable sub-folder: skip it
                    }

                    var dirs = item.Relative.Length == 0 ? [] : item.Relative.TrimEnd('/').Split('/');
                    foreach (var entry in entries)
                    {
                        if (entry is DirectoryInfo sub)
                        {
                            if (opt.Recursive && item.Depth < opt.MaxDepth && !sub.Name.StartsWith('.') && !sub.Name.StartsWith('$'))
                                next.Add((sub, item.Relative + sub.Name + "/", item.Depth + 1));
                        }
                        else if (entry is FileInfo fi
                                 && extensions.Contains(fi.Extension)
                                 && !fi.Name.StartsWith("~$", StringComparison.Ordinal))   // Excel's temporary lock files
                        {
                            var (date, part) = PathHints.Infer(dirs, fi.Name);
                            found.Add(new CatalogFile(fi.FullName, item.Relative + fi.Name, fi.LastWriteTimeUtc.Ticks, fi.Length, date, part));
                        }
                    }
                });
                level = next.Select(n => (n.Item1, n.Item2, n.Item3)).ToList();
            }
        }
        catch (AggregateException ae) when (ae.Flatten().InnerExceptions.OfType<NasUnavailableException>().FirstOrDefault() is { } nu)
        {
            throw nu;
        }

        return found.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Reads one file. A file that is damaged or still being saved gives (null, reason) instead of an exception.</summary>
    private static async Task<(ParsedFile? Parsed, string? Problem)> ReadFileAsync(CatalogFile file, CancellationToken ct)
    {
        // Read the whole file into memory first: one fast sequential read over the network, and it works
        // even while someone has the workbook open in Excel (FileShare.ReadWrite).
        var buffer = new MemoryStream();
        await using (var fs = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await fs.CopyToAsync(buffer, ct);
        }
        buffer.Position = 0;

        const string damaged = "file is damaged or still being saved (skipped)";
        return await Task.Run<(ParsedFile?, string?)>(() =>
        {
            var ext = Path.GetExtension(file.FullPath).ToLowerInvariant();
            try
            {
                if (buffer.Length == 0) return (null, "file is empty (skipped)");
                List<SheetData> sheets;
                switch (ext)
                {
                    case ".xlsx" or ".xlsm":
                        if (!XlsxReader.LooksLikeZip(buffer)) return (null, damaged);   // no exception for the common broken-file case
                        sheets = XlsxReader.Read(buffer);
                        break;
                    case ".csv":
                        sheets = [CsvReader.Read(buffer)];
                        break;
                    default:
                        return (null, $"'{ext}' files are not supported (skipped)");
                }
                return (new ParsedFile(sheets.Select(ShipmentSheetParser.Parse).ToList()), null);
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or EndOfStreamException or IndexOutOfRangeException or FormatException)
            {
                return (null, damaged);   // 'Central Directory corrupt', broken XML inside the workbook ...
            }
        }, ct);
    }

    private static string Friendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "no permission to read this file",
        FileNotFoundException => "file not found (deleted meanwhile?)",
        IOException io when io.Message.Contains("being used", StringComparison.OrdinalIgnoreCase) => "file is locked by another program",
        _ => ex.Message
    };

    private static string FolderName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var i = trimmed.LastIndexOfAny(['\\', '/']);
        return i >= 0 && i < trimmed.Length - 1 ? trimmed[(i + 1)..] : trimmed;
    }
}
