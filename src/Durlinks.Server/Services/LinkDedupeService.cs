using Durlinks.Server.Data;
using Durlinks.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace Durlinks.Server.Services;

/// <summary>URL 规范化：去首尾空格、统一小写、去掉末尾斜杠，作为去重比较键。</summary>
public static class UrlKey
{
    public static string Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var s = url.Trim().ToLowerInvariant();
        while (s.Length > 1 && s.EndsWith('/')) s = s[..^1];
        return s;
    }
}

/// <summary>
/// 链接去重：按规范化 URL 分组，保留 Id 最小（最早）的一条，其余软删除。
/// 默认只处理当前发布库；scope=all 时处理全部未删除链接（不跨库混合，键含 LibraryId）。
/// </summary>
public class LinkDedupeService
{
    private readonly IServiceScopeFactory _scopes;
    public LinkDedupeService(IServiceScopeFactory scopes) { _scopes = scopes; }

    public async Task<DedupeResult> RunAsync(DedupeRequest req, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var q = db.Links.AsNoTracking().Where(x => !x.IsDeleted);
        var all = req.Scope == "all";
        if (!all && req.LibraryId is > 0)
            q = q.Where(x => x.LibraryId == req.LibraryId.Value);

        // 只取必要字段，避免 17 万行整实体进内存
        var rows = await q.Select(x => new
        {
            x.Id, x.Url, x.Title, x.LibraryId
        }).ToListAsync(ct);

        var groups = new Dictionary<string, List<(int Id, string Url, string Title)>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var key = (all ? (r.LibraryId?.ToString() ?? "0") + "|" : "") + UrlKey.Normalize(r.Url);
            if (key.Length == 0 || (all && key.EndsWith('|'))) continue; // 空 URL 不参与去重
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
            list.Add((r.Id, r.Url, r.Title));
        }

        var dups = groups.Where(g => g.Value.Count > 1).ToList();
        var samples = dups.OrderByDescending(g => g.Value.Count).Take(8)
            .Select(g => new DedupeSampleDto
            {
                Url = g.Value[0].Url,
                Count = g.Value.Count,
                Titles = g.Value.Take(4).Select(x => x.Title).ToList()
            }).ToList();

        var result = new DedupeResult
        {
            Scanned = rows.Count,
            Groups = dups.Count,
            Duplicates = dups.Sum(g => g.Value.Count - 1),
            Samples = samples
        };

        if (req.DryRun)
        {
            result.Message = $"预览：扫描 {result.Scanned} 条，发现 {result.Groups} 组重复，可清理 {result.Duplicates} 条";
            return result;
        }

        // 每组保留 Id 最小的一条，其余软删除；分批 SaveChanges 控制内存
        var removeIds = new List<int>();
        foreach (var g in dups)
        {
            result.Kept++;
            removeIds.AddRange(g.Value.OrderBy(x => x.Id).Skip(1).Select(x => x.Id));
        }

        const int batch = 500;
        for (var i = 0; i < removeIds.Count; i += batch)
        {
            var ids = removeIds.Skip(i).Take(batch).ToList();
            var n = await db.Links.Where(x => ids.Contains(x.Id) && !x.IsDeleted)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDeleted, true), ct);
            result.Removed += n;
        }

        result.Message = result.Removed > 0
            ? $"去重完成：保留 {result.Kept} 条，删除重复 {result.Removed} 条"
            : "没有发现重复链接";
        return result;
    }
}
