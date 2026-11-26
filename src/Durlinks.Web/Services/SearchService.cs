using System.Net.Http.Json;
using Durlinks.Shared.DTOs;

namespace Durlinks.Web.Services;

/// <summary>用户端搜索服务：调用当前域名 /api（反代到 :8789）。</summary>
public class SearchService
{
    private readonly HttpClient _http;
    public SearchService(HttpClient http) => _http = http;

    public async Task<PagedResult<LinkDto>?> SearchAsync(LinkQuery q)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(q.Q)) qs.Add($"q={Uri.EscapeDataString(q.Q)}");
        if (!string.IsNullOrWhiteSpace(q.Group)) qs.Add($"group={Uri.EscapeDataString(q.Group)}");
        if (!string.IsNullOrWhiteSpace(q.Tag)) qs.Add($"tag={Uri.EscapeDataString(q.Tag)}");
        if (!string.IsNullOrWhiteSpace(q.Platform)) qs.Add($"platform={Uri.EscapeDataString(q.Platform)}");
        if (q.ImportSource.HasValue) qs.Add($"importSource={(int)q.ImportSource.Value}");
        if (q.LibraryId.HasValue && q.LibraryId.Value > 0) qs.Add($"libraryId={q.LibraryId.Value}");
        qs.Add($"channel={Uri.EscapeDataString(string.IsNullOrWhiteSpace(q.Channel) ? "web" : q.Channel)}");
        qs.Add($"page={q.Page}");
        qs.Add($"pageSize={q.PageSize}");
        var url = "api/links?" + string.Join("&", qs);
        return await _http.GetFromJsonAsync<PagedResult<LinkDto>>(url);
    }

    public async Task<FilterOptionsDto?> GetFiltersAsync(string channel = "web") =>
        await _http.GetFromJsonAsync<FilterOptionsDto>($"api/filters?channel={Uri.EscapeDataString(channel)}");

    /// <summary>当前渠道可见的发布库。</summary>
    public async Task<List<LibraryDto>> GetLibrariesAsync(string channel = "web") =>
        await _http.GetFromJsonAsync<List<LibraryDto>>($"api/libraries?channel={Uri.EscapeDataString(channel)}") ?? [];
}
