namespace Durlinks.Shared.DTOs;

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? TotpCode { get; set; }
    public string? CaptchaId { get; set; }
    public string? CaptchaCode { get; set; }
}

public class CaptchaDto
{
    public string Id { get; set; } = string.Empty;
    public string Svg { get; set; } = string.Empty;      // 完整 SVG 文本
    public string ImageUrl { get; set; } = string.Empty; // data:image/svg+xml;base64,... 前端 <img> 直接用
}

public class MfaCheckDto
{
    public bool MfaEnabled { get; set; }
}

public class LoginResponse
{
    public string Token { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool MfaEnabled { get; set; }
    public bool MfaRequired { get; set; }
}

public class CreateUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class UserDto
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool MfaEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class MfaSetupDto
{
    public string Secret { get; set; } = string.Empty;
    public string QrUri { get; set; } = string.Empty;
}

/// <summary>当前登录账号的 MFA 状态。</summary>
public class MfaStatusDto
{
    public bool MfaEnabled { get; set; }
}

/// <summary>关闭 MFA：需提供当前动态码或登录密码之一。</summary>
public class MfaDisableRequest
{
    public string? Code { get; set; }
    public string? Password { get; set; }
}

/// <summary>修改自己的登录密码。</summary>
public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class LlmConfigDto
{
    /// <summary>0 表示新建；&gt;0 表示编辑已有配置</summary>
    public int Id { get; set; }
    /// <summary>显示名称，留空时自动用「模型名」</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>协议：openai / openai-responses / anthropic / gemini</summary>
    public string Protocol { get; set; } = "openai";
    /// <summary>重试策略：default / transient / all</summary>
    public string RetryPolicy { get; set; } = "default";
    /// <summary>最大重试次数（0-10）</summary>
    public int MaxRetries { get; set; } = 8;
    /// <summary>可重试的错误类型</summary>
    public List<string> RetryOn { get; set; } = new() { "empty", "rate_limit", "server", "timeout", "network" };
    public string ApiEndpoint { get; set; } = string.Empty;
    /// <summary>读取时为脱敏掩码（如 sk-****abcd）；保存时留空表示不修改原密钥。</summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>当前使用的模型</summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>该站点下可用的模型清单（可自定义任意模型名，留空时按 Model 单模型处理）</summary>
    public List<string> Models { get; set; } = new();
    public bool IsConfigured { get; set; }
    /// <summary>是否为默认配置</summary>
    public bool IsDefault { get; set; }
    /// <summary>是否已保存过密钥（前端据此提示"留空不修改"）。</summary>
    public bool HasApiKey { get; set; }
}

/// <summary>切换站点当前使用的模型。</summary>
public class SetModelRequest
{
    public string Model { get; set; } = string.Empty;
}

/// <summary>站点返回的一个可用模型。</summary>
public class LlmModelDto
{
    public string Id { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
}

/// <summary>导出数据压缩包：需输入当前账号密码。</summary>
public class ExportPackageRequest
{
    public string Password { get; set; } = string.Empty;
}

/// <summary>「获取可用模型」的结果。</summary>
public class LlmModelsResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<LlmModelDto> Models { get; set; } = new();
    public long LatencyMs { get; set; }
}
