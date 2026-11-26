using System.Security.Cryptography;
using System.Text;
using Durlinks.Shared.Entities;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Durlinks.Server.Services;

/// <summary>JWT 令牌签发与 TOTP(MFA) 工具</summary>
public class AuthService
{
    private readonly JwtSettings _jwt;
    public AuthService(JwtSettings jwt) => _jwt = jwt;

    public string IssueToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };
        var token = new JwtSecurityToken(
            issuer: "durlinks",
            audience: "durlinks-admin",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(12),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ---------- TOTP ----------
    public static string GenerateMfaSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        return Base32Encode(bytes);
    }

    public static string BuildOtpAuthUri(string username, string secret)
    {
        var issuer = Uri.EscapeDataString("Durlinks");
        var label = Uri.EscapeDataString($"Durlinks:{username}");
        return $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
    }

    public static bool VerifyTotp(string secret, string code)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code)) return false;
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        for (var delta = -1; delta <= 1; delta++) // 允许前后 30 秒窗口
        {
            var expected = ComputeTotp(secret, counter + delta);
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(code)))
                return true;
        }
        return false;
    }

    private static string ComputeTotp(string secret, long counter)
    {
        var key = Base32Decode(secret);
        var buf = new byte[8];
        for (int i = 7; i >= 0; i--) { buf[i] = (byte)(counter & 0xFF); counter >>= 8; }
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(buf);
        int offset = hash[^1] & 0x0F;
        var bin = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) |
                  (hash[offset + 2] << 8) | hash[offset + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(alphabet[(value >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(alphabet[(value << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] Base32Decode(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        s = s.TrimEnd('=').ToUpperInvariant();
        var result = new List<byte>();
        int bits = 0, value = 0;
        foreach (var ch in s)
        {
            var idx = alphabet.IndexOf(ch);
            if (idx < 0) continue;
            value = (value << 5) | idx; bits += 5;
            if (bits >= 8) { result.Add((byte)((value >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return result.ToArray();
    }
}
