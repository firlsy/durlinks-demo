using Durlinks.Shared.Enums;

namespace Durlinks.Shared.Entities;

/// <summary>管理端用户</summary>
public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Admin;
    public string? MfaSecret { get; set; }
    public bool MfaEnabled { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>LLM 配置（管理网页填写）。支持多条：不同 API 站点 / 协议 / 模型，其中一条为默认。</summary>
public class LlmConfig
{
    public int Id { get; set; }
    /// <summary>显示名称，例如「OpenAI 官方」「中转站 A · GPT-4o」</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>协议：openai / openai-responses / anthropic / gemini</summary>
    public string Protocol { get; set; } = "openai";
    /// <summary>重试策略：default（默认仅临时错误）/ transient（按勾选）/ all（所有错误）</summary>
    public string RetryPolicy { get; set; } = "default";
    /// <summary>最大重试次数（0-10）</summary>
    public int MaxRetries { get; set; } = 8;
    /// <summary>可重试的错误类型（CSV）：empty/format/quota/rate_limit/server/stream_end/timeout/network</summary>
    public string RetryOn { get; set; } = "empty,rate_limit,server,timeout,network";
    public string ApiEndpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>当前使用的模型（必须是 ModelsJson 中的一项）</summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>该站点下可用的模型清单，JSON 数组字符串，例如 ["gpt-4o-mini","gpt-4o"]</summary>
    public string ModelsJson { get; set; } = string.Empty;
    public bool IsConfigured { get; set; }
    /// <summary>是否为默认配置（批量补料/链接识别优先使用）</summary>
    public bool IsDefault { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
