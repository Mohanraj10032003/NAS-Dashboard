using System.Globalization;
using System.Text.RegularExpressions;
using ShipmentTracker.Api.Models;
using ShipmentTracker.Api.Services.Excel;

namespace ShipmentTracker.Api.Services;

/// <summary>
/// Turns a sheet into shipment rows. It finds the header row, then the Part number / Date / Result columns
/// by name (case, spaces and symbols are ignored), and keeps every other column as "extra".
/// </summary>
public static class ShipmentSheetParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] PartExact = ["partnumber", "partno", "partnum", "partid", "part", "pn", "material", "item",
        "serialnumber", "serialno", "serial", "sn", "modulename", "modelname", "model", "product", "productname", "cameraid", "barcode", "uid"];
    private static readonly string[] PartContains = ["part", "serial", "module"];
    private static readonly string[] DateExact = ["date", "shipdate", "shipmentdate", "datetime", "timestamp"];
    private static readonly string[] DateContains = ["date", "time"];
    private static readonly string[] ResultExact = ["passfail", "result", "status", "pass", "qcstatus", "testresult", "overallresult", "finalresult", "teststatus", "finalstatus"];
    private static readonly string[] ResultContains = ["pass", "result", "status"];

    private static readonly HashSet<string> PassWords = new(StringComparer.OrdinalIgnoreCase)
        { "pass", "passed", "ok", "p", "true", "1", "yes", "good", "accepted" };
    private static readonly HashSet<string> FailWords = new(StringComparer.OrdinalIgnoreCase)
        { "fail", "failed", "ng", "f", "false", "0", "no", "bad", "reject", "rejected" };

    private static readonly Regex IsoDate = new(@"^(\d{4})[\/\-.](\d{1,2})[\/\-.](\d{1,2})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2}))?)?", RegexOptions.Compiled);
    private static readonly Regex DmyDate = new(@"^(\d{1,2})[\/\-.](\d{1,2})[\/\-.](\d{4})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2}))?)?", RegexOptions.Compiled);

    private sealed record Columns(int Part, int Date, int Result)
    {
        public int Found => (Part >= 0 ? 1 : 0) + (Date >= 0 ? 1 : 0) + (Result >= 0 ? 1 : 0);
    }

    public static ParsedSheet Parse(SheetData sheet)
    {
        // Header row = the first of the top rows that names the most known columns (title rows above it are ignored).
        var headerIndex = -1;
        Columns? cols = null;
        for (var r = 0; r < Math.Min(sheet.Rows.Count, 15); r++)
        {
            if (sheet.Rows[r].All(c => c.IsBlank)) continue;
            var found = Detect(sheet.Rows[r].Select(c => c.Text.Trim()).ToArray());
            if (cols is null || found.Found > cols.Found) { cols = found; headerIndex = r; }
            if (found.Found == 3) break;
        }

        if (cols is null || (cols.Part < 0 && cols.Result < 0))
            return new ParsedSheet(sheet.Name, Skipped: true, []);   // not a shipment sheet

        var headers = UniqueHeaders(sheet.Rows[headerIndex]);
        var rows = new List<ParsedRow>();

        for (var r = headerIndex + 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var part = Text(row, cols.Part).Trim();
            var resultRaw = Text(row, cols.Result).Trim();
            if (part.Length == 0 && resultRaw.Length == 0) continue;

            DateTime? date = null;
            if (cols.Date >= 0 && cols.Date < row.Length)
            {
                var cell = row[cols.Date];
                date = cell.Number is { } serial ? CellFormatter.ToDateTime(serial, sheet.Date1904) : ParseDateText(cell.Text);
            }

            var extra = new Dictionary<string, string>();
            for (var c = 0; c < headers.Length; c++)
            {
                if (headers[c] is null || c == cols.Part || c == cols.Date || c == cols.Result) continue;
                extra[headers[c]!] = c < row.Length ? row[c].Text : "";
            }

            rows.Add(new ParsedRow(part, date, resultRaw, ToResult(resultRaw), extra));
        }

        return new ParsedSheet(sheet.Name, Skipped: false, rows);
    }

    // ------------------------------------------------------------------ columns

    private static Columns Detect(string[] headers)
    {
        var norm = headers.Select(h => Regex.Replace(h.ToLowerInvariant(), "[^a-z0-9]", "")).ToArray();
        return new Columns(
            Find(norm, PartExact, PartContains),
            Find(norm, DateExact, DateContains),
            Find(norm, ResultExact, ResultContains));
    }

    private static int Find(string[] norm, string[] exact, string[] contains)
    {
        foreach (var e in exact)
        {
            var i = Array.IndexOf(norm, e);
            if (i >= 0) return i;
        }
        foreach (var c in contains)
        {
            var i = Array.FindIndex(norm, n => n.Length > 0 && n.Contains(c, StringComparison.Ordinal));
            if (i >= 0) return i;
        }
        return -1;
    }

    /// <summary>Header names; blank headers become null (column ignored), repeated names get _1, _2 ...</summary>
    private static string?[] UniqueHeaders(Cell[] headerRow)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new string?[headerRow.Length];
        for (var i = 0; i < headerRow.Length; i++)
        {
            var name = headerRow[i].Text.Trim();
            if (name.Length == 0) continue;
            if (seen.TryGetValue(name, out var n)) { seen[name] = n + 1; name = $"{name}_{n}"; }
            else seen[name] = 1;
            result[i] = name;
        }
        return result;
    }

    private static string Text(Cell[] row, int col) => col >= 0 && col < row.Length ? row[col].Text : "";

    // ------------------------------------------------------------------ values

    public static string ToResult(string raw)
    {
        var s = raw.Trim();
        if (PassWords.Contains(s)) return "PASS";
        if (FailWords.Contains(s)) return "FAIL";
        return "OTHER";
    }

    /// <summary>Text dates are read as LOCAL time (never UTC). yyyy-mm-dd and dd-mm-yyyy, with or without a time.</summary>
    public static DateTime? ParseDateText(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return null;

        var m = IsoDate.Match(s);
        if (m.Success) return Build(m, year: 1, month: 2, day: 3);

        m = DmyDate.Match(s);
        if (m.Success) return Build(m, year: 3, month: 2, day: 1);

        return DateTime.TryParse(s, Inv, DateTimeStyles.None, out var d) ? d : null;
    }

    private static DateTime? Build(Match m, int year, int month, int day)
    {
        int G(int i, int def = 0) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, Inv) : def;
        try { return new DateTime(G(year), G(month), G(day), G(4), G(5), G(6)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
