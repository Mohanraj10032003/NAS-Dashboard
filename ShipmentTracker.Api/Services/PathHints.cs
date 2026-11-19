using System.Globalization;
using System.Text.RegularExpressions;

namespace ShipmentTracker.Api.Services;

/// <summary>
/// The report folders are organised like  year / month / PART-NUMBER / yyyy-MM-dd / file.xlsx.
/// From the PATH alone (no file has to be opened) we can tell the part number and the date of a file,
/// so a query for one part and a date range only reads the files that can match.
/// </summary>
public static class PathHints
{
    private static readonly Regex YmdDir = new(@"^(\d{4})[-_.](\d{1,2})[-_.](\d{1,2})$", RegexOptions.Compiled);
    private static readonly Regex DmyDir = new(@"^(\d{1,2})[-_.](\d{1,2})[-_.](\d{4})$", RegexOptions.Compiled);
    private static readonly Regex CompactDir = new(@"^(\d{4})(\d{2})(\d{2})$", RegexOptions.Compiled);
    private static readonly Regex YmdInName = new(@"(?<!\d)(\d{4})[-_.](\d{2})[-_.](\d{2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex CompactInName = new(@"(?<!\d)(20\d{2})(\d{2})(\d{2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex DmyInName = new(@"(?<!\d)(\d{2})[-_.](\d{2})[-_.](20\d{2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex YearDir = new(@"^(19|20)\d{2}$", RegexOptions.Compiled);
    private static readonly Regex YearMonthDir = new(@"^(19|20)\d{2}[-_. ]\d{1,2}$", RegexOptions.Compiled);
    private static readonly Regex MonthNumber = new(@"^(0?[1-9]|1[0-2])$", RegexOptions.Compiled);

    private static readonly HashSet<string> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "january","february","march","april","may","june","july","august","september","october","november","december",
        "jan","feb","mar","apr","jun","jul","aug","sep","sept","oct","nov","dec"
    };

    /// <summary>A date written as a folder name (2026-09-12, 12-09-2026, 20260912), or null.</summary>
    public static DateOnly? DateOf(string segment)
    {
        var s = segment.Trim();
        var m = YmdDir.Match(s);
        if (m.Success) return Make(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        m = DmyDir.Match(s);
        if (m.Success) return Make(m.Groups[3].Value, m.Groups[2].Value, m.Groups[1].Value);
        m = CompactDir.Match(s);
        if (m.Success) return Make(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        return null;
    }

    /// <summary>Year, month or date folders: they say WHEN, not WHAT.</summary>
    public static bool IsCalendar(string segment)
    {
        var s = segment.Trim();
        if (DateOf(s) is not null) return true;
        if (YearDir.IsMatch(s) || YearMonthDir.IsMatch(s) || MonthNumber.IsMatch(s)) return true;
        // "august", "Aug 2026", "2026 August"
        var words = s.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words.All(w => MonthNames.Contains(w) || YearDir.IsMatch(w));
    }

    /// <summary>The date and part number of a file, as far as its path tells. Either can be null.</summary>
    public static (DateOnly? Date, string? Part) Infer(IReadOnlyList<string> dirs, string fileName)
    {
        DateOnly? date = null;
        var dateIndex = -1;
        for (var i = dirs.Count - 1; i >= 0; i--)
        {
            if (DateOf(dirs[i]) is { } d) { date = d; dateIndex = i; break; }
        }
        if (date is null)
        {
            var m = YmdInName.Match(fileName);
            if (m.Success) date = Make(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            if (date is null && (m = CompactInName.Match(fileName)).Success) date = Make(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            if (date is null && (m = DmyInName.Match(fileName)).Success) date = Make(m.Groups[3].Value, m.Groups[2].Value, m.Groups[1].Value);
        }

        // the part number = the closest "what" folder above the date folder (or above the file)
        string? part = null;
        for (var i = (dateIndex >= 0 ? dateIndex : dirs.Count) - 1; i >= 0; i--)
        {
            if (!IsCalendar(dirs[i])) { part = dirs[i].Trim(); break; }
        }
        return (date, part);
    }

    private static DateOnly? Make(string y, string m, string d)
    {
        try { return new DateOnly(int.Parse(y, CultureInfo.InvariantCulture), int.Parse(m, CultureInfo.InvariantCulture), int.Parse(d, CultureInfo.InvariantCulture)); }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or FormatException or OverflowException) { return null; }
    }
}
