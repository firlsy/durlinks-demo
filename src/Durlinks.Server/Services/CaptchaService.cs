using System.Collections.Concurrent;
using System.Text;

namespace Durlinks.Server.Services;

/// <summary>轻量图形验证码：生成 SVG，内存保存答案，登录时校验。带过期与惰性清理，防止内存无限增长。</summary>
public class CaptchaService
{
    private sealed record Entry(string Code, long CreatedAt);
    private static readonly ConcurrentDictionary<string, Entry> Store = new();
    private static readonly char[] Chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray(); // 去掉易混淆字符
    private const int TtlSeconds = 300;      // 验证码 5 分钟有效
    private const int MaxEntries = 5000;     // 上限保护
    private static long _lastCleanup;

    public (string Id, string Svg) Generate() => Generate(Guid.NewGuid().ToString("N"));

    /// <summary>按指定 ticket 生成验证码（供图片直出接口用 Cookie 关联）。</summary>
    public (string Id, string Svg) Generate(string ticket)
    {
        CleanupIfNeeded();
        var id = string.IsNullOrWhiteSpace(ticket) ? Guid.NewGuid().ToString("N") : ticket;
        var code = new string(Enumerable.Range(0, 4).Select(_ => Chars[Random.Shared.Next(Chars.Length)]).ToArray());
        Store[id] = new Entry(code, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var rnd = new Random();
        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns='http://www.w3.org/2000/svg' width='120' height='38' viewBox='0 0 120 38'>");
        sb.AppendLine("<rect width='120' height='38' fill='#eef2ff'/>");
        for (var i = 0; i < 4; i++)
        {
            var x1 = rnd.Next(120); var y1 = rnd.Next(38);
            var x2 = rnd.Next(120); var y2 = rnd.Next(38);
            sb.AppendLine($"<line x1='{x1}' y1='{y1}' x2='{x2}' y2='{y2}' stroke='#c7d2fe' stroke-width='1'/>");
        }
        for (var i = 0; i < code.Length; i++)
        {
            var x = 12 + i * 27;
            var y = 26 + rnd.Next(4);
            var color = new[] { "#1e3a8a", "#3730a3", "#0f766e", "#9a3412" }[rnd.Next(4)];
            var rotate = rnd.Next(-15, 15);
            sb.AppendLine($"<text x='{x}' y='{y}' font-family='Arial,sans-serif' font-size='22' font-weight='bold' fill='{color}' transform='rotate({rotate} {x} {y})'>{code[i]}</text>");
        }
        sb.AppendLine("</svg>");
        return (id, sb.ToString());
    }

    /// <summary>校验并消费（一次性）。</summary>
    public bool Verify(string? id, string? code)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(code)) return false;
        if (!Store.TryRemove(id, out var entry)) return false;       // 一次性消费
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - entry.CreatedAt > TtlSeconds) return false; // 过期
        return string.Equals(entry.Code, code.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static void CleanupIfNeeded()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // 每 60 秒清理一次过期项
        if (now - Interlocked.Read(ref _lastCleanup) < 60) return;
        Interlocked.Exchange(ref _lastCleanup, now);
        foreach (var kv in Store)
        {
            if (now - kv.Value.CreatedAt > TtlSeconds)
                Store.TryRemove(kv.Key, out _);
        }
        // 数量上限保护：超出则清空最老的一半（简单起见按 key 顺序移除）
        if (Store.Count > MaxEntries)
        {
            var toRemove = Store.OrderBy(k => k.Value.CreatedAt).Take(Store.Count / 2);
            foreach (var kv in toRemove) Store.TryRemove(kv.Key, out _);
        }
    }
}
