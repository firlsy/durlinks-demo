using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Durlinks.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Durlinks.Server.Services;

/// <summary>
/// 生成「数据与运维信息」压缩包：链接、发布库、统计、部署/备份说明。
/// 不含任何密钥、口令、用户密码哈希；链接按流式读取写出，8 万行也不会把内存打满。
/// </summary>
public static class DataExportService
{
    public static async Task WriteAsync(
        Stream output, AppDbContext db, IConfiguration cfg, string version, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        await WriteLinksCsvAsync(zip, db, ct);
        await WriteLibrariesCsvAsync(zip, db, ct);
        await WriteSummaryJsonAsync(zip, db, cfg, version, ct);
        WriteDeploymentInfo(zip, cfg, version);
        WriteReadme(zip, version);
    }

    private static async Task WriteLinksCsvAsync(ZipArchive zip, AppDbContext db, CancellationToken ct)
    {
        var entry = zip.CreateEntry("links.csv", CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true)); // BOM，Excel 直接识别中文

        await writer.WriteLineAsync("链接ID,序号,发布库,链接形式,分类,名称,链接,全部URL,提取码,网盘过期时间,平台,版本,附属链接,标签,备注,AI摘要,来源,状态,添加时间");

        var query = db.Links.AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.LibraryId).ThenBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new
            {
                x.Id,
                x.SortOrder,
                Library = x.LibraryId == null
                    ? ""
                    : db.PublishLibraries.Where(l => l.Id == x.LibraryId).Select(l => l.Name).FirstOrDefault() ?? "",
                x.LinkType,
                x.GroupName,
                x.Title,
                x.Url,
                Urls = x.LinkUrls.OrderBy(u => u.SortOrder).Select(u => u.Url).ToList(),
                x.ExtractCode,
                x.ExpireAt,
                x.Platform,
                x.Version,
                SubLinks = x.SubLinks.OrderBy(s => s.SortOrder).Select(s => s.Url).ToList(),
                Tags = x.LinkTags.Select(lt => lt.Tag.Name).ToList(),
                x.Remark,
                x.Summary,
                x.ImportSource,
                x.IsHidden,
                x.CreatedAt
            });

        await foreach (var r in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            var source = r.ImportSource == Durlinks.Shared.Enums.ImportSource.AiImported ? "AI接入" : "人工导入";
            await writer.WriteLineAsync(string.Join(',',
                r.Id, r.SortOrder, Csv(r.Library), Csv(r.LinkType), Csv(r.GroupName), Csv(r.Title), Csv(r.Url),
                Csv(string.Join(" | ", r.Urls)), Csv(r.ExtractCode), Csv(r.ExpireAt), Csv(r.Platform), Csv(r.Version),
                Csv(string.Join(" | ", r.SubLinks)), Csv(string.Join(";", r.Tags)), Csv(r.Remark), Csv(r.Summary),
                source, r.IsHidden ? "已隐藏" : "可见", r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")));
        }
    }

    private static async Task WriteLibrariesCsvAsync(ZipArchive zip, AppDbContext db, CancellationToken ct)
    {
        var entry = zip.CreateEntry("libraries.csv", CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteLineAsync("ID,名称,描述,网页端,客户端,链接数,排序,创建时间");

        var links = db.Links.AsNoTracking().Where(x => !x.IsDeleted);
        var rows = await db.PublishLibraries.AsNoTracking().Where(x => !x.IsDeleted)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new
            {
                x.Id, x.Name, x.Description, x.VisibleOnWeb, x.VisibleOnClient,
                Count = links.Count(l => l.LibraryId == x.Id),
                x.SortOrder, x.CreatedAt
            }).ToListAsync(ct);

        foreach (var x in rows)
            await writer.WriteLineAsync(string.Join(',',
                x.Id, Csv(x.Name), Csv(x.Description),
                x.VisibleOnWeb ? "是" : "否", x.VisibleOnClient ? "是" : "否",
                x.Count, x.SortOrder, x.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")));
    }

    private static async Task WriteSummaryJsonAsync(
        ZipArchive zip, AppDbContext db, IConfiguration cfg, string version, CancellationToken ct)
    {
        var summary = new
        {
            app = "durlinks-demo",
            version,
            exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            counts = new
            {
                links = await db.Links.CountAsync(x => !x.IsDeleted, ct),
                hidden = await db.Links.CountAsync(x => !x.IsDeleted && x.IsHidden, ct),
                libraries = await db.PublishLibraries.CountAsync(x => !x.IsDeleted, ct),
                tags = await db.Tags.CountAsync(ct),
                users = await db.Users.CountAsync(ct)
            },
            database = new
            {
                provider = cfg["Database:Provider"] ?? "sqlite",
                database = ReadConnection(cfg).GetValueOrDefault("Database", ""),
                host = ReadConnection(cfg).GetValueOrDefault("Host", "127.0.0.1")
            },
            note = "本压缩包不含任何密钥、口令或用户密码哈希；links.csv 可用 Excel 打开。"
        };
        await WriteJsonAsync(zip, "summary.json", summary);
    }

    private static void WriteDeploymentInfo(ZipArchive zip, IConfiguration cfg, string version)
    {
        var conn = ReadConnection(cfg);
        var info = new
        {
            app = "durlinks-demo",
            version,
            exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            server = new
            {
                appDir = "/opt/durlinks-demo",
                container = "durlinks-demo-api",
                image = "durlinks-demo-api:" + version,
                publicPort = cfg.GetValue<int?>("Ports:Public") ?? 8789,
                adminPort = cfg.GetValue<int?>("Ports:Admin") ?? 8788,
                staticDirs = new
                {
                    user = cfg["StaticFiles:UserDir"] ?? "/opt/durlinks-demo/web",
                    admin = cfg["StaticFiles:AdminDir"] ?? "/opt/durlinks-demo/admin"
                },
                template = "/opt/durlinks-demo/server/导入模板.xlsx",
                configFile = "/opt/durlinks-demo/server/appsettings.Production.json（含密钥，未包含在本包内）"
            },
            database = new
            {
                provider = cfg["Database:Provider"] ?? "sqlite",
                host = conn.GetValueOrDefault("Host", "127.0.0.1"),
                port = conn.GetValueOrDefault("Port", "5432"),
                database = conn.GetValueOrDefault("Database", ""),
                user = conn.GetValueOrDefault("Username", ""),
                password = "（未导出，见服务器 appsettings.Production.json）"
            },
            backupAndRestore = new
            {
                backup = "/opt/durlinks-demo/deploy/backup.sh",
                restore = "/opt/durlinks-demo/deploy/restore.sh <备份包> --yes",
                migrate = "NEW_HOST=x.x.x.x SSHPASS=*** PG_PASSWORD=*** /opt/durlinks-demo/deploy/migrate.sh",
                docs = "/opt/durlinks-demo/deploy/docker-compose.prod.yml + docs/BACKUP-AND-MIGRATE.md"
            },
            files = new[]
            {
                "links.csv —— 全部链接（含发布库、标签、附属链接、来源、状态）",
                "libraries.csv —— 发布库及可见渠道、链接数",
                "summary.json —— 数量统计与数据库类型",
                "deployment.json —— 部署路径/端口/镜像与备份恢复命令",
                "README.txt —— 本说明"
            }
        };
        WriteJsonAsync(zip, "deployment.json", info).GetAwaiter().GetResult();
    }

    private static void WriteReadme(ZipArchive zip, string version)
    {
        var entry = zip.CreateEntry("README.txt", CompressionLevel.NoCompression);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine($"durlinks-demo 数据导出包 · v{version}");
        writer.WriteLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine();
        writer.WriteLine("内容：");
        writer.WriteLine("  links.csv        全部链接（UTF-8 BOM，Excel 可直接打开）");
        writer.WriteLine("  libraries.csv    发布库、渠道可见性与链接数");
        writer.WriteLine("  summary.json     链接/隐藏/发布库/标签/用户数量");
        writer.WriteLine("  deployment.json  部署目录、端口、镜像、备份与迁移命令");
        writer.WriteLine();
        writer.WriteLine("安全说明：");
        writer.WriteLine("  · 不包含数据库口令、JWT 密钥、LLM API Key、用户密码哈希");
        writer.WriteLine("  · 如需完整灾备，请在服务器执行 deploy/backup.sh（含数据库 dump 与运行配置）");
        writer.WriteLine();
        writer.WriteLine("恢复提示：");
        writer.WriteLine("  1) 服务器上执行 deploy/backup.sh 生成完整备份");
        writer.WriteLine("  2) 新机执行 deploy/bootstrap.sh，指定备份包与镜像");
        writer.WriteLine("  3) 迁移步骤见 deployment.json 的 backupAndRestore");
    }

    private static async Task WriteJsonAsync(ZipArchive zip, string name, object value)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>解析 PostgreSQL 连接串为字典（仅用于导出展示，绝不输出 Password）。</summary>
    private static Dictionary<string, string> ReadConnection(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres") ?? "";
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in cs.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2) dict[kv[0].Trim()] = kv[1].Trim();
        }
        return dict;
    }

    private static string Csv(string? value)
    {
        var s = value ?? string.Empty;
        if (s.Length == 0) return "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}
