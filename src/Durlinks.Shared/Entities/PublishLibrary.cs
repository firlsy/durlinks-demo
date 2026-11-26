namespace Durlinks.Shared.Entities;

/// <summary>
/// 发布库：一组链接的集合，可自定义名称，并分别控制是否对网页端 / 客户端开放。
/// 管理端「发布管理」在此维护；网页端与客户端只看到对各自渠道开放的库。
/// </summary>
public class PublishLibrary
{
    public int Id { get; set; }

    /// <summary>发布库名称（自定义）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>描述/备注</summary>
    public string? Description { get; set; }

    /// <summary>网页端可查看/搜索</summary>
    public bool VisibleOnWeb { get; set; } = true;

    /// <summary>客户端（桌面端）可查看/搜索</summary>
    public bool VisibleOnClient { get; set; } = true;

    /// <summary>排序（越小越靠前）</summary>
    public int SortOrder { get; set; }

    /// <summary>是否软删除</summary>
    public bool IsDeleted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Link> Links { get; set; } = new List<Link>();
}
