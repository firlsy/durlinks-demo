using System.Security.Cryptography;
using Durlinks.Server.Services;
using Durlinks.Shared.Entities;
using Durlinks.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace Durlinks.Server.Data;

public static class DbInitializer
{
    /// <summary>首次启动时建库、补齐 schema、建默认 admin、导入样例 xlsx（若提供）。</summary>
    public static void Initialize(AppDbContext db, string? seedXlsxPath, out string adminInitialPassword, ILogger? logger = null)
    {
        db.Database.EnsureCreated();
        // 已有库不会因模型新增而自动改表，这里补发布库表/列并归入默认库
        SchemaUpgrader.Upgrade(db, logger);

        // 首次启动的默认管理员口令：admin / admin123（开箱即用，部署后请立即修改）。
        // 如需自定义，部署时设置环境变量 DURLINKS_ADMIN_PASSWORD，避免使用默认口令。
        adminInitialPassword = Environment.GetEnvironmentVariable("DURLINKS_ADMIN_PASSWORD") is { Length: > 0 } custom
            ? custom
            : "admin123";

        if (!db.Users.Any())
        {
            db.Users.Add(new User
            {
                Username = "admin",
                PasswordHash = PasswordHelper.Hash(adminInitialPassword),
                Role = UserRole.SuperAdmin,
                MfaEnabled = false
            });
        }

        if (!db.LlmConfigs.Any())
        {
            db.LlmConfigs.Add(new LlmConfig { Name = "默认配置", IsConfigured = false, IsDefault = true });
        }

        if (!db.Links.Any() && seedXlsxPath is not null && File.Exists(seedXlsxPath))
        {
            var libId = db.PublishLibraries.OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
                .Select(x => (int?)x.Id).FirstOrDefault();
            using var fs = File.OpenRead(seedXlsxPath);
            var links = ExcelImportService.Parse(fs, ImportSource.Manual, out _);
            foreach (var l in links) l.LibraryId = libId;
            db.Links.AddRange(links);
        }

        db.SaveChanges();
    }
}

/// <summary>简单加盐哈希（PBKDF2）。M2 鉴权复用。</summary>
public static class PasswordHelper
{
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 2) return false;
        var salt = Convert.FromBase64String(parts[0]);
        var expected = Convert.FromBase64String(parts[1]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
