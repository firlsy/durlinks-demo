using System.Security.Cryptography;
using System.Text;
using Durlinks.Server.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ===== 双数据库后端：SQLite（本地/自用）与 PostgreSQL（服务器）=====
// 通过配置 Database:Provider 切换：sqlite（默认）| postgres
var provider = (builder.Configuration["Database:Provider"] ?? "sqlite").ToLowerInvariant();
var fullDbPath = "";
string connStr;
if (provider == "postgres")
{
    connStr = builder.Configuration.GetConnectionString("Postgres")
              ?? throw new InvalidOperationException("使用 PostgreSQL 时必须配置 ConnectionStrings:Postgres");
}
else
{
    var dbPath = builder.Configuration["Database:Path"] ?? "data/durlinks.db";
    fullDbPath = Path.GetFullPath(dbPath, AppContext.BaseDirectory);
    Directory.CreateDirectory(Path.GetDirectoryName(fullDbPath)!);
    connStr = $"Data Source={fullDbPath}";
}

builder.Services.AddDbContext<AppDbContext>(opt =>
{
    if (provider == "postgres") opt.UseNpgsql(connStr);
    else opt.UseSqlite(connStr);
});
builder.Services.AddHttpClient();
// 本地模型（Ollama / LM Studio）常监听回环地址，系统代理会把 127.0.0.1 也劫持，这里给它一条直连通道
builder.Services.AddHttpClient("llm-direct")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseProxy = false });
builder.Services.AddScoped<Durlinks.Server.Services.AuthService>();
builder.Services.AddScoped<Durlinks.Server.Services.LlmService>();
builder.Services.AddScoped<Durlinks.Server.Services.LinkDedupeService>();
builder.Services.AddSingleton<Durlinks.Server.Services.CaptchaService>();
// 批量 AI 补料后台任务（单例，内部用 IServiceScopeFactory 创建作用域）
builder.Services.AddSingleton<Durlinks.Server.Services.BatchEnrichService>();
// Excel 后台导入（大表流式解析 + 分批入库）
builder.Services.AddSingleton<Durlinks.Server.Services.ExcelImportJobService>();
// 表单上传上限：83k 行模板约 10-30MB，留足余量（默认 128MB 的 Kestrel 限制另配）
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
    o.MultipartBodyLengthLimit = 64L * 1024 * 1024);

// JWT 认证：生产环境必须显式配置足够长的密钥，否则拒绝启动（禁止硬编码弱密钥）
var jwtSecret = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret.Length < 32
    || jwtSecret.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
    || jwtSecret.StartsWith("dev-", StringComparison.OrdinalIgnoreCase))
{
    if (builder.Environment.IsDevelopment())
    {
        // 本地开发自动生成随机密钥，避免硬编码
        jwtSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    }
    else
    {
        throw new InvalidOperationException("生产环境必须通过 Jwt:Secret 配置至少 32 位的随机密钥（不得含 CHANGE_ME/dev- 等占位符）");
    }
}
builder.Services.AddSingleton(new Durlinks.Server.Services.JwtSettings(jwtSecret));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt => opt.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = "durlinks", ValidAudience = "durlinks-admin",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
    });
builder.Services.AddAuthorization();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

// 用户端端口（只读）/ 管理端端口（全权限），从配置读取
var publicPort = builder.Configuration.GetValue<int>("Ports:Public", 8789);
var adminPort = builder.Configuration.GetValue<int>("Ports:Admin", 8788);

// Kestrel 监听两个端口。Docker 部署时设 Kestrel:Host=0.0.0.0 让反代可达；本机默认 localhost。
var listenHost = builder.Configuration["Kestrel:Host"] ?? "localhost";
var ip = System.Net.IPAddress.Parse(listenHost == "0.0.0.0" ? "0.0.0.0" : "127.0.0.1");
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(ip, publicPort);
    k.Listen(ip, adminPort);
    k.Limits.MaxRequestBodySize = 64L * 1024 * 1024; // 大 Excel 上传
});

var app = builder.Build();

// 初始化数据库（建库 + 默认 admin + 导入样例）
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var seed = builder.Configuration["Database:SeedXlsx"];
    // 相对路径按内容根（项目目录）解析，绝对路径直接用
    string? seedPath = null;
    if (!string.IsNullOrWhiteSpace(seed))
        seedPath = Path.IsPathRooted(seed) ? seed : Path.GetFullPath(seed, builder.Environment.ContentRootPath);
    DbInitializer.Initialize(db, seedPath, out var initialPwd, app.Logger);
    app.Logger.LogInformation("数据库就绪：{Provider} {Target}", provider, provider == "postgres" ? "PostgreSQL" : fullDbPath);
    app.Logger.LogInformation("默认管理员账号：admin / {Pwd}（首次登录后请立即修改）", initialPwd);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// 端口守卫：用户端端口仅允许 GET 且仅限公开搜索接口；鉴权/管理接口只在管理端口可用
app.Use(async (ctx, next) =>
{
    if (ctx.Connection.LocalPort == publicPort)
    {
        var path = ctx.Request.Path.Value ?? "";
        var isPublicApi = path.StartsWith("/api/links", StringComparison.OrdinalIgnoreCase)
                          || path.StartsWith("/api/filters", StringComparison.OrdinalIgnoreCase)
                          || path.StartsWith("/api/libraries", StringComparison.OrdinalIgnoreCase);
        var isApi = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase);
        // 只读端口：非 GET 一律拒绝；API 仅放行公开搜索接口（防止 mfa/check 等枚举泄露）
        if (!HttpMethods.IsGet(ctx.Request.Method) || (isApi && !isPublicApi))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("只读端口：仅允许公开搜索的 GET 请求");
            return;
        }
    }
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

// ===== 托管 Blazor 静态站 =====
// 用户端口提供 web 站，管理端口提供 admin 站；/api 与 /swagger 等交给 Controller。
// 静态目录由配置 StaticFiles:UserDir / StaticFiles:AdminDir 指定（相对内容根或绝对路径）。
var userSite = builder.Configuration["StaticFiles:UserDir"] ?? "wwwroot-user";
var adminSite = builder.Configuration["StaticFiles:AdminDir"] ?? "wwwroot-admin";
string ResolveSite(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(p, builder.Environment.ContentRootPath);

app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "/";
    if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }
    // 非 API 请求：按端口选站点目录，返回静态文件或 index.html（SPA 回退）
    var isPublic = ctx.Connection.LocalPort == publicPort;
    var root = ResolveSite(isPublic ? userSite : adminSite);

    // 管理端：根路径返回纯静态登录页（秒开，验证码图片直出，不依赖 WASM）
    if (!isPublic && (path == "/" || path == "/login.html"))
    {
        var loginPage = Path.Combine(root, "login.html");
        if (System.IO.File.Exists(loginPage))
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            await ctx.Response.SendFileAsync(loginPage);
            return;
        }
    }

    // 管理端 Blazor 控制台挂在 /console/ 下：剥离前缀映射到站点目录
    var rel = path.TrimStart('/');
    if (!isPublic && rel.StartsWith("console/", StringComparison.OrdinalIgnoreCase))
        rel = rel["console/".Length..];
    else if (!isPublic && rel == "console")
        rel = "";
    if (!string.IsNullOrEmpty(rel) && !rel.EndsWith('/'))
    {
        var candidate = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        if (System.IO.File.Exists(candidate))
        {
            // Content-Type 始终按原始文件扩展名判定（不能按 .br/.gz）
            var ext = Path.GetExtension(candidate).ToLowerInvariant();
            var contentType = ext switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "application/javascript",
                ".css" => "text/css",
                ".wasm" => "application/wasm",
                ".json" => "application/json",
                ".dat" => "application/octet-stream",
                ".png" => "image/png",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };
            ctx.Response.ContentType = contentType;

            // 优先返回预压缩版本（Blazor 发布目录自带 .br/.gz），大幅减少下载体积
            var accept = ctx.Request.Headers.AcceptEncoding.ToString();
            string? serveFile = candidate;
            if (accept.Contains("br", StringComparison.OrdinalIgnoreCase)
                && System.IO.File.Exists(candidate + ".br"))
            {
                serveFile = candidate + ".br";
                ctx.Response.Headers.ContentEncoding = "br";
            }
            else if (accept.Contains("gzip", StringComparison.OrdinalIgnoreCase)
                     && System.IO.File.Exists(candidate + ".gz"))
            {
                serveFile = candidate + ".gz";
                ctx.Response.Headers.ContentEncoding = "gzip";
            }
            ctx.Response.Headers.Vary = "Accept-Encoding";
            // 带指纹的框架文件（_framework/）文件名随内容变化，可安全长缓存；
            // 刷新页面时浏览器直接用本地缓存，避免重复下载 ~8MB wasm。
            if (rel.StartsWith("_framework/", StringComparison.OrdinalIgnoreCase))
                ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            else if (ext == ".html" || ext == ".css")
                // HTML/CSS 文件名不含指纹，必须每次校验，否则发版后浏览器仍用旧样式
                ctx.Response.Headers.CacheControl = "no-cache";
            else
                ctx.Response.Headers.CacheControl = "public, max-age=86400";
            // wasm/js 需正确 MIME 才能被浏览器编译，压缩版同样保持原 Content-Type
            await ctx.Response.SendFileAsync(serveFile);
            return;
        }
    }
    var index = Path.Combine(root, "index.html");
    if (System.IO.File.Exists(index))
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache"; // index 必须实时，确保拿到最新固件引用
        await ctx.Response.SendFileAsync(index);
        return;
    }
    await next();
});

app.MapControllers();

app.Logger.LogInformation("用户端 http://localhost:{Public}（只读）", publicPort);
app.Logger.LogInformation("管理端 http://localhost:{Admin}（全权限）", adminPort);
app.Run();
