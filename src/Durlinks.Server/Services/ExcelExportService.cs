using ClosedXML.Excel;
using Durlinks.Shared.DTOs;

namespace Durlinks.Server.Services;

/// <summary>
/// 把链接导出为导入模板同结构的 xlsx（14 列），可直接再次导入，形成闭环。
/// 针对大数据量优化：固定列宽（不做逐格测量）、直接写目标流（少一次整表内存拷贝）。
/// </summary>
public static class ExcelExportService
{
    private static readonly string[] Headers =
    [
        "序号", "链接形式", "分类", "名称", "链接", "提取码", "网盘过期时间",
        "平台", "版本", "附属链接", "添加时间", "标签", "备注", "AI摘要"
    ];

    // 固定列宽：比 AdjustToContents 快一个量级（后者要遍历全部单元格测文本宽度）
    private static readonly double[] Widths = [7, 12, 12, 34, 44, 10, 14, 12, 10, 34, 12, 22, 24, 46];

    /// <summary>把链接写入指定流（xlsx 二进制）。调用方负责流的生命周期。</summary>
    public static void Write(Stream output, IEnumerable<LinkDto> links)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("导入模板");

        for (var c = 0; c < Headers.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = Headers[c];
            cell.Style.Font.Bold = true;
            ws.Column(c + 1).Width = Widths[c];
        }

        var row = 2;
        foreach (var l in links)
        {
            // 多 URL 单元格内换行，与导入解析保持一致
            var urlText = string.Join('\n', (l.Urls ?? []).Where(u => !string.IsNullOrWhiteSpace(u)));
            if (string.IsNullOrEmpty(urlText)) urlText = l.Url ?? "";
            var subText = string.Join('\n', (l.SubLinks ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s.Url)).Select(s => s.Url));

            ws.Cell(row, 1).Value = row - 1;
            ws.Cell(row, 2).Value = l.LinkType ?? "";
            ws.Cell(row, 3).Value = l.GroupName ?? "";
            ws.Cell(row, 4).Value = l.Title ?? "";
            ws.Cell(row, 5).Value = urlText;
            ws.Cell(row, 6).Value = l.ExtractCode ?? "";
            ws.Cell(row, 7).Value = l.ExpireAt ?? "";
            ws.Cell(row, 8).Value = l.Platform ?? "";
            ws.Cell(row, 9).Value = l.Version ?? "";
            ws.Cell(row, 10).Value = subText;
            ws.Cell(row, 11).Value = l.CreatedAt.ToString("yyyy-MM-dd");
            ws.Cell(row, 12).Value = string.Join(';', l.Tags ?? []);
            ws.Cell(row, 13).Value = l.Remark ?? "";
            ws.Cell(row, 14).Value = l.Summary ?? "";
            row++;
        }

        wb.SaveAs(output);
    }
}
