using Durlinks.Server.Data;
using Durlinks.Shared.DTOs;
using Durlinks.Shared.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Durlinks.Server.Controllers;

/// <summary>用户端只读接口：搜索/筛选/分页。仅 :8789 端口可访问（Program.cs 端口守卫）。</summary>
[ApiController]
[Route("api")]
public class PublicController : ControllerBase
{
    private readonly AppDbContext _db;
    public PublicController(AppDbContext db) => _db = db;

    [HttpGet("links")]
    public async Task<ActionResult<PagedResult<LinkDto>>> Search([FromQuery] LinkQuery filter)
    {
        filter.Page = Math.Max(1, filter.Page);
        filter.PageSize = Math.Clamp(filter.PageSize, 1, 500);

        var isClient = string.Equals(filter.Channel, "client", StringComparison.OrdinalIgnoreCase);
        var query = _db.Links
            .Where(x => !x.IsHidden && !x.IsDeleted)
            // 仅展示对当前渠道开放的发布库中的链接（未归类链接不对外）
            .Where(x => x.Library != null && !x.Library.IsDeleted
                        && (isClient ? x.Library.VisibleOnClient : x.Library.VisibleOnWeb))
            .AsQueryable();

        // 指定发布库时只查该库
        if (filter.LibraryId.HasValue && filter.LibraryId.Value > 0)
            query = query.Where(x => x.LibraryId == filter.LibraryId.Value);

        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var kw = filter.Q.Trim();
            query = query.Where(x =>
                x.Title.Contains(kw) || x.Url.Contains(kw) ||
                x.GroupName.Contains(kw) || (x.Summary ?? "").Contains(kw) ||
                (x.Platform ?? "").Contains(kw));
        }
        // 多值优先（同维 OR / 跨维 AND）；未提供多值时回退旧单值参数，保持 web 端契约兼容
        var groups = filter.Groups?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
        var platforms = filter.Platforms?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
        var tagValues = filter.Tags?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
        var sources = filter.ImportSources?.ToList() ?? [];

        // 兼容逗号分隔的多值字段：把值包在逗号里做 Contains，单值与多值都能命中
        if (groups.Count > 0)
            query = query.Where(x => groups.Any(g => ("," + x.GroupName + ",").Contains("," + g + ",")));
        else if (!string.IsNullOrWhiteSpace(filter.Group))
            query = query.Where(x => ("," + x.GroupName + ",").Contains("," + filter.Group + ","));

        if (platforms.Count > 0)
            query = query.Where(x => x.Platform != null && platforms.Any(p => ("," + x.Platform + ",").Contains("," + p + ",")));
        else if (!string.IsNullOrWhiteSpace(filter.Platform))
            query = query.Where(x => x.Platform != null && ("," + x.Platform + ",").Contains("," + filter.Platform + ","));

        if (sources.Count > 0)
            query = query.Where(x => sources.Contains(x.ImportSource));
        else if (filter.ImportSource.HasValue)
            query = query.Where(x => x.ImportSource == filter.ImportSource.Value);

        if (tagValues.Count > 0)
            query = query.Where(x => x.LinkTags.Any(lt => tagValues.Contains(lt.Tag.Name)));
        else if (!string.IsNullOrWhiteSpace(filter.Tag))
            query = query.Where(x => x.LinkTags.Any(lt => lt.Tag.Name == filter.Tag));

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id) // tie-breaker：SortOrder 重复时翻页也必须稳定
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(x => new LinkDto
            {
                Id = x.Id,
                SortOrder = x.SortOrder,
                LinkType = x.LinkType,
                GroupName = x.GroupName,
                Title = x.Title,
                Url = x.Url,
                ExtractCode = x.ExtractCode,
                ExpireAt = x.ExpireAt,
                Platform = x.Platform,
                Version = x.Version,
                Remark = x.Remark,
                Summary = x.Summary,
                CreatedAt = x.CreatedAt,
                ImportSource = x.ImportSource,
                IsHidden = x.IsHidden,
                IsDeleted = x.IsDeleted,
                LibraryId = x.LibraryId,
                LibraryName = x.Library != null ? x.Library.Name : null
            })
            .ToListAsync();

        // 补 assumptions：子列表（多 URL / 附属链接 / 标签）
        var ids = items.Select(i => i.Id).ToList();
        var urlMap = await _db.LinkUrls.Where(u => ids.Contains(u.LinkId))
            .OrderBy(u => u.SortOrder)
            .GroupBy(u => u.LinkId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(u => u.Url).ToList());
        var subMap = await _db.LinkSubLinks.Where(s => ids.Contains(s.LinkId))
            .OrderBy(s => s.SortOrder)
            .GroupBy(s => s.LinkId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(s => new SubLinkDto { Url = s.Url, Title = s.Title }).ToList());
        var tagMap = await _db.LinkTags.Where(t => ids.Contains(t.LinkId))
            .Select(t => new { t.LinkId, t.Tag.Name })
            .GroupBy(t => t.LinkId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(t => t.Name).ToList());

        foreach (var item in items)
        {
            if (urlMap.TryGetValue(item.Id, out var urls) && urls.Count > 0) item.Urls = urls;
            if (subMap.TryGetValue(item.Id, out var subs)) item.SubLinks = subs;
            if (tagMap.TryGetValue(item.Id, out var tags)) item.Tags = tags;
        }

        return Ok(new PagedResult<LinkDto>
        {
            Items = items, Total = total, Page = filter.Page, PageSize = filter.PageSize
        });
    }

    [HttpGet("filters")]
    public async Task<ActionResult<FilterOptionsDto>> Filters([FromQuery] string? channel)
    {
        var isClient = string.Equals(channel, "client", StringComparison.OrdinalIgnoreCase);
        // 只统计当前渠道可见的链接，避免泄露未发布库的分组/平台
        var visible = _db.Links.Where(x => !x.IsHidden && !x.IsDeleted
            && x.Library != null && !x.Library.IsDeleted
            && (isClient ? x.Library.VisibleOnClient : x.Library.VisibleOnWeb));

        // 分组/平台可能存逗号分隔的多值，拆成原子项供前端筛选
        var groupNames = await visible.Select(x => x.GroupName).ToListAsync();
        var platformNames = await visible.Where(x => x.Platform != null).Select(x => x.Platform!).ToListAsync();
        return Ok(new FilterOptionsDto
        {
            Groups = groupNames.SelectMany(x => SplitMulti(x)).Distinct().OrderBy(x => x).ToList(),
            Tags = await visible.SelectMany(x => x.LinkTags).Select(lt => lt.Tag.Name).Distinct().OrderBy(x => x).ToListAsync(),
            Platforms = platformNames.SelectMany(x => SplitMulti(x)).Distinct().OrderBy(x => x).ToList()
        });
    }

    private static IEnumerable<string> SplitMulti(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0);
}
