using Durlinks.Server.Data;
using Durlinks.Server.Services;
using Durlinks.Shared.DTOs;
using Durlinks.Shared.Entities;
using Durlinks.Shared.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Durlinks.Server.Controllers;

/// <summary>管理端：链接管理（筛选后逐个隐藏/删除）、双通道导入、LLM 配置与生成。仅 :8788 可访问，需 JWT。</summary>
[ApiController]
[Route("api")]
[Authorize]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly LlmService _llm;
    private readonly BatchEnrichService _batch;
    private readonly ExcelImportJobService _importJob;
    private readonly LinkDedupeService _dedupe;
    private readonly IConfiguration _cfg;
    private readonly ILogger<AdminController> _log;
    public AdminController(AppDbContext db, LlmService llm, BatchEnrichService batch,
        ExcelImportJobService importJob, LinkDedupeService dedupe, IConfiguration cfg, ILogger<AdminController> log)
    { _db = db; _llm = llm; _batch = batch; _importJob = importJob; _dedupe = dedupe; _cfg = cfg; _log = log; }

    // ---------- 看板统计 ----------
    [HttpGet("admin/stats")]
    public async Task<ActionResult<object>> Stats()
    {
        var total = await _db.Links.CountAsync(x => !x.IsDeleted);
        var hidden = await _db.Links.CountAsync(x => !x.IsDeleted && x.IsHidden);
        var aiCount = await _db.Links.CountAsync(x => !x.IsDeleted && x.ImportSource == Shared.Enums.ImportSource.AiImported);
        var groups = await _db.Links.Where(x => !x.IsDeleted).Select(x => x.GroupName).Distinct().CountAsync();
        var tags = await _db.Tags.CountAsync();
        var netdisk = await _db.Links.CountAsync(x => !x.IsDeleted && x.LinkType == "网盘");
        var download = await _db.Links.CountAsync(x => !x.IsDeleted && x.LinkType == "下载直链");
        return Ok(new { total, hidden, aiCount, groups, tags, netdisk, download });
    }

    // ---------- 链接管理 ----------
    [HttpGet("admin/links")]
    public async Task<ActionResult<PagedResult<LinkDto>>> List([FromQuery] LinkQuery filter)
    {
        // 分页参数钳制：防止 Page<=0 导致负偏移异常、PageSize 过大拖垮内存
        filter.Page = Math.Max(1, filter.Page);
        filter.PageSize = Math.Clamp(filter.PageSize, 1, 200);

        var query = _db.Links.AsQueryable(); // 管理端可见隐藏的，但不含已删除
        query = query.Where(x => !x.IsDeleted);

        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var kw = filter.Q.Trim();
            query = query.Where(x => x.Title.Contains(kw) || x.Url.Contains(kw) || x.GroupName.Contains(kw));
        }
        if (!string.IsNullOrWhiteSpace(filter.Group)) query = query.Where(x => ("," + x.GroupName + ",").Contains("," + filter.Group + ","));
        if (!string.IsNullOrWhiteSpace(filter.Tag))
            query = query.Where(x => x.LinkTags.Any(lt => lt.Tag.Name == filter.Tag));
        if (filter.ImportSource.HasValue) query = query.Where(x => x.ImportSource == filter.ImportSource.Value);
        if (filter.LibraryId.HasValue && filter.LibraryId.Value > 0)
            query = query.Where(x => x.LibraryId == filter.LibraryId.Value);

        var total = await query.CountAsync();
        var entities = await query.Include(x => x.Library).OrderBy(x => x.SortOrder)
            .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToListAsync();
        var dtos = await ToDtos(entities);
        return Ok(new PagedResult<LinkDto> { Items = dtos, Total = total, Page = filter.Page, PageSize = filter.PageSize });
    }

    [HttpPatch("admin/links/{id}/hidden")]
    public async Task<ActionResult> SetHidden(int id, [FromBody] HiddenDto dto)
    {
        var link = await _db.Links.FindAsync(id);
        if (link is null) return NotFound();
        link.IsHidden = dto.Hidden;
        await _db.SaveChangesAsync();
        return Ok(new { id, isHidden = link.IsHidden });
    }

    [HttpDelete("admin/links/{id}")]
    public async Task<ActionResult> SoftDelete(int id)
    {
        var link = await _db.Links.FindAsync(id);
        if (link is null) return NotFound();
        link.IsDeleted = true;
        await _db.SaveChangesAsync();
        return Ok(new { id, isDeleted = true });
    }

    /// <summary>筛选后勾选的 id 列表，逐个隐藏/删除（不做"按标签一键全隐藏"）</summary>
    [HttpPatch("admin/links/batch/hidden")]
    public async Task<ActionResult> BatchHidden([FromBody] BatchIdsDto dto)
    {
        var links = await _db.Links.Where(x => dto.Ids.Contains(x.Id)).ToListAsync();
        foreach (var l in links) l.IsHidden = dto.Hidden;
        await _db.SaveChangesAsync();
        return Ok(new { affected = links.Count, hidden = dto.Hidden });
    }

    [HttpDelete("admin/links/batch")]
    public async Task<ActionResult> BatchDelete([FromBody] BatchIdsDto dto)
    {
        var links = await _db.Links.Where(x => dto.Ids.Contains(x.Id)).ToListAsync();
        foreach (var l in links) l.IsDeleted = true;
        await _db.SaveChangesAsync();
        return Ok(new { affected = links.Count, deleted = true });
    }

    // ---------- 链接去重 ----------
    [HttpPost("admin/links/dedupe")]
    public async Task<ActionResult<DedupeResult>> Dedupe([FromBody] DedupeRequest? req)
    {
        req ??= new DedupeRequest();
        var result = await _dedupe.RunAsync(req, HttpContext.RequestAborted);
        if (!req.DryRun && result.Removed > 0)
            _log.LogInformation("链接去重：范围 {Scope}，库 {Lib}，删除 {Removed} 条", req.Scope, req.LibraryId, result.Removed);
        return Ok(result);
    }

    // ---------- 导入通道 B：Excel（人工导入） ----------
    [HttpPost("admin/import/excel")]
    public async Task<ActionResult> ImportExcel(IFormFile file, [FromQuery] int? libraryId)
    {
        if (file is null || file.Length == 0) return BadRequest(new { message = "未上传文件" });
        const int maxBytes = 10 * 1024 * 1024; // 10MB 上限，防止超大文件打满内存
        if (file.Length > maxBytes) return BadRequest(new { message = "文件不能超过 10MB" });
        var libId = await ResolveLibraryId(libraryId);
        if (libId is null) return BadRequest(new { message = "发布库不存在" });
        using var stream = file.OpenReadStream();
        var links = ExcelImportService.Parse(stream, ImportSource.Manual, out var warnings);
        foreach (var l in links) l.LibraryId = libId;
        // ExcelImportService 只做纯解析，无法查库；同名标签若库里已存在必须复用，否则撞 Tags.Name 唯一索引
        await AttachExistingTags(links);
        _db.Links.AddRange(links);
        await _db.SaveChangesAsync();
        return Ok(new { imported = links.Count, warnings });
    }

    /// <summary>
    /// 把解析结果里的标签换成数据库中的已存在实例（不存在则在本次导入内复用同一实例），
    /// 同时做链接内去重。否则重复导入含相同标签的表格会触发 Tags.Name 唯一索引冲突。
    /// </summary>
    private async Task AttachExistingTags(List<Link> links)
    {
        var names = links.SelectMany(l => l.LinkTags.Select(lt => lt.Tag.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var map = (await _db.Tags.Where(t => names.Contains(t.Name)).ToListAsync())
            .ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);

        foreach (var link in links)
        {
            var kept = new List<LinkTag>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lt in link.LinkTags)
            {
                var name = (lt.Tag.Name ?? string.Empty).Trim();
                if (name.Length == 0 || !seen.Add(name)) continue;
                if (!map.TryGetValue(name, out var tag))
                {
                    tag = lt.Tag;
                    tag.Name = name;
                    map[name] = tag; // 本批次后续链接复用同一实例
                }
                kept.Add(new LinkTag { Tag = tag });
            }
            link.LinkTags.Clear();
            foreach (var k in kept) link.LinkTags.Add(k);
        }
    }

    // ---------- Excel 后台导入（大表：流式解析 + 分批入库 + 进度/取消） ----------
    /// <summary>上传 Excel 并启动后台导入任务，立即返回进度对象（前端轮询）。</summary>
    [HttpPost("admin/import/excel/jobs")]
    public async Task<ActionResult> StartExcelImport(IFormFile file, [FromQuery] int? libraryId)
    {
        if (file is null || file.Length == 0) return BadRequest(new { message = "未上传文件" });
        const long maxBytes = 50L * 1024 * 1024;
        if (file.Length > maxBytes) return BadRequest(new { message = "文件不能超过 50MB" });
        var libId = await ResolveLibraryId(libraryId);
        if (libId is null) return BadRequest(new { message = "发布库不存在" });

        // 落到临时文件，后台任务流式读取，避免把整本 Excel 读进内存
        var temp = Path.Combine(Path.GetTempPath(), $"durlinks-import-{Guid.NewGuid():N}.xlsx");
        await using (var fs = System.IO.File.Create(temp))
            await file.CopyToAsync(fs);

        if (!_importJob.Start(temp, file.FileName, libId.Value, User.Identity?.Name ?? ""))
        {
            try { System.IO.File.Delete(temp); } catch { }
            return Conflict(new { message = "已有导入任务正在运行", status = _importJob.GetStatus() });
        }
        return Ok(new { started = true, status = _importJob.GetStatus() });
    }

    /// <summary>查询 Excel 导入进度。</summary>
    [HttpGet("admin/import/excel/jobs/status")]
    public ActionResult<ExcelImportStatus> ExcelImportStatus() => Ok(_importJob.GetStatus());

    /// <summary>取消 Excel 导入（当前批次存完后停止）。</summary>
    [HttpPost("admin/import/excel/jobs/cancel")]
    public ActionResult CancelExcelImport()
    {
        var ok = _importJob.Cancel();
        return Ok(new { requested = ok, status = _importJob.GetStatus() });
    }

    [HttpGet("admin/import/template")]
    public ActionResult DownloadTemplate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "templates", "导入模板.xlsx");
        if (!System.IO.File.Exists(path))
        {
            // 回退：内容根下的 templates
            path = Path.Combine(Directory.GetCurrentDirectory(), "templates", "导入模板.xlsx");
        }
        if (!System.IO.File.Exists(path)) return NotFound("模板文件不存在");
        var bytes = System.IO.File.ReadAllBytes(path);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "导入模板.xlsx");
    }

    // ---------- 导入通道 A：粘贴链接（AI接入） ----------
    [HttpPost("admin/import/links/parse")]
    public async Task<ActionResult<List<LinkInfoDto>>> ParseLinks([FromBody] PasteDto dto)
    {
        var lines = (dto.Text ?? string.Empty)
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // 每行一次 LLM 调用，限制数量防止 API 成本失控
        if (lines.Length > 100) return BadRequest(new { message = "单次最多识别 100 行" });
        var result = new List<LinkInfoDto>();
        foreach (var line in lines)
        {
            var info = await _llm.RecognizeLinkAsync(line);
            result.Add(new LinkInfoDto
            {
                RawText = line,
                Title = info.Title ?? ExtractDomain(line) ?? line,
                LinkType = info.LinkType ?? "官网/普通网址",
                GroupName = info.GroupName ?? "未分类",
                Platform = info.Platform,
                Tags = info.Tags,
                Summary = info.Summary,
                Recognized = info.Recognized
            });
        }
        return Ok(result);
    }

    // ---------- 手动新建 / 编辑链接 ----------
    [HttpPost("admin/links")]
    public async Task<ActionResult> CreateLink([FromBody] LinkCreateRequest dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Title) && string.IsNullOrWhiteSpace(dto.Url))
            return BadRequest(new { message = "标题和链接至少填一项" });
        if ((dto.Url?.Length ?? 0) > 2000) return BadRequest(new { message = "链接过长" });

        var libId = await ResolveLibraryId(dto.LibraryId);
        if (libId is null) return BadRequest(new { message = "发布库不存在" });

        var maxSort = await _db.Links.Where(x => x.LibraryId == libId)
            .Select(x => (int?)x.SortOrder).MaxAsync() ?? 0;

        var url = dto.Url?.Trim() ?? string.Empty;
        var link = new Link
        {
            LibraryId = libId,
            SortOrder = maxSort + 1,
            LinkType = string.IsNullOrWhiteSpace(dto.LinkType) ? "官网/普通网址" : dto.LinkType,
            GroupName = string.IsNullOrWhiteSpace(dto.GroupName) ? "未分类" : dto.GroupName,
            Title = string.IsNullOrWhiteSpace(dto.Title) ? (ExtractDomain(url) ?? url) : dto.Title,
            Url = url,
            ExtractCode = dto.ExtractCode,
            ExpireAt = dto.ExpireAt,
            Platform = dto.Platform,
            Version = dto.Version,
            Remark = dto.Remark,
            Summary = dto.Summary,
            ImportSource = ImportSource.Manual
        };
        if (!string.IsNullOrWhiteSpace(url))
            link.LinkUrls.Add(new LinkUrl { Url = url, SortOrder = 0 });

        var cache = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in dto.Tags ?? [])
        {
            var name = raw?.Trim();
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
            if (!cache.TryGetValue(name, out var tag))
            {
                tag = await _db.Tags.FirstOrDefaultAsync(t => t.Name == name) ?? new Tag { Name = name };
                cache[name] = tag;
            }
            link.LinkTags.Add(new LinkTag { Tag = tag });
        }

        _db.Links.Add(link);
        await _db.SaveChangesAsync();
        return Ok(new { id = link.Id });
    }

    [HttpPut("admin/links/{id}")]
    public async Task<ActionResult> UpdateLink(int id, [FromBody] LinkUpdateRequest dto)
    {
        var link = await _db.Links.Include(x => x.LinkTags).ThenInclude(lt => lt.Tag)
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        if (link is null) return NotFound();

        if (dto.LibraryId.HasValue)
        {
            var libId = await ResolveLibraryId(dto.LibraryId);
            if (libId is null) return BadRequest(new { message = "发布库不存在" });
            link.LibraryId = libId;
        }
        if (dto.Title is not null) link.Title = dto.Title.Trim();
        if (dto.Url is not null && dto.Url.Trim().Length > 2000) return BadRequest(new { message = "链接过长" });
        if (dto.Url is not null)
        {
            link.Url = dto.Url.Trim();
            var urls = await _db.LinkUrls.Where(u => u.LinkId == id).ToListAsync();
            _db.LinkUrls.RemoveRange(urls);
            if (!string.IsNullOrWhiteSpace(link.Url))
                _db.LinkUrls.Add(new LinkUrl { LinkId = id, Url = link.Url, SortOrder = 0 });
        }
        if (dto.LinkType is not null) link.LinkType = dto.LinkType;
        if (dto.GroupName is not null) link.GroupName = dto.GroupName;
        if (dto.Platform is not null) link.Platform = dto.Platform;
        if (dto.Version is not null) link.Version = dto.Version;
        if (dto.ExtractCode is not null) link.ExtractCode = dto.ExtractCode;
        if (dto.ExpireAt is not null) link.ExpireAt = dto.ExpireAt;
        if (dto.Remark is not null) link.Remark = dto.Remark;
        if (dto.Summary is not null) link.Summary = dto.Summary;
        if (dto.IsHidden.HasValue) link.IsHidden = dto.IsHidden.Value;

        if (dto.Tags is not null)
        {
            link.LinkTags.Clear();
            var cache = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in dto.Tags)
            {
                var name = raw?.Trim();
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                if (!cache.TryGetValue(name, out var tag))
                {
                    tag = await _db.Tags.FirstOrDefaultAsync(t => t.Name == name) ?? new Tag { Name = name };
                    cache[name] = tag;
                }
                link.LinkTags.Add(new LinkTag { Tag = tag });
            }
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = "已保存" });
    }

    /// <summary>把选中链接移动到另一个发布库（原库不再保留）。</summary>
    [HttpPost("admin/links/move")]
    public async Task<ActionResult> MoveLinks([FromBody] MoveCopyLinksRequest req)
    {
        if (req.Ids is null || req.Ids.Count == 0) return BadRequest(new { message = "请先选择链接" });
        if (req.Ids.Count > 500) return BadRequest(new { message = "单次最多 500 条" });
        var target = await ResolveLibraryId(req.TargetLibraryId);
        if (target is null) return BadRequest(new { message = "目标发布库不存在" });

        var links = await _db.Links.Where(x => req.Ids.Contains(x.Id) && !x.IsDeleted).ToListAsync();
        if (links.Count == 0) return BadRequest(new { message = "没有可移动的链接" });
        if (links.All(x => x.LibraryId == target)) return Ok(new { moved = 0, message = "已全部在目标库中" });

        var maxSort = await _db.Links.Where(x => x.LibraryId == target && !x.IsDeleted)
            .Select(x => (int?)x.SortOrder).MaxAsync() ?? 0;
        foreach (var l in links)
        {
            l.LibraryId = target;
            l.SortOrder = ++maxSort;
        }
        await _db.SaveChangesAsync();
        return Ok(new { moved = links.Count });
    }

    /// <summary>把选中链接复制到另一个发布库（原库保留，含 URL/附属链接/标签深拷贝）。</summary>
    [HttpPost("admin/links/copy")]
    public async Task<ActionResult> CopyLinks([FromBody] MoveCopyLinksRequest req)
    {
        if (req.Ids is null || req.Ids.Count == 0) return BadRequest(new { message = "请先选择链接" });
        if (req.Ids.Count > 200) return BadRequest(new { message = "单次最多复制 200 条" });
        var target = await ResolveLibraryId(req.TargetLibraryId);
        if (target is null) return BadRequest(new { message = "目标发布库不存在" });

        var sources = await _db.Links
            .Include(x => x.LinkUrls).Include(x => x.SubLinks)
            .Include(x => x.LinkTags).ThenInclude(lt => lt.Tag)
            .Where(x => req.Ids.Contains(x.Id) && !x.IsDeleted).ToListAsync();
        if (sources.Count == 0) return BadRequest(new { message = "没有可复制的链接" });

        var maxSort = await _db.Links.Where(x => x.LibraryId == target && !x.IsDeleted)
            .Select(x => (int?)x.SortOrder).MaxAsync() ?? 0;
        // 标签按 Id 复用同一实例，避免唯一索引冲突
        var tagCache = new Dictionary<int, Tag>();

        foreach (var src in sources)
        {
            var copy = new Link
            {
                LibraryId = target,
                SortOrder = ++maxSort,
                LinkType = src.LinkType,
                GroupName = src.GroupName,
                Title = src.Title,
                Url = src.Url,
                ExtractCode = src.ExtractCode,
                ExpireAt = src.ExpireAt,
                Platform = src.Platform,
                Version = src.Version,
                Remark = src.Remark,
                Summary = src.Summary,
                ImportSource = src.ImportSource,
                IsHidden = src.IsHidden
            };
            foreach (var u in src.LinkUrls)
                copy.LinkUrls.Add(new LinkUrl { Url = u.Url, SortOrder = u.SortOrder });
            foreach (var s in src.SubLinks)
                copy.SubLinks.Add(new LinkSubLink { Url = s.Url, Title = s.Title, SortOrder = s.SortOrder });
            foreach (var lt in src.LinkTags)
            {
                if (!tagCache.TryGetValue(lt.TagId, out var tag))
                {
                    tag = lt.Tag;
                    tagCache[lt.TagId] = tag;
                }
                copy.LinkTags.Add(new LinkTag { Tag = tag });
            }
            _db.Links.Add(copy);
        }
        await _db.SaveChangesAsync();
        return Ok(new { copied = sources.Count });
    }

    /// <summary>库 id 归一：未指定或不存在时回退第一个可用库。</summary>
    private async Task<int?> ResolveLibraryId(int? libraryId)
    {
        if (libraryId.HasValue && libraryId.Value > 0)
            return await _db.PublishLibraries.AnyAsync(x => x.Id == libraryId.Value && !x.IsDeleted)
                ? libraryId : null;
        return await _db.PublishLibraries.Where(x => !x.IsDeleted)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => (int?)x.Id).FirstOrDefaultAsync();
    }

    [HttpPost("admin/import/links")]
    public async Task<ActionResult> ImportLinks([FromBody] List<LinkInfoDto> items, [FromQuery] int? libraryId)
    {
        if (items is null || items.Count == 0) return BadRequest(new { message = "没有可导入的链接" });
        if (items.Count > 500) return BadRequest(new { message = "单次最多导入 500 条" });
        var libId = await ResolveLibraryId(libraryId);
        if (libId is null) return BadRequest(new { message = "发布库不存在" });
        int count = 0;
        // 同批次内同名标签必须复用同一实例，否则 Tag.Name 唯一索引冲突导致整批失败
        var tagCache = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var url = ExtractUrl(item.RawText) ?? item.RawText;
            var link = new Link
            {
                LibraryId = libId,
                SortOrder = ++count,
                LinkType = string.IsNullOrEmpty(item.LinkType) ? "官网/普通网址" : item.LinkType,
                GroupName = string.IsNullOrEmpty(item.GroupName) ? "未分类" : item.GroupName,
                Title = string.IsNullOrEmpty(item.Title) ? (ExtractDomain(url) ?? url) : item.Title,
                Url = url,
                Platform = item.Platform,
                Summary = item.Summary,
                ImportSource = ImportSource.AiImported
            };
            link.LinkUrls.Add(new LinkUrl { Url = url, SortOrder = 0 });
            var tagsInThisLink = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 同一链接内去重，避免复合主键冲突
            foreach (var tagName in item.Tags ?? [])
            {
                var name = tagName?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!tagsInThisLink.Add(name)) continue;
                if (!tagCache.TryGetValue(name, out var tag))
                {
                    tag = await _db.Tags.FirstOrDefaultAsync(t => t.Name == name) ?? new Tag { Name = name };
                    tagCache[name] = tag;
                }
                link.LinkTags.Add(new LinkTag { Tag = tag });
            }
            _db.Links.Add(link);
        }
        await _db.SaveChangesAsync();
        return Ok(new { imported = count, source = "AI接入" });
    }

    // ---------- LLM 配置与生成（支持多站点/多模型） ----------
    /// <summary>列出全部 LLM 配置（密钥只回传尾 4 位掩码）。</summary>
    [HttpGet("admin/llm/configs")]
    public ActionResult<List<LlmConfigDto>> ListLlmConfigs()
    {
        var list = _db.LlmConfigs.OrderByDescending(x => x.IsDefault).ThenBy(x => x.Id)
            .Select(ToLlmDto).ToList();
        return Ok(list);
    }

    /// <summary>兼容旧接口：返回默认配置。</summary>
    [HttpGet("admin/llm/config")]
    public ActionResult<LlmConfigDto> GetLlmConfig()
    {
        var c = _db.LlmConfigs.Where(x => x.IsConfigured)
                    .OrderByDescending(x => x.IsDefault).ThenBy(x => x.Id).FirstOrDefault()
                ?? _db.LlmConfigs.OrderBy(x => x.Id).FirstOrDefault()
                ?? new LlmConfig();
        return Ok(ToLlmDto(c));
    }

    /// <summary>新建或更新一条配置。Id=0 新建；密钥留空/掩码表示不修改。</summary>
    [HttpPost("admin/llm/configs")]
    public async Task<ActionResult> SaveLlmConfig([FromBody] LlmConfigDto dto)
    {
        var isNew = dto.Id <= 0;
        var c = isNew ? new LlmConfig() : await _db.LlmConfigs.FindAsync(dto.Id);
        if (c is null) return NotFound(new { message = "配置不存在" });

        if (!string.IsNullOrWhiteSpace(dto.Name)) c.Name = dto.Name.Trim();
        c.Protocol = NormalizeProtocol(dto.Protocol);
        c.ApiEndpoint = (dto.ApiEndpoint ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(dto.ApiKey) && !dto.ApiKey.StartsWith("****"))
            c.ApiKey = dto.ApiKey.Trim();

        // 模型清单：允许任意自定义模型名；去空去重保序，上限 30 个，单个名最长 120 字符
        var models = NormalizeModels(dto.Models);
        var active = (dto.Model ?? string.Empty).Trim();
        if (models.Count == 0 && active.Length > 0) models.Add(active);
        if (active.Length == 0 || !models.Contains(active, StringComparer.OrdinalIgnoreCase))
            active = models.FirstOrDefault() ?? string.Empty;
        c.Model = active;
        c.ModelsJson = models.Count == 0 ? string.Empty : JsonSerializer.Serialize(models);
        c.RetryPolicy = NormalizeRetryPolicy(dto.RetryPolicy);
        c.MaxRetries = Math.Clamp(dto.MaxRetries, 0, 10);
        c.RetryOn = NormalizeRetryOn(dto.RetryOn);
        c.IsConfigured = !string.IsNullOrWhiteSpace(c.ApiEndpoint)
                         && !string.IsNullOrWhiteSpace(c.ApiKey)
                         && !string.IsNullOrWhiteSpace(c.Model);
        if (string.IsNullOrWhiteSpace(c.Name))
            c.Name = string.IsNullOrWhiteSpace(c.Model) ? "未命名配置" : c.Model;
        c.UpdatedAt = DateTime.UtcNow;
        if (isNew) _db.LlmConfigs.Add(c);

        // 第一条配置自动成为默认
        var hasDefault = await _db.LlmConfigs.AnyAsync(x => x.IsDefault && x.Id != c.Id);
        if (dto.IsDefault || (!isNew && c.IsDefault) || !hasDefault) c.IsDefault = true;

        await _db.SaveChangesAsync();
        if (c.IsDefault) await ClearOtherDefaults(c.Id);
        return Ok(ToLlmDto(c));
    }

    /// <summary>删除一条配置；若删的是默认项，自动把另一条已配置的提升为默认。</summary>
    [HttpDelete("admin/llm/configs/{id:int}")]
    public async Task<ActionResult> DeleteLlmConfig(int id)
    {
        var c = await _db.LlmConfigs.FindAsync(id);
        if (c is null) return NotFound();
        var wasDefault = c.IsDefault;
        _db.LlmConfigs.Remove(c);
        await _db.SaveChangesAsync();
        if (wasDefault)
        {
            var next = await _db.LlmConfigs.Where(x => x.IsConfigured)
                .OrderBy(x => x.Id).FirstOrDefaultAsync() ?? await _db.LlmConfigs.OrderBy(x => x.Id).FirstOrDefaultAsync();
            if (next is not null) { next.IsDefault = true; await _db.SaveChangesAsync(); }
        }
        return Ok(new { deleted = id });
    }

    /// <summary>把指定配置设为默认（同时取消其它默认项）。</summary>
    [HttpPost("admin/llm/configs/{id:int}/default")]
    public async Task<ActionResult> SetDefaultLlmConfig(int id)
    {
        var c = await _db.LlmConfigs.FindAsync(id);
        if (c is null) return NotFound();
        c.IsDefault = true;
        await _db.SaveChangesAsync();
        await ClearOtherDefaults(id);
        return Ok(new { defaultId = id });
    }

    /// <summary>切换某站点当前使用的模型（模型必须已在该站点的模型清单里）。</summary>
    [HttpPost("admin/llm/configs/{id:int}/model")]
    public async Task<ActionResult> SetLlmModel(int id, [FromBody] SetModelRequest req)
    {
        var c = await _db.LlmConfigs.FindAsync(id);
        if (c is null) return NotFound();
        var models = ParseModels(c);
        var target = (req?.Model ?? string.Empty).Trim();
        var hit = models.FirstOrDefault(m => string.Equals(m, target, StringComparison.OrdinalIgnoreCase));
        if (hit is null) return BadRequest(new { message = "该模型不在站点的模型清单中，请先在编辑里添加" });
        c.Model = hit;
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToLlmDto(c));
    }

    /// <summary>测试 LLM 连接（用输入框的值；密钥留空或为掩码时回退到该条/默认配置的密钥）。</summary>
    [HttpPost("admin/llm/test")]
    public async Task<ActionResult> TestLlm([FromBody] LlmConfigDto dto)
    {
        var saved = dto.Id > 0
            ? await _db.LlmConfigs.FindAsync(dto.Id)
            : await _db.LlmConfigs.OrderByDescending(x => x.IsDefault).ThenBy(x => x.Id).FirstOrDefaultAsync();
        var apiKey = dto.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("****"))
            apiKey = saved?.ApiKey ?? string.Empty;

        var (ok, message, latencyMs) = await _llm.TestAsync(
            dto.ApiEndpoint, apiKey, dto.Model, dto.Protocol,
            dto.RetryPolicy, dto.MaxRetries, dto.RetryOn);
        return Ok(new { ok, message, latencyMs });
    }

    /// <summary>获取站点可用模型（GET /models），供设置页「获取可用模型」按钮使用。</summary>
    [HttpPost("admin/llm/models")]
    public async Task<ActionResult<LlmModelsResult>> ListLlmModels([FromBody] LlmConfigDto dto)
    {
        var saved = dto.Id > 0
            ? await _db.LlmConfigs.FindAsync(dto.Id)
            : await _db.LlmConfigs.OrderByDescending(x => x.IsDefault).ThenBy(x => x.Id).FirstOrDefaultAsync();
        var apiKey = dto.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("****"))
            apiKey = saved?.ApiKey ?? string.Empty;

        var result = await _llm.ListModelsAsync(dto.ApiEndpoint, apiKey, dto.Protocol);
        return Ok(result);
    }

    /// <summary>兼容旧接口：更新默认配置（前端已切换到 /configs）。</summary>
    [HttpPut("admin/llm/config")]
    public async Task<ActionResult> SetLlmConfig([FromBody] LlmConfigDto dto)
    {
        var c = await _db.LlmConfigs.OrderByDescending(x => x.IsDefault).ThenBy(x => x.Id).FirstOrDefaultAsync();
        if (c is null) { c = new LlmConfig { IsDefault = true }; _db.LlmConfigs.Add(c); }
        dto.Id = c.Id;
        return await SaveLlmConfig(dto);
    }

    private static LlmConfigDto ToLlmDto(LlmConfig c)
    {
        var hasKey = !string.IsNullOrWhiteSpace(c.ApiKey);
        return new LlmConfigDto
        {
            Id = c.Id, Name = c.Name, Protocol = string.IsNullOrWhiteSpace(c.Protocol) ? "openai" : c.Protocol,
            ApiEndpoint = c.ApiEndpoint,
            ApiKey = hasKey ? "****" + c.ApiKey[^Math.Min(4, c.ApiKey.Length)..] : string.Empty,
            Model = c.Model, Models = ParseModels(c),
            RetryPolicy = string.IsNullOrWhiteSpace(c.RetryPolicy) ? "default" : c.RetryPolicy,
            MaxRetries = c.MaxRetries,
            RetryOn = (c.RetryOn ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            IsConfigured = c.IsConfigured, IsDefault = c.IsDefault, HasApiKey = hasKey
        };
    }

    /// <summary>读模型清单；老数据只有 Model 时退化为单模型，保证前端不为空。</summary>
    private static List<string> ParseModels(LlmConfig c)
    {
        if (!string.IsNullOrWhiteSpace(c.ModelsJson))
        {
            try
            {
                var arr = JsonSerializer.Deserialize<List<string>>(c.ModelsJson);
                var list = NormalizeModels(arr);
                if (list.Count > 0) return list;
            }
            catch (JsonException) { /* 脏数据按空处理，下面回退 */ }
        }
        return string.IsNullOrWhiteSpace(c.Model) ? new List<string>() : new List<string> { c.Model };
    }

    /// <summary>去空白、去重（忽略大小写）、限长限量，保留用户输入顺序。</summary>
    private static List<string> NormalizeModels(IEnumerable<string>? models)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var m in models ?? Enumerable.Empty<string>())
        {
            var v = (m ?? string.Empty).Trim();
            if (v.Length == 0) continue;
            if (v.Length > 120) v = v[..120];
            if (!seen.Add(v)) continue;
            list.Add(v);
            if (list.Count >= 30) break;
        }
        return list;
    }

    private static readonly string[] KnownRetryOn =
        ["empty", "format", "quota", "rate_limit", "server", "stream_end", "timeout", "network"];

    private static string NormalizeRetryPolicy(string? policy)
    {
        var p = (policy ?? "default").Trim().ToLowerInvariant();
        return p is "default" or "transient" or "all" ? p : "default";
    }

    private static string NormalizeRetryOn(IEnumerable<string>? items)
    {
        var list = (items ?? Enumerable.Empty<string>())
            .Select(x => (x ?? string.Empty).Trim().ToLowerInvariant())
            .Where(x => KnownRetryOn.Contains(x))
            .Distinct().ToList();
        if (list.Count == 0) list = ["empty", "rate_limit", "server", "timeout", "network"];
        return string.Join(',', list);
    }

    private static string NormalizeProtocol(string? protocol)
    {
        var p = (protocol ?? "openai").Trim().ToLowerInvariant();
        return p is "anthropic" or "claude" ? "anthropic"
             : p is "gemini" or "google" ? "gemini"
             : "openai";
    }

    private async Task ClearOtherDefaults(int keepId)
    {
        var others = await _db.LlmConfigs.Where(x => x.IsDefault && x.Id != keepId).ToListAsync();
        if (others.Count == 0) return;
        foreach (var o in others) o.IsDefault = false;
        await _db.SaveChangesAsync();
    }

    [HttpPost("admin/ai/enrich/{id}")]
    public async Task<ActionResult> Enrich(int id)
    {
        // 必须 Include 已有标签关联，否则 link.LinkTags 为空集合导致重复插入触发复合主键冲突
        var link = await _db.Links
            .Include(x => x.LinkTags).ThenInclude(lt => lt.Tag)
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        if (link is null) return NotFound();
        var r = await _llm.EnrichAsync(link.Title, link.Url, link.Remark);
        var existing = new HashSet<string>(link.LinkTags.Select(lt => lt.Tag.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var tagName in r.Tags)
        {
            var name = tagName?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!existing.Add(name)) continue; // 已有关联或本批重复，跳过
            var tag = await _db.Tags.FirstOrDefaultAsync(t => t.Name == name) ?? new Tag { Name = name };
            link.LinkTags.Add(new LinkTag { Tag = tag });
        }
        if (!string.IsNullOrWhiteSpace(r.Summary)) link.Summary = r.Summary;
        await _db.SaveChangesAsync();
        return Ok(new { tags = r.Tags, summary = r.Summary });
    }

    // ---------- 批量 AI 补料 ----------
    /// <summary>启动批量补料任务（后台运行），同一时刻只允许一个任务。</summary>
    [HttpPost("admin/ai/enrich/batch")]
    public ActionResult StartBatchEnrich([FromBody] BatchEnrichRequest dto)
    {
        dto ??= new BatchEnrichRequest();
        var started = _batch.Start(dto);
        if (!started) return Conflict(new { message = "已有批量补料任务正在运行", status = _batch.GetStatus() });
        return Ok(new { started = true, status = _batch.GetStatus() });
    }

    /// <summary>查询批量补料进度。</summary>
    [HttpGet("admin/ai/enrich/batch/status")]
    public ActionResult<BatchEnrichStatus> BatchEnrichStatus() => Ok(_batch.GetStatus());

    /// <summary>请求取消批量补料（当前批次结束后停止）。</summary>
    [HttpPost("admin/ai/enrich/batch/cancel")]
    public ActionResult CancelBatchEnrich()
    {
        var ok = _batch.Cancel();
        return Ok(new { requested = ok, status = _batch.GetStatus() });
    }

    /// <summary>导出补料后的链接为导入模板同结构 xlsx，可直接再次导入。</summary>
    [HttpGet("admin/ai/enrich/export")]
    public async Task<IActionResult> ExportEnriched([FromQuery] int? libraryId, [FromQuery] int limit = 200000)
    {
        var cap = Math.Clamp(limit, 1, 200000);
        var query = _db.Links.AsNoTracking().Where(x => !x.IsDeleted);
        if (libraryId.HasValue && libraryId.Value > 0) query = query.Where(x => x.LibraryId == libraryId.Value);

        // 直接在 SQL 侧投影出 14 列需要的字段（不物化实体、不做导航修复），避免大表导出时内存翻倍
        var rows = await query.OrderBy(x => x.SortOrder).Take(cap)
            .Select(e => new
            {
                e.SortOrder, e.LinkType, e.GroupName, e.Title, e.Url, e.ExtractCode,
                e.ExpireAt, e.Platform, e.Version, e.CreatedAt, e.Remark, e.Summary,
                Urls = e.LinkUrls.OrderBy(u => u.SortOrder).Select(u => u.Url).ToList(),
                SubUrls = e.SubLinks.OrderBy(s => s.SortOrder).Select(s => s.Url).ToList(),
                Tags = e.LinkTags.Select(lt => lt.Tag.Name).ToList()
            })
            .ToListAsync();

        var dtos = new List<LinkDto>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            dtos.Add(new LinkDto
            {
                SortOrder = i + 1, LinkType = r.LinkType, GroupName = r.GroupName, Title = r.Title,
                Url = r.Url, ExtractCode = r.ExtractCode, ExpireAt = r.ExpireAt, Platform = r.Platform,
                Version = r.Version, Remark = r.Remark, Summary = r.Summary, CreatedAt = r.CreatedAt,
                Urls = r.Urls, Tags = r.Tags,
                SubLinks = r.SubUrls.Select(u => new SubLinkDto { Url = u }).ToList()
            });
        }

        // 写入 MemoryStream 后直接以流返回（FileStreamResult），不再 ToArray 复制整份文件
        var ms = new MemoryStream();
        ExcelExportService.Write(ms, dtos);
        ms.Position = 0;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmm");
        return File(ms, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"链接导出_{stamp}.xlsx");
    }

    // ---------- 数据导出（压缩包） ----------
    /// <summary>
    /// 导出链接/发布库/统计/部署信息为 zip。属于敏感操作，必须输入当前账号密码。
    /// 包内不含任何密钥、口令与用户密码哈希。
    /// </summary>
    [HttpPost("admin/export/package")]
    public async Task<IActionResult> ExportPackage([FromBody] ExportPackageRequest? req)
    {
        var username = User.Identity?.Name ?? "";
        var user = await _db.Users.FirstOrDefaultAsync(x => x.Username == username);
        if (user is null) return Unauthorized(new { message = "登录状态已失效，请重新登录" });

        if (string.IsNullOrWhiteSpace(req?.Password) || !PasswordHelper.Verify(req.Password, user.PasswordHash))
        {
            _log.LogWarning("导出数据被拒绝：{User} 密码校验未通过", username);
            return BadRequest(new { message = "密码错误，未导出" });
        }

        var temp = Path.Combine(Path.GetTempPath(), $"durlinks-export-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var fs = System.IO.File.Create(temp))
            {
                var version = typeof(AdminController).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
                await DataExportService.WriteAsync(fs, _db, _cfg, version, HttpContext.RequestAborted);
            }
            var name = $"durlinks-demo-export-{DateTime.Now:yyyyMMdd-HHmm}.zip";
            var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.DeleteOnClose);
            _log.LogInformation("数据导出：{User} 生成 {Name}", username, name);
            return File(stream, "application/zip", name);
        }
        catch
        {
            try { System.IO.File.Delete(temp); } catch { }
            throw;
        }
    }

    // ---------- helpers ----------
    private async Task<List<LinkDto>> ToDtos(List<Link> entities)
    {
        var ids = entities.Select(e => e.Id).ToList();
        var urlMap = await _db.LinkUrls.Where(u => ids.Contains(u.LinkId)).GroupBy(u => u.LinkId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(u => u.Url).ToList());
        var tagMap = await _db.LinkTags.Where(t => ids.Contains(t.LinkId))
            .Select(t => new { t.LinkId, t.Tag.Name }).GroupBy(t => t.LinkId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(t => t.Name).ToList());
        var subMap = await _db.LinkSubLinks.Where(s => ids.Contains(s.LinkId)).GroupBy(s => s.LinkId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(s => new SubLinkDto { Url = s.Url, Title = s.Title }).ToList());

        return entities.Select(e => new LinkDto
        {
            Id = e.Id, SortOrder = e.SortOrder, LinkType = e.LinkType, GroupName = e.GroupName,
            Title = e.Title, Url = e.Url, ExtractCode = e.ExtractCode, ExpireAt = e.ExpireAt,
            Platform = e.Platform, Version = e.Version, Remark = e.Remark, Summary = e.Summary,
            CreatedAt = e.CreatedAt, ImportSource = e.ImportSource, IsHidden = e.IsHidden, IsDeleted = e.IsDeleted,
            LibraryId = e.LibraryId, LibraryName = e.Library != null ? e.Library.Name : null,
            Urls = urlMap.GetValueOrDefault(e.Id) ?? [],
            Tags = tagMap.GetValueOrDefault(e.Id) ?? [],
            SubLinks = subMap.GetValueOrDefault(e.Id) ?? []
        }).ToList();
    }

    private static string? ExtractUrl(string text)
    {
        var i = text.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        if (i < 0) i = text.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        if (i < 0) i = text.IndexOf("magnet:", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = text[i..];
        var end = rest.IndexOfAny([' ', '\t', '\n', '\r', '，', '。']);
        return end > 0 ? rest[..end] : rest;
    }

    private static string? ExtractDomain(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"https?://([^/\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    public class HiddenDto { public bool Hidden { get; set; } = true; }
    public class PasteDto { public string Text { get; set; } = string.Empty; }
    public class LinkInfoDto
    {
        public string RawText { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? LinkType { get; set; }
        public string? GroupName { get; set; }
        public string? Platform { get; set; }
        public List<string> Tags { get; set; } = [];
        public string? Summary { get; set; }
        public bool Recognized { get; set; }
    }
}
