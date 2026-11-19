namespace ShipmentTracker.Api.Models;

/// <summary>One row of an Excel sheet, exactly the shape the Angular dashboard expects.</summary>
public sealed record ShipmentRecordDto(
    int Id,
    string Part,
    string? Date,            // local time, "yyyy-MM-ddTHH:mm:ss" (no time-zone suffix), null when the row has no date
    string Result,           // PASS | FAIL | OTHER
    string ResultRaw,        // text exactly as written in the sheet
    string Source,           // "sub-folder/file.xlsx › Sheet1"
    Dictionary<string, string> Extra);   // every other column of the sheet

public sealed record ShipmentsResponse(
    string AppId,
    string AppName,
    string Version,
    DateTime GeneratedAtUtc,
    string Folder,
    string FolderName,
    int FileCount,          // files that were read for this query
    int SheetCount,
    int SkippedSheets,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Skipped,   // damaged / still-being-saved files that were left out (not an error)
    string? Warning,
    IReadOnlyList<ShipmentRecordDto> Records,
    int TotalRows,                   // rows of the whole selection
    bool Truncated);                 // true: Records holds only the first MaxTableRows of them

public sealed record DayCount(string Date, int Pass, int Fail);
public sealed record PartFails(string Part, int Fails, int Total);

/// <summary>Only the numbers of a selection (fast to send); the rows come separately from /api/shipments.</summary>
public sealed record SummaryResponse(
    string AppId, string AppName, string Version, DateTime GeneratedAtUtc, string Folder, string FolderName,
    int FileCount, int SheetCount, int SkippedSheets, IReadOnlyList<string> Errors, IReadOnlyList<string> Skipped, string? Warning,
    int Total, int Pass, int Fail, int Other, int PartCount,
    IReadOnlyList<DayCount> Days, IReadOnlyList<PartFails> TopFails);

/// <summary>What exists in an application's folder: used to fill the drop-downs without reading any Excel file.</summary>
public sealed record CatalogResponse(
    string AppId,
    string AppName,
    string Folder,
    int FileCount,
    IReadOnlyList<string> Parts,
    string? MinDate,        // yyyy-MM-dd
    string? MaxDate,
    DateTime BuiltAtUtc,
    string? Warning,
    int MaxRangeDays);   // 0 = no limit

public sealed record AppInfo(string Id, string Name);

public sealed record NasStatus(
    string AppId,
    bool Connected,
    string Folder,
    string FolderName,
    DateTime? LastScanUtc,
    string? LastError,
    int FileCount,
    int RecordCount,
    bool Scanning);

/// <summary>Result of reading one sheet (before ids are assigned).</summary>
public sealed record ParsedRow(string Part, DateTime? Date, string ResultRaw, string Result, Dictionary<string, string> Extra);

public sealed record ParsedSheet(string Name, bool Skipped, List<ParsedRow> Rows);

public sealed record ParsedFile(List<ParsedSheet> Sheets);

public sealed class TooManyFilesException(int count, int max)
    : Exception($"This selection covers {count} files (the limit per load is {max}). Choose a part number or a shorter date range.")
{
    public int Count { get; } = count;
    public int Max { get; } = max;
}

public sealed class NasUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
