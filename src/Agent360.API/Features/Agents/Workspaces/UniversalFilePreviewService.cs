using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Agent360.API.Features.Agents.Security;

namespace Agent360.API.Features.Agents.Workspaces;

public sealed record UniversalSheetPreviewDto(
    string Name,
    List<string> Headers,
    List<List<string>> Rows,
    int TotalRows);

public sealed record UniversalSlidePreviewDto(
    int Index,
    string Title,
    List<string> Bullets);

public sealed record UniversalArchiveEntryDto(
    string Path,
    long Size,
    long CompressedSize,
    string? ModifiedAt,
    bool IsDirectory);

public sealed record UniversalFilePreviewDto(
    string FileName,
    string Path,
    string Extension,
    string PreviewKind,
    string MimeType,
    long SizeBytes,
    string? TextContent = null,
    List<UniversalSheetPreviewDto>? Sheets = null,
    List<UniversalSlidePreviewDto>? Slides = null,
    List<string>? Paragraphs = null,
    List<UniversalArchiveEntryDto>? ArchiveEntries = null,
    string? InlineUrl = null);

public static class UniversalFilePreviewService
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "mdx", "html", "htm", "svg", "json", "yaml", "yml", "xml", "toml", "ini", "log",
        "py", "ts", "tsx", "js", "jsx", "cs", "go", "rs", "java", "cpp", "c", "h", "sh", "bash", "sql",
        "css", "scss", "less", "vue", "svelte", "env", "dockerfile", "gitignore", "makefile"
    };

    public static UniversalFilePreviewDto BuildPreview(
        byte[] bytes,
        string filePath,
        string? inlineUrl = null)
    {
        var cleanPath = (filePath ?? "").Replace('\\', '/');
        var fileName = Path.GetFileName(cleanPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "artifact";

        var ext = WorkspacePathAcl.ExtractExtension(fileName);
        var (_, previewKind, mimeType) = WorkspacePathAcl.ClassifyArtifact(fileName, "file");
        var sizeBytes = bytes.LongLength;

        try
        {
            if (ext is "csv" or "tsv")
            {
                var delimiter = ext == "tsv" ? '\t' : ',';
                var text = DecodeUtf8Safe(bytes, 512 * 1024);
                var sheet = ParseDelimitedTable(fileName, text, delimiter);
                return new UniversalFilePreviewDto(
                    FileName: fileName,
                    Path: cleanPath,
                    Extension: ext,
                    PreviewKind: "spreadsheet",
                    MimeType: mimeType,
                    SizeBytes: sizeBytes,
                    TextContent: text.Length > 120_000 ? text[..120_000] : text,
                    Sheets: [sheet],
                    InlineUrl: inlineUrl);
            }

            if (ext is "xlsx" or "xlsm")
            {
                var sheets = ParseXlsxSheets(bytes);
                if (sheets.Count > 0)
                {
                    return new UniversalFilePreviewDto(
                        FileName: fileName,
                        Path: cleanPath,
                        Extension: ext,
                        PreviewKind: "spreadsheet",
                        MimeType: mimeType,
                        SizeBytes: sizeBytes,
                        Sheets: sheets,
                        InlineUrl: inlineUrl);
                }
            }

            if (ext == "docx")
            {
                var (paragraphs, tables) = ParseDocxContent(bytes);
                return new UniversalFilePreviewDto(
                    FileName: fileName,
                    Path: cleanPath,
                    Extension: ext,
                    PreviewKind: "document",
                    MimeType: mimeType,
                    SizeBytes: sizeBytes,
                    TextContent: string.Join("\n\n", paragraphs),
                    Sheets: tables.Count > 0 ? tables : null,
                    Paragraphs: paragraphs,
                    InlineUrl: inlineUrl);
            }

            if (ext == "pptx")
            {
                var slides = ParsePptxSlides(bytes);
                var flatText = string.Join("\n\n", slides.Select(s => $"Slide {s.Index}: {s.Title}\n" + string.Join("\n", s.Bullets.Select(b => $"• {b}"))));
                return new UniversalFilePreviewDto(
                    FileName: fileName,
                    Path: cleanPath,
                    Extension: ext,
                    PreviewKind: "presentation",
                    MimeType: mimeType,
                    SizeBytes: sizeBytes,
                    TextContent: flatText,
                    Slides: slides,
                    InlineUrl: inlineUrl);
            }

            if (ext == "rtf")
            {
                var rawRtf = DecodeUtf8Safe(bytes, 512 * 1024);
                var stripped = StripRtf(rawRtf);
                var paras = stripped.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                return new UniversalFilePreviewDto(
                    FileName: fileName,
                    Path: cleanPath,
                    Extension: ext,
                    PreviewKind: "document",
                    MimeType: mimeType,
                    SizeBytes: sizeBytes,
                    TextContent: stripped,
                    Paragraphs: paras,
                    InlineUrl: inlineUrl);
            }

            if (ext == "zip")
            {
                var entries = ParseZipEntries(bytes);
                return new UniversalFilePreviewDto(
                    FileName: fileName,
                    Path: cleanPath,
                    Extension: ext,
                    PreviewKind: "archive",
                    MimeType: mimeType,
                    SizeBytes: sizeBytes,
                    ArchiveEntries: entries,
                    InlineUrl: inlineUrl);
            }

            if (TextExtensions.Contains(ext) || IsLikelyUtf8Text(bytes))
            {
                var text = DecodeUtf8Safe(bytes, 256 * 1024);
                var effectiveKind = ext is "html" or "htm" ? "html"
                    : ext is "md" or "mdx" ? "markdown"
                    : ext == "svg" ? "image"
                    : previewKind == "binary" ? "text" : previewKind;
                return new UniversalFilePreviewDto(
                    FileName: fileName,
                    Path: cleanPath,
                    Extension: ext,
                    PreviewKind: effectiveKind,
                    MimeType: mimeType,
                    SizeBytes: sizeBytes,
                    TextContent: text,
                    InlineUrl: inlineUrl);
            }
        }
        catch
        {
            // Fall through to safe fallback
        }

        return new UniversalFilePreviewDto(
            FileName: fileName,
            Path: cleanPath,
            Extension: ext,
            PreviewKind: previewKind,
            MimeType: mimeType,
            SizeBytes: sizeBytes,
            InlineUrl: inlineUrl);
    }

    private static string DecodeUtf8Safe(byte[] bytes, int maxBytes)
    {
        var count = Math.Min(bytes.Length, maxBytes);
        var text = Encoding.UTF8.GetString(bytes, 0, count);
        return text.TrimStart('\uFEFF');
    }

    private static bool IsLikelyUtf8Text(byte[] bytes)
    {
        if (bytes.Length == 0) return true;
        var sampleLen = Math.Min(bytes.Length, 4096);
        var nullCount = 0;
        for (var i = 0; i < sampleLen; i++)
        {
            if (bytes[i] == 0) nullCount++;
        }
        return nullCount == 0;
    }

    private static UniversalSheetPreviewDto ParseDelimitedTable(string name, string text, char delimiter)
    {
        var allRows = new List<List<string>>();
        var currentRow = new List<string>();
        var currentCell = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        currentCell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    currentCell.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == delimiter)
                {
                    currentRow.Add(currentCell.ToString().Trim());
                    currentCell.Clear();
                }
                else if (c is '\r' or '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    currentRow.Add(currentCell.ToString().Trim());
                    currentCell.Clear();
                    if (currentRow.Any(cell => !string.IsNullOrEmpty(cell)))
                    {
                        allRows.Add(currentRow);
                    }
                    currentRow = [];
                }
                else
                {
                    currentCell.Append(c);
                }
            }
        }

        if (currentCell.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentCell.ToString().Trim());
            if (currentRow.Any(cell => !string.IsNullOrEmpty(cell)))
            {
                allRows.Add(currentRow);
            }
        }

        var totalRows = Math.Max(0, allRows.Count - 1);
        var headers = allRows.Count > 0 ? allRows[0] : [];
        var dataRows = allRows.Skip(1).Take(250).ToList();
        return new UniversalSheetPreviewDto(name, headers, dataRows, totalRows);
    }

    private static List<UniversalSheetPreviewDto> ParseXlsxSheets(byte[] bytes)
    {
        var results = new List<UniversalSheetPreviewDto>();
        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);

        // 1. Read sharedStrings.xml
        var sharedStrings = new List<string>();
        var sstEntry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, "xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase));
        if (sstEntry is not null)
        {
            using var s = sstEntry.Open();
            var doc = XDocument.Load(s);
            foreach (var si in doc.Descendants().Where(x => x.Name.LocalName == "si"))
            {
                var text = string.Concat(si.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value));
                sharedStrings.Add(text);
            }
        }

        // 2. Read workbook.xml + workbook.xml.rels to map sheet names to target files
        var sheetNames = new List<(string Name, string TargetPath)>();
        var wbEntry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, "xl/workbook.xml", StringComparison.OrdinalIgnoreCase));
        var relsEntry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, "xl/_rels/workbook.xml.rels", StringComparison.OrdinalIgnoreCase));

        var relMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (relsEntry is not null)
        {
            using var rs = relsEntry.Open();
            var rdoc = XDocument.Load(rs);
            foreach (var rel in rdoc.Descendants().Where(x => x.Name.LocalName == "Relationship"))
            {
                var id = rel.Attribute("Id")?.Value;
                var target = rel.Attribute("Target")?.Value;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(target))
                {
                    var normalizedTarget = target.TrimStart('/');
                    if (!normalizedTarget.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                    {
                        normalizedTarget = "xl/" + normalizedTarget;
                    }
                    relMap[id] = normalizedTarget;
                }
            }
        }

        if (wbEntry is not null)
        {
            using var ws = wbEntry.Open();
            var wdoc = XDocument.Load(ws);
            var idx = 1;
            foreach (var sh in wdoc.Descendants().Where(x => x.Name.LocalName == "sheet"))
            {
                var name = sh.Attribute("name")?.Value ?? $"Sheet {idx}";
                var rId = sh.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
                if (rId is not null && relMap.TryGetValue(rId, out var targetPath))
                {
                    sheetNames.Add((name, targetPath));
                }
                else
                {
                    sheetNames.Add((name, $"xl/worksheets/sheet{idx}.xml"));
                }
                idx++;
            }
        }

        if (sheetNames.Count == 0)
        {
            var fallbackEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (var i = 0; i < fallbackEntries.Count; i++)
            {
                sheetNames.Add(($"Sheet {i + 1}", fallbackEntries[i].FullName));
            }
        }

        foreach (var (sheetName, targetPath) in sheetNames.Take(10))
        {
            var entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, targetPath, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;

            using var es = entry.Open();
            var sdoc = XDocument.Load(es);
            var parsedRows = new List<List<string>>();
            var maxCols = 0;

            foreach (var rowEl in sdoc.Descendants().Where(x => x.Name.LocalName == "row"))
            {
                var rowCells = new Dictionary<int, string>();
                var nextCol = 0;
                foreach (var cEl in rowEl.Elements().Where(x => x.Name.LocalName == "c"))
                {
                    var cellRef = cEl.Attribute("r")?.Value;
                    var colIdx = ParseColumnIndex(cellRef, nextCol);
                    nextCol = colIdx + 1;

                    var cellType = cEl.Attribute("t")?.Value;
                    var vEl = cEl.Elements().FirstOrDefault(x => x.Name.LocalName == "v");
                    var isEl = cEl.Elements().FirstOrDefault(x => x.Name.LocalName == "is");
                    var val = "";

                    if (cellType == "s" && vEl is not null && int.TryParse(vEl.Value, out var sstIdx) && sstIdx >= 0 && sstIdx < sharedStrings.Count)
                    {
                        val = sharedStrings[sstIdx];
                    }
                    else if (cellType == "inlineStr" && isEl is not null)
                    {
                        val = string.Concat(isEl.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value));
                    }
                    else if (cellType == "b" && vEl is not null)
                    {
                        val = vEl.Value == "1" ? "TRUE" : "FALSE";
                    }
                    else if (vEl is not null)
                    {
                        val = vEl.Value;
                    }

                    rowCells[colIdx] = val.Trim();
                    if (colIdx + 1 > maxCols) maxCols = colIdx + 1;
                }

                if (rowCells.Values.Any(v => !string.IsNullOrEmpty(v)))
                {
                    var maxRowCol = Math.Min(Math.Max(rowCells.Keys.Max() + 1, 1), 40);
                    var list = new List<string>(maxRowCol);
                    for (var c = 0; c < maxRowCol; c++)
                    {
                        list.Add(rowCells.TryGetValue(c, out var cv) ? cv : "");
                    }
                    parsedRows.Add(list);
                }
            }

            if (parsedRows.Count == 0) continue;
            var colCount = Math.Min(Math.Max(maxCols, parsedRows.Max(r => r.Count)), 40);
            foreach (var r in parsedRows)
            {
                while (r.Count < colCount) r.Add("");
            }

            var headers = parsedRows[0];
            var body = parsedRows.Skip(1).Take(250).ToList();
            results.Add(new UniversalSheetPreviewDto(sheetName, headers, body, Math.Max(0, parsedRows.Count - 1)));
        }

        return results;
    }

    private static int ParseColumnIndex(string? cellRef, int fallback)
    {
        if (string.IsNullOrWhiteSpace(cellRef)) return fallback;
        var col = 0;
        var hasLetters = false;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z')
            {
                col = col * 26 + (ch - 'A' + 1);
                hasLetters = true;
            }
            else if (ch is >= 'a' and <= 'z')
            {
                col = col * 26 + (ch - 'a' + 1);
                hasLetters = true;
            }
            else
            {
                break;
            }
        }
        return hasLetters ? col - 1 : fallback;
    }

    private static (List<string> Paragraphs, List<UniversalSheetPreviewDto> Tables) ParseDocxContent(byte[] bytes)
    {
        var paragraphs = new List<string>();
        var tables = new List<UniversalSheetPreviewDto>();

        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        var docEntry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, "word/document.xml", StringComparison.OrdinalIgnoreCase));
        if (docEntry is null) return (paragraphs, tables);

        using var s = docEntry.Open();
        var doc = XDocument.Load(s);
        var body = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "body");
        if (body is null) return (paragraphs, tables);

        var tblIdx = 1;
        foreach (var el in body.Elements())
        {
            if (el.Name.LocalName == "p")
            {
                var pText = ExtractWordParagraphText(el);
                if (!string.IsNullOrWhiteSpace(pText))
                {
                    paragraphs.Add(pText);
                }
            }
            else if (el.Name.LocalName == "tbl")
            {
                var rows = new List<List<string>>();
                foreach (var tr in el.Elements().Where(x => x.Name.LocalName == "tr"))
                {
                    var cells = new List<string>();
                    foreach (var tc in tr.Elements().Where(x => x.Name.LocalName == "tc"))
                    {
                        var cellParas = tc.Elements()
                            .Where(x => x.Name.LocalName == "p")
                            .Select(ExtractWordParagraphText)
                            .Where(t => !string.IsNullOrWhiteSpace(t));
                        cells.Add(string.Join(" ", cellParas));
                    }
                    if (cells.Any(c => !string.IsNullOrWhiteSpace(c)))
                    {
                        rows.Add(cells);
                    }
                }
                if (rows.Count > 0)
                {
                    var headers = rows[0];
                    var data = rows.Skip(1).Take(100).ToList();
                    tables.Add(new UniversalSheetPreviewDto($"Bảng {tblIdx++}", headers, data, Math.Max(0, rows.Count - 1)));
                }
            }
        }

        return (paragraphs, tables);
    }

    private static string ExtractWordParagraphText(XElement pEl)
    {
        var sb = new StringBuilder();
        foreach (var node in pEl.Descendants())
        {
            if (node.Name.LocalName == "t")
            {
                sb.Append(node.Value);
            }
            else if (node.Name.LocalName == "tab")
            {
                sb.Append('\t');
            }
            else if (node.Name.LocalName is "br" or "cr")
            {
                sb.Append('\n');
            }
        }
        return sb.ToString().Trim();
    }

    private static List<UniversalSlidePreviewDto> ParsePptxSlides(byte[] bytes)
    {
        var slides = new List<UniversalSlidePreviewDto>();
        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);

        var slideEntries = archive.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
                        e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e =>
            {
                var m = Regex.Match(e.FullName, @"slide(\d+)\.xml$", RegexOptions.IgnoreCase);
                return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 9999;
            })
            .ToList();

        var idx = 1;
        foreach (var entry in slideEntries.Take(60))
        {
            using var s = entry.Open();
            var doc = XDocument.Load(s);
            var lines = new List<string>();

            foreach (var p in doc.Descendants().Where(x => x.Name.LocalName == "p"))
            {
                var line = string.Concat(p.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value)).Trim();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }

            var title = lines.FirstOrDefault() ?? $"Slide {idx}";
            var bullets = lines.Skip(1).ToList();
            slides.Add(new UniversalSlidePreviewDto(idx++, title, bullets));
        }

        return slides;
    }

    private static List<UniversalArchiveEntryDto> ParseZipEntries(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        return archive.Entries
            .Take(300)
            .Select(e => new UniversalArchiveEntryDto(
                Path: e.FullName,
                Size: e.Length,
                CompressedSize: e.CompressedLength,
                ModifiedAt: e.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                IsDirectory: string.IsNullOrEmpty(e.Name) && e.FullName.EndsWith('/')))
            .ToList();
    }

    private static string StripRtf(string rtf)
    {
        if (string.IsNullOrWhiteSpace(rtf)) return "";
        var text = Regex.Replace(rtf, @"\\par[d]?\b", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\\tab\b", "\t", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\\'[0-9a-fA-F]{2}", "");
        text = Regex.Replace(text, @"\\[a-zA-Z]+-?\d*\s?", "");
        text = text.Replace("{", "").Replace("}", "");
        return text.Trim();
    }
}
