namespace Durlinks.Shared.Enums;

/// <summary>导入来源</summary>
public enum ImportSource
{
    /// <summary>人工填写模板 Excel 导入</summary>
    Manual = 0,

    /// <summary>粘贴链接由 AI 识别导入</summary>
    AiImported = 1
}

/// <summary>用户角色</summary>
public enum UserRole
{
    /// <summary>超级管理员（内置 admin），可创建用户</summary>
    SuperAdmin = 0,

    /// <summary>管理员，可隐藏/删除/导入</summary>
    Admin = 1
}

/// <summary>链接形式（对应 xlsx B 列）</summary>
public static class LinkTypes
{
    public const string DirectDownload = "下载直链";
    public const string OfficialSite = "官网/普通网址";
    public const string NetDisk = "网盘";
    public const string Magnet = "磁力";

    public static readonly string[] All =
        [DirectDownload, OfficialSite, NetDisk, Magnet];
}
