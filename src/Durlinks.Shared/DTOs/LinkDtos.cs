using Durlinks.Shared.Enums;

namespace Durlinks.Shared.DTOs;

/// <summary>链接展示 DTO（用户端/管理端通用，含来源标记）</summary>
public class LinkDto
{
    public int Id { get; set; }
    public int SortOrder { get; set; }
    public string LinkType { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public List<string> Urls { get; set; } = [];
    public string? ExtractCode { get; set; }
    public string? ExpireAt { get; set; }
    public string? Platform { get; set; }
    public string? Version { get; set; }
    public string? Remark { get; set; }
    public string? Summary { get; set; }
    public DateTime CreatedAt { get; set; }
    public ImportSource ImportSource { get; set; }
    public string ImportSourceLabel => ImportSource == ImportSource.AiImported ? "AI接入" : "人工导入";
    public List<SubLinkDto> SubLinks { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public bool IsHidden { get; set; }
    public bool IsDeleted { get; set; }
    public int? LibraryId { get; set; }
    public string? LibraryName { get; set; }
}

public class SubLinkDto
{
    public string Url { get; set; } = string.Empty;
    public string? Title { get; set; }
}

/// <summary>搜索/筛选查询</summary>
public class LinkQuery
{
    public string? Q { get; set; }
    public string? Group { get; set; }
    public string? Tag { get; set; }
    public string? Platform { get; set; }
    public ImportSource? ImportSource { get; set; }
    /// <summary>多值筛选（同维 OR、跨维 AND）。非空时优先于下方旧单值字段，web 端旧参数保持兼容。</summary>
    public List<string>? Groups { get; set; }
    public List<string>? Tags { get; set; }
    public List<string>? Platforms { get; set; }
    public List<ImportSource>? ImportSources { get; set; }
    /// <summary>按发布库筛选（null/0 表示不限）</summary>
    public int? LibraryId { get; set; }
    /// <summary>渠道：web（网页端，默认）/ client（客户端）。决定按哪个可见性过滤发布库</summary>
    public string? Channel { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>发布库展示</summary>
public class LibraryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool VisibleOnWeb { get; set; }
    public bool VisibleOnClient { get; set; }
    public int SortOrder { get; set; }
    public int LinkCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>发布库新建/编辑</summary>
public class LibrarySaveRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool VisibleOnWeb { get; set; } = true;
    public bool VisibleOnClient { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>删除发布库的二次确认：只需输入当前登录账号的密码。</summary>
public class DeleteLibraryRequest
{
    public string Password { get; set; } = string.Empty;
}

/// <summary>Excel 后台导入任务的进度。</summary>
public class ExcelImportStatus
{
    public bool Running { get; set; }
    public string FileName { get; set; } = string.Empty;
    /// <summary>已扫描的数据行数（含空行之后的累计）</summary>
    public int Processed { get; set; }
    /// <summary>已入库的链接条数</summary>
    public int Imported { get; set; }
    /// <summary>因 URL 重复被跳过的条数</summary>
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool CancelRequested { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>手动新建链接</summary>
public class LinkCreateRequest
{
    public int? LibraryId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? LinkType { get; set; }
    public string? GroupName { get; set; }
    public string? Platform { get; set; }
    public string? Version { get; set; }
    public string? ExtractCode { get; set; }
    public string? ExpireAt { get; set; }
    public string? Remark { get; set; }
    public string? Summary { get; set; }
    public List<string> Tags { get; set; } = [];
}

/// <summary>编辑链接（仅覆盖提供的字段）</summary>
public class LinkUpdateRequest
{
    public int? LibraryId { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
    public string? LinkType { get; set; }
    public string? GroupName { get; set; }
    public string? Platform { get; set; }
    public string? Version { get; set; }
    public string? ExtractCode { get; set; }
    public string? ExpireAt { get; set; }
    public string? Remark { get; set; }
    public string? Summary { get; set; }
    public List<string>? Tags { get; set; }
    public bool? IsHidden { get; set; }
}

/// <summary>分页结果</summary>
public class PagedResult<T>
{
    public List<T> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

/// <summary>筛选选项</summary>
public class FilterOptionsDto
{
    public List<string> Groups { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> Platforms { get; set; } = [];
}

/// <summary>管理端分组/平台/标签选项集合。</summary>
public class CategoryOptionsDto
{
    public List<string> Groups { get; set; } = [];
    public List<string> Platforms { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> LinkTypes { get; set; } = [];
}

public class CategoryNameRequest
{
    public string? Name { get; set; }
}

public class CategoryRenameRequest
{
    public string? OldName { get; set; }
    public string? NewName { get; set; }
}

public class CategoryBatchDeleteRequest
{
    public List<string> Names { get; set; } = [];
}

public class CategoryBatchDeleteResult
{
    public List<string> Deleted { get; set; } = [];
    public List<string> Skipped { get; set; } = [];
}

/// <summary>批量隐藏/删除请求（筛选后勾选的 id 列表）</summary>
public class BatchIdsDto
{
    public List<int> Ids { get; set; } = [];
    public bool Hidden { get; set; } = true;
}

/// <summary>把链接移动到/复制到指定发布库</summary>
public class MoveCopyLinksRequest
{
    public List<int> Ids { get; set; } = [];
    public int TargetLibraryId { get; set; }
}

/// <summary>批量 AI 补料请求：对未补料链接并发调用 LLM 生成标签/摘要并回写</summary>
public class BatchEnrichRequest
{
    /// <summary>补料范围：missing=仅无摘要或无标签（默认），all=全部链接</summary>
    public string Mode { get; set; } = "missing";
    /// <summary>本次最多处理条数；&lt;=0 时按安全默认值 100 处理（不会理解为全量）</summary>
    public int Limit { get; set; } = 100;
    /// <summary>并发数（1-8），默认 3</summary>
    public int Concurrency { get; set; } = 3;
    /// <summary>仅处理指定发布库，0=全部</summary>
    public int LibraryId { get; set; }
}

/// <summary>批量 AI 补料进度</summary>
public class BatchEnrichStatus
{
    public bool Running { get; set; }
    public string Mode { get; set; } = "missing";
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    /// <summary>因配置缺失/无变化被跳过的条数</summary>
    public int Skipped { get; set; }
    public string Message { get; set; } = "";
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public bool CancelRequested { get; set; }
}

/// <summary>链接去重请求：scope=library(当前库默认)/all(全部库)；dryRun=true 只预览不删除。</summary>
public class DedupeRequest
{
    public string Scope { get; set; } = "library";
    public int? LibraryId { get; set; }
    public bool DryRun { get; set; }
}

/// <summary>去重结果：按规范化 URL 分组后的重复统计。</summary>
public class DedupeResult
{
    public int Scanned { get; set; }
    public int Groups { get; set; }
    public int Duplicates { get; set; }
    public int Removed { get; set; }
    public int Kept { get; set; }
    public List<DedupeSampleDto> Samples { get; set; } = [];
    public string Message { get; set; } = string.Empty;
}

public class DedupeSampleDto
{
    public string Url { get; set; } = string.Empty;
    public int Count { get; set; }
    public List<string> Titles { get; set; } = [];
}
