using System.Net.Http.Headers;
using Microsoft.JSInterop;

namespace Durlinks.AdminWeb.Services;

/// <summary>自动为管理端请求附带 JWT；从 localStorage 读取令牌。</summary>
public class AuthHandler : DelegatingHandler
{
    private readonly IJSRuntime _js;
    public AuthHandler(IJSRuntime js) => _js = js;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var token = await _js.InvokeAsync<string>("localStorage.getItem", "durlinks_token");
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        catch { /* 首次渲染 JS 未就绪，忽略 */ }
        return await base.SendAsync(request, ct);
    }
}
