using Durlinks.Shared.Enums;

namespace Durlinks.Shared.Entities;

/// <summary>链接主表（对应 xlsx 一行，14 列）</summary>
public class Link
{
    public int Id { get; set; }
    public int SortOrder { get; set; }
    public string LinkType { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? ExtractCode { get; set; }
    public string? ExpireAt { get; set; }
    public string? Platform { get; set; }
    public string? Version { get; set; }
    public string? Remark { get; set; }
    public string? Summary { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ImportSource ImportSource { get; set; } = ImportSource.Manual;
    public bool IsHidden { get; set; }
    public bool IsDeleted { get; set; }
    public int? LibraryId { get; set; }
    public PublishLibrary? Library { get; set; }
    public ICollection<LinkUrl> LinkUrls { get; set; } = new List<LinkUrl>();
    public ICollection<LinkSubLink> SubLinks { get; set; } = new List<LinkSubLink>();
    public ICollection<LinkTag> LinkTags { get; set; } = new List<LinkTag>();
}

/// <summary>链接的多个 URL（E 列多行，一对多）</summary>
public class LinkUrl
{
    public int Id { get; set; }
    public int LinkId { get; set; }
    public string Url { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public Link Link { get; set; } = null!;
}

/// <summary>附属链接（J 列，拆子表，每条一行）</summary>
public class LinkSubLink
{
    public int Id { get; set; }
    public int LinkId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? Title { get; set; }
    public int SortOrder { get; set; }
    public Link Link { get; set; } = null!;
}

/// <summary>标签</summary>
public class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ICollection<LinkTag> LinkTags { get; set; } = new List<LinkTag>();
}

/// <summary>链接-标签 多对多</summary>
public class LinkTag
{
    public int LinkId { get; set; }
    public Link Link { get; set; } = null!;
    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}

/// <summary>分组字典</summary>
public class Group
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>平台字典</summary>
public class PlatformOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>链接形式字典</summary>
public class LinkTypeOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
