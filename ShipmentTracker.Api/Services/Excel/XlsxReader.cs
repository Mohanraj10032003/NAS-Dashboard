using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ShipmentTracker.Api.Services.Excel;

/// <summary>
/// Reads .xlsx / .xlsm with nothing but the .NET base library.
/// An .xlsx file is a zip of XML files: workbook.xml lists the sheets, sharedStrings.xml holds the text,
/// styles.xml tells which numbers are really dates, and each sheetN.xml holds the cells.
/// </summary>
public static class XlsxReader
{
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// A cheap check (no exception) that the stream really ends like a zip file. A file that is still being saved, was cut off,
    /// or is only an Excel lock file (~$name.xlsx) fails this and can be skipped quietly.
    /// </summary>
    public static bool LooksLikeZip(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 22) return false;
        var tail = (int)Math.Min(stream.Length, 22 + 65535);   // end-of-central-directory record: 22 bytes + comment up to 64 KB
        var buf = new byte[tail];
        stream.Position = stream.Length - tail;
        var read = 0;
        while (read < tail)
        {
            var n = stream.Read(buf, read, tail - read);
            if (n <= 0) break;
            read += n;
        }
        stream.Position = 0;
        for (var i = read - 22; i >= 0; i--)
            if (buf[i] == 0x50 && buf[i + 1] == 0x4B && buf[i + 2] == 0x05 && buf[i + 3] == 0x06) return true;
        return false;
    }

    public static List<SheetData> Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var wbEntry = Find(zip, "xl/workbook.xml")
            ?? throw new InvalidDataException("This is not a valid .xlsx file (xl/workbook.xml is missing).");

        var (sheetRefs, date1904) = ReadWorkbook(wbEntry);
        var rels = ReadRelationships(Find(zip, "xl/_rels/workbook.xml.rels"));
        var shared = ReadSharedStrings(Find(zip, "xl/sharedStrings.xml"));
        var styles = ReadStyles(Find(zip, "xl/styles.xml"));

        var result = new List<SheetData>();
        foreach (var (name, relId) in sheetRefs)
        {
            if (!rels.TryGetValue(relId, out var target)) continue;
            var entry = Find(zip, target);
            if (entry is null) continue;
            result.Add(new SheetData(name, date1904, ReadSheet(entry, shared, styles, date1904)));
        }
        return result;
    }

    // ------------------------------------------------------------------ package parts

    private static ZipArchiveEntry? Find(ZipArchive zip, string path)
    {
        path = path.TrimStart('/');
        return zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    private static (List<(string Name, string RelId)> Sheets, bool Date1904) ReadWorkbook(ZipArchiveEntry entry)
    {
        var sheets = new List<(string, string)>();
        var date1904 = false;
        using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "workbookPr")
            {
                var v = reader.GetAttribute("date1904");
                date1904 = v is "1" or "true";
            }
            else if (reader.LocalName == "sheet")
            {
                var name = reader.GetAttribute("name") ?? "Sheet";
                var id = reader.GetAttribute("id", RelNs);
                if (id is not null) sheets.Add((name, id));
            }
        }
        return (sheets, date1904);
    }

    /// <summary>relationship id -> path of the sheet part inside the zip.</summary>
    private static Dictionary<string, string> ReadRelationships(ZipArchiveEntry? entry)
    {
        var map = new Dictionary<string, string>();
        if (entry is null) return map;
        using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
            var type = reader.GetAttribute("Type") ?? "";
            if (!type.EndsWith("/worksheet", StringComparison.OrdinalIgnoreCase)) continue; // skip chart sheets etc.
            var id = reader.GetAttribute("Id");
            var target = reader.GetAttribute("Target");
            if (id is null || target is null) continue;
            map[id] = Normalize(target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target);
        }
        return map;
    }

    private static string Normalize(string path)
    {
        var stack = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == ".." && stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            else stack.Add(part);
        }
        return string.Join('/', stack);
    }

    private static List<string> ReadSharedStrings(ZipArchiveEntry? entry)
    {
        var list = new List<string>();
        if (entry is null) return list;
        using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
                list.Add(TextOf((XElement)XNode.ReadFrom(reader)));
            else
                reader.Read();
        }
        return list;
    }

    /// <summary>Joins every text run of an &lt;si&gt; / &lt;is&gt; element (rich text has several), ignoring phonetic hints.</summary>
    private static string TextOf(XElement el) =>
        string.Concat(el.Descendants()
            .Where(e => e.Name.LocalName == "t" && !e.Ancestors().Any(a => a.Name.LocalName == "rPh"))
            .Select(e => e.Value));

    /// <summary>Style index (the "s" attribute of a cell) -> how its number should be shown.</summary>
    private static CellStyle[] ReadStyles(ZipArchiveEntry? entry)
    {
        if (entry is null) return [];
        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);

        var custom = doc.Descendants().Where(e => e.Name.LocalName == "numFmt")
            .Select(e => (Id: (int?)e.Attribute("numFmtId"), Code: (string?)e.Attribute("formatCode")))
            .Where(x => x.Id is not null && x.Code is not null)
            .ToDictionary(x => x.Id!.Value, x => x.Code!);

        var cellXfs = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "cellXfs");
        if (cellXfs is null) return [];

        return cellXfs.Elements().Where(e => e.Name.LocalName == "xf")
            .Select(xf =>
            {
                var id = (int?)xf.Attribute("numFmtId") ?? 0;
                custom.TryGetValue(id, out var code);
                return CellFormatter.Classify(id, code);
            })
            .ToArray();
    }

    // ------------------------------------------------------------------ one sheet

    private static List<Cell[]> ReadSheet(ZipArchiveEntry entry, List<string> shared, CellStyle[] styles, bool date1904)
    {
        var rows = new List<Cell[]>();
        List<Cell>? current = null;
        var nextCol = 0;

        using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }

            if (reader.LocalName == "row")
            {
                if (current is not null) rows.Add(current.ToArray());
                current = [];
                nextCol = 0;
                reader.Read();
            }
            else if (reader.LocalName == "c" && current is not null)
            {
                var col = ColumnIndex(reader.GetAttribute("r")) ?? nextCol;
                var type = reader.GetAttribute("t");
                var styleIdx = int.TryParse(reader.GetAttribute("s"), out var si) ? si : 0;
                string? v = null, inline = null;

                if (reader.IsEmptyElement)
                {
                    reader.Read();
                }
                else
                {
                    using (var c = reader.ReadSubtree())
                    {
                        c.Read(); // <c>
                        c.Read(); // first child (or </c>)
                        while (!c.EOF)
                        {
                            if (c.NodeType == XmlNodeType.Element && c.LocalName == "v") v = c.ReadElementContentAsString();
                            else if (c.NodeType == XmlNodeType.Element && c.LocalName == "is") inline = TextOf((XElement)XNode.ReadFrom(c));
                            else c.Read();
                        }
                    }
                    reader.Read(); // step past </c>
                }

                while (current.Count < col) current.Add(Cell.Empty);
                var cell = MakeCell(type, v, inline, styleIdx, shared, styles, date1904);
                if (current.Count == col) current.Add(cell); else current[col] = cell;
                nextCol = col + 1;
            }
            else
            {
                reader.Read();
            }
        }
        if (current is not null) rows.Add(current.ToArray());
        return rows;
    }

    private static Cell MakeCell(string? type, string? v, string? inline, int styleIdx, List<string> shared, CellStyle[] styles, bool date1904)
    {
        switch (type)
        {
            case "s":
                return int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count ? new(shared[idx], null, false) : Cell.Empty;
            case "inlineStr":
                return new(inline ?? v ?? "", null, false);
            case "str":
                return new(v ?? "", null, false);
            case "b":
                return new(v == "1" ? "TRUE" : "FALSE", null, false);
            case "e":
                return new(v ?? "", null, false);
            case "d":
                if (DateTime.TryParse(v, Inv, DateTimeStyles.None, out var iso))
                {
                    var serial = (iso - (date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30))).TotalDays;
                    return new(iso.ToString(iso.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", Inv), serial, true);
                }
                return new(v ?? "", null, false);
        }

        // numeric cell (type "n" or missing)
        if (v is null) return inline is not null ? new(inline, null, false) : Cell.Empty;
        if (!double.TryParse(v, NumberStyles.Float, Inv, out var num)) return new(v, null, false);

        var style = styleIdx >= 0 && styleIdx < styles.Length ? styles[styleIdx] : CellStyle.General;
        var isDate = style.Kind is NumberKind.Date or NumberKind.DateTime or NumberKind.Time;
        return new(CellFormatter.Display(num, style, date1904), num, isDate);
    }

    /// <summary>"AB12" -> 27 (zero based column index).</summary>
    private static int? ColumnIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return null;
        var col = 0;
        var i = 0;
        for (; i < cellRef.Length && char.IsLetter(cellRef[i]); i++)
            col = col * 26 + (char.ToUpperInvariant(cellRef[i]) - 'A' + 1);
        return i == 0 ? null : col - 1;
    }
}
