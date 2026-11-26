using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Durlinks.Shared.Entities;
using Durlinks.Shared.Enums;

namespace Durlinks.Server.Services;

/// <summary>
/// 解析导入模板 Excel。改用 OpenXML 流式读取：逐行产出、内存占用与行数无关，
/// 8 万行大表也不会把 1.7G 内存的机器打爆（原 ClosedXML 会整本加载）。
/// 表头按列名匹配，兼容 13/14 列版本。
/// </summary>
public static class ExcelImportService
{
    private static readonly char[] TagSeparators = [';', '；', ',', '，'];

    /// <summary>小文件兼容入口：一次性物化为 List（慎用于超大表，优先用 ReadRows 流式处理）。</summary>
    public static List<Link> Parse(Stream excelStream, ImportSource source, out List<string> errors)
    {
        errors = [];
        var links = new List<Link>();
        var sort = 0;
        var rowNo = 1;
        foreach (var cells in ReadRows(excelStream))
        {
            rowNo++;
            var link = MapRow(cells, rowNo, sort + 1, source, errors);
            if (link is null) continue;
            sort++;
            links.Add(link);
        }
        return links;
    }

    /// <summary>
    /// 逐行读取工作表，产出「表头 → 单元格文本」的字典。表头行自身不产出。
    /// 优先选择名称包含「导入」的工作表，否则取第一张。
    /// </summary>
    public static IEnumerable<Dictionary<string, string?>> ReadRows(Stream stream)
    {
        using var doc = SpreadsheetDocument.Open(stream, false);
        var wbPart = doc.WorkbookPart ?? throw new InvalidDataException("Excel 缺少 Workbook");
        var sheets = wbPart.Workbook.Sheets?.Elements<Sheet>().ToList() ?? [];
        var sheet = sheets.FirstOrDefault(s => (s.Name?.Value ?? "").Contains("导入"))
                    ?? sheets.FirstOrDefault()
                    ?? throw new InvalidDataException("Excel 中没有工作表");
        if (sheet.Id?.Value is null) yield break;
        var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id.Value);

        // 共享字符串表：xlsx 里绝大多数文本都存这里
        var shared = new List<string>();
        if (wbPart.SharedStringTablePart?.SharedStringTable is { } sst)
        {
            foreach (var item in sst.Elements<SharedStringItem>())
                shared.Add(item.InnerText ?? string.Empty);
        }

        var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>();
        if (sheetData is null) yield break;

        // 第一步：解析表头，建立列号 → 列名
        Dictionary<int, string>? headerMap = null;
        foreach (var row in sheetData.Elements<Row>())
        {
            if (headerMap is null)
            {
                headerMap = new Dictionary<int, string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var name = (CellText(cell, shared) ?? string.Empty).Trim();
                    if (name.Length == 0) continue;
                    var idx = ColumnIndex(cell.CellReference?.Value);
                    if (idx > 0 && !headerMap.ContainsKey(idx)) headerMap[idx] = name;
                }
                continue;
            }

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in row.Elements<Cell>())
            {
                var idx = ColumnIndex(cell.CellReference?.Value);
                if (idx <= 0 || !headerMap.TryGetValue(idx, out var name)) continue;
                values[name] = CellText(cell, shared);
            }
            yield return values;
        }
    }

    /// <summary>把一行单元格映射成链接实体；整行为空返回 null。</summary>
    public static Link? MapRow(IReadOnlyDictionary<string, string?> cells, int rowNumber,
        int sortOrder, ImportSource source, List<string> errors)
    {
        string? Get(string name)
        {
            if (!cells.TryGetValue(name, out var v)) return null;
            var s = v?.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        var title = Get("名称");
        var url = Get("链接");
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(url)) return null;

        var link = new Link
        {
            SortOrder = int.TryParse(Get("序号"), out var n) ? n : sortOrder,
            LinkType = Get("链接形式") ?? LinkTypes.OfficialSite,
            GroupName = Get("分类") ?? "未分类",
            Title = title ?? url ?? "(未命名)",
            Url = url ?? string.Empty,
            ExtractCode = Get("提取码"),
            ExpireAt = Get("网盘过期时间"),
            Platform = Get("平台"),
            Version = Get("版本"),
            Remark = Get("备注"),
            Summary = Get("AI摘要"),
            ImportSource = source
        };

        var created = Get("添加时间");
        if (created is not null)
        {
            // OpenXML 里日期单元格是 Excel 序列号（如 45292），先按序列号转换。
            // 表格里的时间是北京时间墙上时间，Npgsql 只接受 UTC，需要显式换算，
            // 否则会抛 “Cannot write DateTime with Kind=Unspecified”。
            DateTime? parsed = null;
            if (double.TryParse(created, out var serial) && serial > 20000 && serial < 80000)
                parsed = DateTime.FromOADate(serial);
            else if (DateTime.TryParse(created, out var dt))
                parsed = dt;
            if (parsed is { } value)
                link.CreatedAt = ToUtc(value);
        }

        // E 列多行 URL -> LinkUrls（含主 URL）
        var urlLines = (url ?? string.Empty)
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < urlLines.Length; i++)
            link.LinkUrls.Add(new LinkUrl { Url = urlLines[i], SortOrder = i });

        // J 列附属链接（多行）-> SubLinks
        var sub = Get("附属链接");
        if (!string.IsNullOrEmpty(sub))
        {
            var lines = sub.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < lines.Length; i++)
                link.SubLinks.Add(new LinkSubLink { Url = lines[i], SortOrder = i });
        }

        // L 列标签（分号/逗号分隔）
        var tags = Get("标签");
        if (!string.IsNullOrEmpty(tags))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tn in tags.Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!seen.Add(tn)) continue;
                link.LinkTags.Add(new LinkTag { Tag = new Tag { Name = tn } });
            }
        }

        if (link.LinkType == LinkTypes.NetDisk && string.IsNullOrEmpty(link.ExtractCode))
            errors.Add($"第 {rowNumber} 行：网盘链接未填提取码（已导入，提取码留空）");
        if (link.LinkType == LinkTypes.DirectDownload && string.IsNullOrEmpty(link.Platform))
            errors.Add($"第 {rowNumber} 行：下载直链未填平台（已导入，平台留空）");

        return link;
    }

    /// <summary>
    /// Npgsql 的 timestamptz 只接受 Kind=Utc，Excel 里解析出来的是 Unspecified。
    /// 表格里的「添加时间」是北京时间墙上时间，按 +08:00 解释后存成真正的 UTC 瞬间，
    /// 这样无论用 psql 还是前端按本地时区渲染，看到的都是表格里那一列。
    /// </summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value.AddHours(-8), DateTimeKind.Utc)
    };

    /// <summary>取单元格文本：共享字符串 / 内联字符串 / 布尔 / 数值原样。</summary>
    private static string? CellText(Cell cell, List<string> shared)
    {
        var raw = cell.CellValue?.InnerText;
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            if (int.TryParse(raw, out var i) && i >= 0 && i < shared.Count) return shared[i];
            return null;
        }
        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.InnerText;
        if (cell.DataType?.Value == CellValues.Boolean)
            return raw == "1" ? "TRUE" : "FALSE";
        return raw;
    }

    /// <summary>"AB12" -> 28（1 起）。</summary>
    private static int ColumnIndex(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference)) return 0;
        var idx = 0;
        foreach (var ch in cellReference)
        {
            if (ch is >= 'A' and <= 'Z') idx = idx * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') idx = idx * 26 + (ch - 'a' + 1);
            else break;
        }
        return idx;
    }
}
