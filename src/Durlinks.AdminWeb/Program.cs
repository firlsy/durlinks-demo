using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Durlinks.AdminWeb;
using Durlinks.AdminWeb.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// 管理网页与 API 同域名，/api 由反代转到 :8788
// Auth：带 JWT 的 client（需 JS 读 localStorage）
builder.Services.AddScoped<AuthHandler>();
builder.Services.AddScoped(sp =>
{
    var handler = sp.GetRequiredService<AuthHandler>();
    handler.InnerHandler = new HttpClientHandler();
    return new HttpClient(handler) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
});
// Anon：不带 JS 依赖的 client，专供验证码/MFA查询/登录（首屏即可用）
builder.Services.AddKeyedScoped<HttpClient>("Anon", (sp, key) =>
    new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<I18nService>();

await builder.Build().RunAsync();
