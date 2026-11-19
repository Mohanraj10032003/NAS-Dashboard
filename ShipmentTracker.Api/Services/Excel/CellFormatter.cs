using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ShipmentTracker.Api.Services.Excel;

public enum NumberKind { General, Number, Date, Time, DateTime }

/// <summary>What an Excel number format means for display: a date, a time, or a plain number pattern.</summary>
public sealed record CellStyle(NumberKind Kind, string? Pattern)
{
    public static readonly CellStyle General = new(NumberKind.General, null);
}

public static class CellFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Regex SimpleNumber = new(@"^[#0,]*(\.[0#]+)?%?$", RegexOptions.Compiled);

    /// <summary>Works out the style for a numFmtId (+ custom format code, when the file defines one).</summary>
    public static CellStyle Classify(int numFmtId, string? formatCode)
    {
        if (formatCode is null)
        {
            return numFmtId switch
            {
                0 => CellStyle.General,
                1 => new(NumberKind.Number, "0"),
                2 => new(NumberKind.Number, "0.00"),
                3 => new(NumberKind.Number, "#,##0"),
                4 => new(NumberKind.Number, "#,##0.00"),
                9 => new(NumberKind.Number, "0%"),
                10 => new(NumberKind.Number, "0.00%"),
                14 or 15 or 16 or 17 => new(NumberKind.Date, null),
                18 or 19 or 20 or 21 or 45 or 46 or 47 => new(NumberKind.Time, null),
                22 => new(NumberKind.DateTime, null),
                >= 27 and <= 36 => new(NumberKind.Date, null),
                >= 50 and <= 58 => new(NumberKind.Date, null),
                _ => CellStyle.General
            };
        }

        if (formatCode.Equals("General", StringComparison.OrdinalIgnoreCase)) return CellStyle.General;

        var section = Clean(formatCode.Split(';')[0]);
        var hasDate = section.Contains('y') || section.Contains('d');
        var hasTime = section.Contains('h') || section.Contains('s');
        if (!hasDate && !hasTime && section.Contains('m') && !section.Contains('0') && !section.Contains('#'))
            hasDate = true; // bare "m" / "mmm" = month name

        if (hasDate && hasTime) return new(NumberKind.DateTime, null);
        if (hasDate) return new(NumberKind.Date, null);
        if (hasTime) return new(NumberKind.Time, null);

        var pattern = section.Replace("_", "").Replace("*", "").Replace("\\", "");
        return SimpleNumber.IsMatch(pattern) && pattern.Length > 0
            ? new CellStyle(NumberKind.Number, pattern)
            : CellStyle.General;
    }

    /// <summary>Removes quoted text, [colour]/[condition] blocks and escaped characters from a format code.</summary>
    private static string Clean(string code)
    {
        var sb = new StringBuilder();
        var inQuote = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '"') { inQuote = !inQuote; continue; }
            if (inQuote) continue;
            if (c == '\\' || c == '_' || c == '*') { i++; continue; }          // escaped / padding char
            if (c == '[')
            {
                var end = code.IndexOf(']', i);
                if (end < 0) break;
                var inner = code.Substring(i + 1, end - i - 1).ToLowerInvariant();
                if (inner is "h" or "hh" or "m" or "mm" or "s" or "ss") sb.Append(inner[0]); // [h]:mm:ss elapsed time
                i = end;
                continue;
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ display text

    public static string Display(double value, CellStyle style, bool date1904)
    {
        switch (style.Kind)
        {
            case NumberKind.Date:
            case NumberKind.DateTime:
            case NumberKind.Time:
                return DisplayDateLike(value, style.Kind, date1904);
            case NumberKind.Number when style.Pattern is not null:
                try { return value.ToString(style.Pattern, Inv); }
                catch (FormatException) { return General(value); }
            default:
                return General(value);
        }
    }

    public static string General(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "";
        if (Math.Abs(value) < 1e15 && value == Math.Floor(value)) return ((long)value).ToString(Inv);
        return value.ToString("G15", Inv);
    }

    private static string DisplayDateLike(double serial, NumberKind kind, bool date1904)
    {
        var seconds = (long)Math.Round(serial * 86400.0);
        if (kind == NumberKind.Time)
        {
            // elapsed time: 25:30:00 stays 25:30:00
            var h = seconds / 3600;
            var m = seconds % 3600 / 60;
            var s = seconds % 60;
            return $"{h:00}:{m:00}:{s:00}";
        }

        var dt = ToDateTime(serial, date1904);
        if (dt is null) return General(serial);
        var d = dt.Value;
        if (kind == NumberKind.Date || d.TimeOfDay == TimeSpan.Zero) return d.ToString("yyyy-MM-dd", Inv);
        return d.ToString("yyyy-MM-dd HH:mm:ss", Inv);
    }

    /// <summary>Excel serial number to local wall-clock time (no time-zone maths, so no drift).</summary>
    public static DateTime? ToDateTime(double serial, bool date1904)
    {
        if (double.IsNaN(serial) || serial < 1 || serial > 2958465) return null; // < 1 = time-only cell
        var epoch = date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30);
        return epoch.AddSeconds(Math.Round(serial * 86400.0));
    }
}
