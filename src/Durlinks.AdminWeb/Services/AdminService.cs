using System.Net.Http.Json;
using Durlinks.Shared.DTOs;
using Microsoft.Extensions.DependencyInjection;

namespace Durlinks.AdminWeb.Services;

public class AdminService
{
    private readonly HttpClient _http;
    private readonly HttpClient _anon;
    public AdminService(HttpClient http, [FromKeyedServices("Anon")] HttpClient anon)
    { _http = http; _anon = anon; }

    public async Task<CaptchaDto?> GetCaptcha() =>
        await _anon.GetFromJsonAsync<CaptchaDto>("/api/auth/captcha");

    /// <summary>当前登录账号的 MFA 状态。</summary>
    public async Task<bool> GetMyMfaStatus()
    {
        try
        {
            var r = await _http.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/me");
            return r.TryGetProperty("mfaEnabled", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch { return false; }
    }

    /// <summary>开始启用 MFA，返回密钥与 otpauth URI。</summary>
    public async Task<MfaSetupDto?> SetupMfa()
    {
        var r = await _http.PostAsync("/api/auth/mfa/setup", null);
        return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<MfaSetupDto>() : null;
    }

    /// <summary>用动态码确认启用 MFA。</summary>
    public async Task<(bool ok, string? msg)> VerifyMfa(string code)
    {
        var r = await _http.PostAsJsonAsync("/api/auth/mfa/verify", new { code });
        if (r.IsSuccessStatusCode) return (true, null);
        return (false, await ReadMsg(r));
    }

    /// <summary>关闭 MFA（需动态码或密码）。</summary>
    public async Task<(bool ok, string? msg)> DisableMfa(string? code, string? password)
    {
        var r = await _http.PostAsJsonAsync("/api/auth/mfa/disable", new MfaDisableRequest { Code = code, Password = password });
        if (r.IsSuccessStatusCode) return (true, null);
        return (false, await ReadMsg(r));
    }

    /// <summary>修改自己的密码。</summary>
    public async Task<(bool ok, string? msg)> ChangePassword(string current, string next)
    {
        var r = await _http.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest { CurrentPassword = current, NewPassword = next });
        if (r.IsSuccessStatusCode) return (true, null);
        return (false, await ReadMsg(r));
    }

    private static async Task<string?> ReadMsg(HttpResponseMessage r)
    {
        try { return (await r.Content.ReadFromJsonAsync<Dictionary<string, string>>())?.GetValueOrDefault("message"); }
        catch { return null; }
    }

    public async Task<bool> CheckMfa(string username)
    {
        try
        {
            var r = await _anon.GetFromJsonAsync<MfaCheckDto>($"/api/auth/mfa/check?username={Uri.EscapeDataString(username)}");
            return r?.MfaEnabled ?? false;
        }
        catch { return false; }
    }

    public async Task<LoginResult> Login(string u, string p, string? totp, string captchaId, string captchaCode)
    {
        var r = await _anon.PostAsJsonAsync("/api/auth/login", new LoginRequest
        { Username = u, Password = p, TotpCode = totp, CaptchaId = captchaId, CaptchaCode = captchaCode });
        if (!r.IsSuccessStatusCode)
        {
            string? msg = null;
            try { msg = (await r.Content.ReadFromJsonAsync<Dictionary<string, string>>())?.GetValueOrDefault("message"); } catch { }
            return new LoginResult { Ok = false, Message = msg };
        }
        var res = await r.Content.ReadFromJsonAsync<LoginResponse>();
        return new LoginResult { Ok = true, Response = res };
    }

    public class LoginResult
    {
        public bool Ok { get; set; }
        public string? Message { get; set; }
        public LoginResponse? Response { get; set; }
    }

    public async Task<Dictionary<string, object>?> GetStats() =>
        await _http.GetFromJsonAsync<Dictionary<string, object>>("/api/admin/stats");

    public async Task<PagedResult<LinkDto>?> ListLinks(LinkQuery q)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(q.Q)) qs.Add($"q={Uri.EscapeDataString(q.Q)}");
        if (!string.IsNullOrWhiteSpace(q.Group)) qs.Add($"group={Uri.EscapeDataString(q.Group)}");
        if (!string.IsNullOrWhiteSpace(q.Tag)) qs.Add($"tag={Uri.EscapeDataString(q.Tag)}");
        if (q.ImportSource.HasValue) qs.Add($"importSource={(int)q.ImportSource.Value}");
        if (q.LibraryId.HasValue && q.LibraryId.Value > 0) qs.Add($"libraryId={q.LibraryId.Value}");
        qs.Add($"page={q.Page}"); qs.Add($"pageSize={q.PageSize}");
        return await _http.GetFromJsonAsync<PagedResult<LinkDto>>("/api/admin/links?" + string.Join("&", qs));
    }

    // ---------- 发布库 ----------
    public async Task<List<LibraryDto>> Libraries() =>
        await _http.GetFromJsonAsync<List<LibraryDto>>("/api/admin/libraries") ?? [];

    public async Task<(bool ok, string? msg)> CreateLibrary(LibrarySaveRequest req)
    {
        var r = await _http.PostAsJsonAsync("/api/admin/libraries", req);
        return r.IsSuccessStatusCode ? (true, null) : (false, await ReadMsg(r));
    }

    public async Task<(bool ok, string? msg)> UpdateLibrary(int id, LibrarySaveRequest req)
    {
        var r = await _http.PutAsJsonAsync($"/api/admin/libraries/{id}", req);
        return r.IsSuccessStatusCode ? (true, null) : (false, await ReadMsg(r));
    }

    /// <summary>删除发布库（只需当前账号密码二次确认）。</summary>
    public async Task<(bool ok, string? msg)> DeleteLibrary(int id, string password)
    {
        var r = await _http.PostAsJsonAsync($"/api/admin/libraries/{id}/delete", new { password });
        return r.IsSuccessStatusCode ? (true, null) : (false, await ReadMsg(r));
    }

    // ---------- 分类管理 ----------
    public async Task<CategoryOptionsDto> Categories() =>
        await _http.GetFromJsonAsync<CategoryOptionsDto>("/api/admin/categories") ?? new();

    public async Task<(bool ok, string? msg)> CreateCategory(string kind, string name)
    {
        var r = await _http.PostAsJsonAsync($"/api/admin/categories/{Uri.EscapeDataString(kind)}", new CategoryNameRequest { Name = name });
        return r.IsSuccessStatusCode ? (true, null) : (false, await ReadMsg(r));
    }

    public async Task<(bool ok, string? msg)> RenameCategory(string kind, string oldName, string newName)
    {
        var r = await _http.PutAsJsonAsync($"/api/admin/categories/{Uri.EscapeDataString(kind)}", new CategoryRenameRequest { OldName = oldName, NewName = newName });
        return r.IsSuccessStatusCode ? (true, null) : (false, await ReadMsg(r));
    }

    public async Task<(bool ok, string? msg)> DeleteCategory(string kind, string name)
    {
        var r = await _http.DeleteAsync($"/api/admin/categories/{Uri.EscapeDataString(kind)}?name={Uri.EscapeDataString(name)}");
        return r.IsSuccessStatusCode ? (true, null) : (false, await ReadMsg(r));
    }

    public async Task<(CategoryBatchDeleteResult? result, string? msg)> BatchDeleteCategory(string kind, IEnumerable<string> names)
    {
        var r = await _http.PostAsJsonAsync($"/api/admin/categories/{Uri.EscapeDataString(kind)}/batch-delete",
            new CategoryBatchDeleteRequest { Names = names.ToList() });
        if (!r.IsSuccessStatusCode) return (null, await ReadMsg(r));
        return (await r.Content.ReadFromJsonAsync<CategoryBatchDeleteResult>() ?? new(), null);
    }

    // ---------- 链接新建/编辑 ----------
    public async Task<(bool ok, string? msg)> CreateLink(LinkCreateRequest req)
    {
        var r = await _http.PostAsJsonAsync("/api/admin/links", req);
        return r.IsSuccessStatusCode ? (true, null) : (false, await ReadMsg(r));
    }

    public async Task<(bool ok, string? msg)> UpdateLink(int id, LinkUpdateRequest req)
    {
        var r = await _http.PutAsJsonAsync($"/api/admin/links/{id}", req);
        return r.IsSuccessStatusCode ? (true, null) : (false, await ReadMsg(r));
    }

    /// <summary>移动链接到目标发布库。</summary>
    public async Task<(bool ok, string? msg, int count)> MoveLinks(List<int> ids, int targetLibraryId)
    {
        var r = await _http.PostAsJsonAsync("/api/admin/links/move", new MoveCopyLinksRequest { Ids = ids, TargetLibraryId = targetLibraryId });
        if (!r.IsSuccessStatusCode) return (false, await ReadMsg(r), 0);
        var n = 0;
        try
        {
            var d = await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            if (d.TryGetProperty("moved", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number) n = v.GetInt32();
        }
        catch { }
        return (true, null, n);
    }

    /// <summary>复制链接到目标发布库。</summary>
    public async Task<(bool ok, string? msg, int count)> CopyLinks(List<int> ids, int targetLibraryId)
    {
        var r = await _http.PostAsJsonAsync("/api/admin/links/copy", new MoveCopyLinksRequest { Ids = ids, TargetLibraryId = targetLibraryId });
        if (!r.IsSuccessStatusCode) return (false, await ReadMsg(r), 0);
        var n = 0;
        try
        {
            var d = await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            if (d.TryGetProperty("copied", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number) n = v.GetInt32();
        }
        catch { }
        return (true, null, n);
    }

    public async Task SetHidden(int id, bool hidden) =>
        await _http.PatchAsJsonAsync($"/api/admin/links/{id}/hidden", new { hidden });

    public async Task SoftDelete(int id) =>
        await _http.DeleteAsync($"/api/admin/links/{id}");

    public async Task BatchHidden(List<int> ids, bool hidden) =>
        await _http.PatchAsJsonAsync("/api/admin/links/batch/hidden", new { ids, hidden });

    public async Task BatchDelete(List<int> ids)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, "/api/admin/links/batch")
        { Content = JsonContent.Create(new { ids }) };
        await _http.SendAsync(req);
    }

    public async Task<DedupeResult?> DedupeLinks(DedupeRequest req)
    {
        var resp = await _http.PostAsJsonAsync("/api/admin/links/dedupe", req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<DedupeResult>();
    }

    public async Task<List<UserDto>?> Users() =>
        await _http.GetFromJsonAsync<List<UserDto>>("/api/auth/users");

    public async Task CreateUser(string u, string p) =>
        await _http.PostAsJsonAsync("/api/auth/users", new { username = u, password = p });

    public async Task<LlmConfigDto?> GetLlm() =>
        await _http.GetFromJsonAsync<LlmConfigDto>("/api/admin/llm/config");

    /// <summary>全部模型服务配置（多站点/多模型）。</summary>
    public async Task<List<LlmConfigDto>> ListLlmConfigs()
    {
        try { return await _http.GetFromJsonAsync<List<LlmConfigDto>>("/api/admin/llm/configs") ?? []; }
        catch { return []; }
    }

    /// <summary>新建或更新一条模型服务配置。</summary>
    public async Task<(bool ok, string msg)> SaveLlmConfig(LlmConfigDto dto)
    {
        var r = await _http.PostAsJsonAsync("/api/admin/llm/configs", dto);
        return (r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
    }

    public async Task DeleteLlmConfig(int id)
    {
        await _http.DeleteAsync($"/api/admin/llm/configs/{id}");
    }

    public async Task SetDefaultLlmConfig(int id)
    {
        await _http.PostAsync($"/api/admin/llm/configs/{id}/default", null);
    }

    /// <summary>切换站点当前使用的模型。</summary>
    public async Task<(bool ok, string msg)> SetLlmModel(int id, string model)
    {
        var r = await _http.PostAsJsonAsync($"/api/admin/llm/configs/{id}/model", new { model });
        return (r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
    }

    /// <summary>获取站点可用模型清单（OpenAI / Anthropic / Gemini）。</summary>
    public async Task<LlmModelsResult?> ListLlmModels(LlmConfigDto dto)
    {
        try
        {
            var r = await _http.PostAsJsonAsync("/api/admin/llm/models", dto);
            if (!r.IsSuccessStatusCode)
                return new LlmModelsResult { Ok = false, Message = $"请求失败 HTTP {(int)r.StatusCode}" };
            return await r.Content.ReadFromJsonAsync<LlmModelsResult>();
        }
        catch (Exception ex)
        {
            return new LlmModelsResult { Ok = false, Message = ex.Message };
        }
    }

    /// <summary>测试 LLM 连接，返回 (是否成功, 提示, 耗时ms)。</summary>
    public async Task<(bool ok, string message, long ms)> TestLlm(LlmConfigDto dto)
    {
        try
        {
            var r = await _http.PostAsJsonAsync("/api/admin/llm/test", dto);
            if (!r.IsSuccessStatusCode) return (false, $"请求失败 HTTP {(int)r.StatusCode}", 0);
            var d = await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var ok = d.TryGetProperty("ok", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.True;
            var msg = d.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var ms = d.TryGetProperty("latencyMs", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.Number ? l.GetInt64() : 0;
            return (ok, msg, ms);
        }
        catch (Exception ex) { return (false, ex.Message, 0); }
    }

    public async Task SaveLlm(LlmConfigDto dto) =>
        await _http.PutAsJsonAsync("/api/admin/llm/config", dto);

    public async Task<string?> ParseLinks(string text)
    {
        var r = await _http.PostAsJsonAsync("/api/admin/import/links/parse", new { text });
        return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync() : null;
    }

    public async Task<(bool ok, string body)> ImportExcel(MultipartFormDataContent content, int? libraryId)
    {
        var url = "/api/admin/import/excel" + (libraryId is > 0 ? $"?libraryId={libraryId}" : "");
        var r = await _http.PostAsync(url, content);
        return (r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
    }

    /// <summary>上传 Excel 并启动后台导入任务（大表不阻塞请求）。</summary>
    public async Task<(bool ok, string body)> StartExcelImport(MultipartFormDataContent content, int? libraryId)
    {
        var url = "/api/admin/import/excel/jobs" + (libraryId is > 0 ? $"?libraryId={libraryId}" : "");
        var r = await _http.PostAsync(url, content);
        return (r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
    }

    public async Task<ExcelImportStatus?> ExcelImportStatus()
    {
        try { return await _http.GetFromJsonAsync<ExcelImportStatus>("/api/admin/import/excel/jobs/status"); }
        catch { return null; }
    }

    public async Task CancelExcelImport()
    {
        await _http.PostAsync("/api/admin/import/excel/jobs/cancel", null);
    }

    public async Task<(bool ok, string body)> ImportLinksRaw(object items, int? libraryId)
    {
        var url = "/api/admin/import/links" + (libraryId is > 0 ? $"?libraryId={libraryId}" : "");
        var r = await _http.PostAsJsonAsync(url, items);
        return (r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
    }

    public async Task<string?> Enrich(int id)
    {
        var r = await _http.PostAsync($"/api/admin/ai/enrich/{id}", null);
        return await r.Content.ReadAsStringAsync();
    }

    // ---- 批量 AI 补料 ----
    public async Task<(bool ok, string msg)> StartBatchEnrich(BatchEnrichRequest req)
    {
        var r = await _http.PostAsJsonAsync("/api/admin/ai/enrich/batch", req);
        var body = await r.Content.ReadAsStringAsync();
        return (r.IsSuccessStatusCode, body);
    }

    public async Task<BatchEnrichStatus?> GetBatchEnrichStatus()
    {
        try { return await _http.GetFromJsonAsync<BatchEnrichStatus>("/api/admin/ai/enrich/batch/status"); }
        catch { return null; }
    }

    public async Task CancelBatchEnrich()
    {
        await _http.PostAsync("/api/admin/ai/enrich/batch/cancel", null);
    }
}
