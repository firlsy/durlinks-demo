namespace Durlinks.Server.Services;

/// <summary>JWT 签名密钥（启动时校验后单例持有，签发与校验共用同一来源）。</summary>
public sealed class JwtSettings
{
    public string Secret { get; }
    public JwtSettings(string secret) => Secret = secret;
}
