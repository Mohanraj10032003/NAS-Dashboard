namespace ShipmentTracker.Api.Services.Excel;

/// <summary>
/// One spreadsheet cell.
/// Text   = what Excel shows (dates, times and numbers already formatted),
/// Number = the underlying number for numeric / date cells (null for text cells),
/// IsDate = the cell carries a date or time number format.
/// </summary>
public readonly record struct Cell(string Text, double? Number, bool IsDate)
{
    public static readonly Cell Empty = new("", null, false);
    public bool IsBlank => Number is null && string.IsNullOrWhiteSpace(Text);
}

public sealed class SheetData(string name, bool date1904, List<Cell[]> rows)
{
    public string Name { get; } = name;
    public bool Date1904 { get; } = date1904;
    public List<Cell[]> Rows { get; } = rows;
}
