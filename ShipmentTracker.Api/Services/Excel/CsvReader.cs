using System.Text;

namespace ShipmentTracker.Api.Services.Excel;

/// <summary>Small CSV reader: quoted fields, "" escapes, line breaks inside quotes, and , ; or tab as separator.</summary>
public static class CsvReader
{
    public static SheetData Read(Stream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 65536, leaveOpen: true);
        var text = reader.ReadToEnd();

        var sep = DetectSeparator(text);
        var rows = new List<Cell[]>();
        var fields = new List<Cell>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var wasQuoted = false;

        void EndField()
        {
            var s = wasQuoted ? sb.ToString() : sb.ToString().Trim();
            fields.Add(new Cell(s, null, false));
            sb.Clear();
            wasQuoted = false;
        }
        void EndRow()
        {
            EndField();
            if (fields.Any(f => !f.IsBlank)) rows.Add(fields.ToArray());
            fields = [];
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
                continue;
            }

            if (ch == '"' && sb.Length == 0) { inQuotes = true; wasQuoted = true; }
            else if (ch == sep) EndField();
            else if (ch == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); }
            else if (ch == '\n') EndRow();
            else sb.Append(ch);
        }
        if (sb.Length > 0 || fields.Count > 0) EndRow();

        return new SheetData("Sheet1", false, rows);
    }

    private static char DetectSeparator(string text)
    {
        var end = text.IndexOf('\n');
        var first = end < 0 ? text : text[..end];
        char best = ',';
        var bestCount = -1;
        foreach (var c in new[] { ',', ';', '\t' })
        {
            var n = first.Count(x => x == c);
            if (n > bestCount) { best = c; bestCount = n; }
        }
        return best;
    }
}
