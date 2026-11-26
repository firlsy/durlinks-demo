using System.Text;
using System.Text.Json;
using Durlinks.Server.Data;
using Durlinks.Shared.DTOs;
using Durlinks.Shared.Entities;

namespace Durlinks.Server.Services;

/// <summary>
/// 调用 LLM：识别链接信息 / 生成标签与摘要。
/// 支持三种协议：openai（含所有 OpenAI 兼容中转站）、anthropic、gemini。
/// 失败时优雅降级，不阻塞导入。
/// </summary>
public class LlmService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LlmService> _log;

    public LlmService(AppDbContext db, IHttpClientFactory httpFactory, ILogger<LlmService> log)
    {
        _db = db; _httpFactory = httpFactory; _log = log;
    }

    public class LinkInfo
    {
        public string? Title { get; set; }
        public string? LinkType { get; set; }
        public string? GroupName { get; set; }
        public string? Platform { get; set; }
        public List<string> Tags { get; set; } = [];
        public string? Summary { get; set; }
        public bool Recognized { get; set; }
    }

    public class EnrichResult
    {
        public List<string> Tags { get; set; } = [];
        public string? Summary { get; set; }
    }

    /// <summary>识别一条链接（"链接 + 可选备注"文本），尽可能识别，失败返回未识别的空结果。</summary>
    public async Task<LinkInfo> RecognizeLinkAsync(string rawText)
    {
        var fallback = new LinkInfo { Recognized = false };
        var cfg = GetConfig();
        if (cfg is null) return fallback;

        var schema = "{\"title\":\"标题\",\"linkType\":\"下载直链|官网/普通网址|网盘|磁力\",\"groupName\":\"分类\",\"platform\":\"平台(Windows/Android/iOS/Mac/Linux/Web/跨平台等,不确定留空)\",\"tags\":[\"标签1\",\"标签2\"],\"summary\":\"一句话摘要\"}";
        var prompt = "你是链接信息整理助手。根据下面的文本，输出 JSON（只输出 JSON，不要解释）：\n"
                     + schema + "\n文本：\n" + rawText
                     + "\n规则：无法确定的字段留空字符串，tags 最多 5 个。若文本里没有任何可判断信息，title 用 URL 域名。";

        var json = await ChatAsync(cfg, prompt, "你是信息抽取助手，只输出合法 JSON。");
        if (json is null) return fallback;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var r = new LinkInfo
            {
                Title = GetStr(root, "title"),
                LinkType = GetStr(root, "linkType"),
                GroupName = GetStr(root, "groupName"),
                Platform = GetStr(root, "platform"),
                Summary = GetStr(root, "summary"),
                Recognized = true
            };
            if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                foreach (var t in tags.EnumerateArray())
                    if (t.ValueKind == JsonValueKind.String) r.Tags.Add(t.GetString()!);
            return r;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LLM 识别结果解析失败，按未识别处理");
            return fallback;
        }
    }

    /// <summary>为一条链接生成标签与摘要（用于人工导入后的补料）。</summary>
    public async Task<EnrichResult> EnrichAsync(string title, string url, string? remark)
    {
        var cfg = GetConfig();
        if (cfg is null) return new EnrichResult();
        return await EnrichAsync(cfg, title, url, remark);
    }

    /// <summary>
    /// 复用已读取的配置生成标签与摘要。批量补料时每条都查一次配置会造成 N 次多余查询，
    /// 由调用方读一次后传入本重载。
    /// </summary>
    public async Task<EnrichResult> EnrichAsync(LlmConfig cfg, string title, string url, string? remark)
    {
        var result = new EnrichResult();
        if (cfg is null || !cfg.IsConfigured) return result;

        var schema = "{\"tags\":[\"标签1\",\"标签2\",\"标签3\"],\"summary\":\"一句话摘要\"}";
        var prompt = "为下面的链接生成标签和一句话摘要，输出 JSON（只输出 JSON）：\n"
                     + schema + "\n名称：" + title + "\n链接：" + url + "\n备注：" + (remark ?? "")
                     + "\n标签最多5个，摘要不超过50字。";
        var json = await ChatAsync(cfg, prompt, "你是信息整理助手，只输出合法 JSON。");
        if (json is null) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            result.Summary = GetStr(root, "summary");
            if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                foreach (var t in tags.EnumerateArray())
                    if (t.ValueKind == JsonValueKind.String) result.Tags.Add(t.GetString()!);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LLM enrich 结果解析失败");
        }
        return result;
    }

    /// <summary>取默认可用配置：优先 IsDefault，其次最早创建的一条。</summary>
    private LlmConfig? GetConfig() =>
        _db.LlmConfigs.Where(x => x.IsConfigured)
            .OrderByDescending(x => x.IsDefault).ThenBy(x => x.Id)
            .FirstOrDefault();

    /// <summary>测试 LLM 连接：发一个最小请求，返回是否可用、耗时与具体错误（供设置页"测试连接"使用）。</summary>
    public async Task<(bool ok, string message, long latencyMs)> TestAsync(
        string endpoint, string apiKey, string model, string protocol = "openai",
        string retryPolicy = "default", int maxRetries = 2, IEnumerable<string>? retryOn = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
                return (false, "请填写 API 地址和模型名", 0);
            if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return (false, "API 地址必须是 http/https 开头的完整 URL", 0);
            if (string.IsNullOrWhiteSpace(apiKey))
                return (false, "请填写 API Key", 0);

            var cfg = new LlmConfig
            {
                ApiEndpoint = endpoint.Trim(), ApiKey = apiKey.Trim(),
                Model = model.Trim(), Protocol = NormalizeProtocol(protocol), IsConfigured = true,
                RetryPolicy = string.IsNullOrWhiteSpace(retryPolicy) ? "default" : retryPolicy.Trim().ToLowerInvariant(),
                MaxRetries = Math.Clamp(maxRetries, 0, 10),
                RetryOn = string.Join(',', (retryOn ?? DefaultRetryOn)
                    .Select(x => (x ?? "").Trim().ToLowerInvariant())
                    .Where(x => x.Length > 0).Distinct())
            };
            // 测试连接只重试 2 次，避免用户等太久
            var (ok, _, error, _) = await SendWithRetryAsync(cfg, "只回复 OK", "ping", 16, 20,
                jsonMode: false, ct: default, retryOverride: 2);
            sw.Stop();
            if (ok) return (true, $"连接成功（{cfg.Protocol} · 模型 {cfg.Model} 响应正常）", sw.ElapsedMilliseconds);

            var hint = error ?? "请求失败";
            if (hint.Contains("401") || hint.Contains("403")) hint = "API Key 无效或无权限（" + hint + "）";
            else if (hint.Contains("404")) hint = "接口路径不存在，请检查 Base URL 与协议是否匹配（" + hint + "）";
            else if (hint.Contains("429")) hint = "请求过于频繁或额度不足（" + hint + "）";
            return (false, hint, sw.ElapsedMilliseconds);
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            return (false, "连接超时（20 秒），请检查地址或网络", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, $"连接失败：{ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 拉取站点可用模型清单：OpenAI 兼容 / Anthropic / Gemini 三种协议的 /models 端点各不相同。
    /// 只读操作，失败不影响已保存配置。
    /// </summary>
    public async Task<LlmModelsResult> ListModelsAsync(string endpoint, string apiKey, string protocol)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new LlmModelsResult();
        try
        {
            var p = NormalizeProtocol(protocol);
            var baseUrl = (endpoint ?? string.Empty).Trim().TrimEnd('/');
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                result.Message = "API 地址必须是 http/https 开头的完整 URL";
                return result;
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                result.Message = "请填写 API Key";
                return result;
            }

            // 本地模型（Ollama 等）绕过系统代理
            var isLoopback = uri.Host is "localhost" or "127.0.0.1" or "::1"
                             || uri.Host.StartsWith("127.", StringComparison.Ordinal);
            var client = _httpFactory.CreateClient(isLoopback ? "llm-direct" : string.Empty);
            client.Timeout = TimeSpan.FromSeconds(20);

            HttpRequestMessage req;
            if (p == "anthropic")
            {
                var url = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                    ? baseUrl + "/models" : baseUrl + "/v1/models";
                req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
            else if (p == "gemini")
            {
                var root = baseUrl.Contains("/v1beta", StringComparison.OrdinalIgnoreCase)
                    ? baseUrl : baseUrl + "/v1beta";
                req = new HttpRequestMessage(HttpMethod.Get,
                    root + "/models?key=" + Uri.EscapeDataString(apiKey));
            }
            else
            {
                var url = baseUrl.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
                    ? baseUrl : baseUrl + "/models";
                req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            using (req)
            {
                using var resp = await client.SendAsync(req);
                var raw = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    var hint = (int)resp.StatusCode switch
                    {
                        401 or 403 => "API Key 无效或无权限",
                        404 => "接口路径不存在，请检查 Base URL 与协议是否匹配",
                        429 => "请求过于频繁或额度不足",
                        >= 500 => "服务端错误，稍后重试",
                        _ => "请求被拒绝"
                    };
                    result.Message = $"{hint}（HTTP {(int)resp.StatusCode}）{Truncate(raw, 160)}";
                    return result;
                }

                using var doc = JsonDocument.Parse(raw);
                var rootEl = doc.RootElement;
                var list = new List<LlmModelDto>();
                if (p == "anthropic")
                {
                    if (rootEl.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var m in arr.EnumerateArray())
                        {
                            var id = GetStr(m, "id");
                            if (!string.IsNullOrWhiteSpace(id))
                                list.Add(new LlmModelDto { Id = id!.Trim(), DisplayName = GetStr(m, "display_name") });
                        }
                }
                else if (p == "gemini")
                {
                    if (rootEl.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var m in arr.EnumerateArray())
                        {
                            var name = GetStr(m, "name");
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            var id = name!.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? name[7..] : name;
                            list.Add(new LlmModelDto { Id = id, DisplayName = GetStr(m, "displayName") });
                        }
                }
                else
                {
                    if (rootEl.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var m in arr.EnumerateArray())
                        {
                            var id = GetStr(m, "id");
                            if (!string.IsNullOrWhiteSpace(id)) list.Add(new LlmModelDto { Id = id!.Trim() });
                        }
                }

                result.Models = list
                    .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                    .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .Take(500)
                    .ToList();
                result.Ok = true;
                result.Message = result.Models.Count == 0
                    ? "站点没有返回模型（可能需要在后台开通模型权限）"
                    : $"获取到 {result.Models.Count} 个模型";
                return result;
            }
        }
        catch (TaskCanceledException)
        {
            result.Message = "获取超时（20 秒），请检查地址或网络";
            return result;
        }
        catch (Exception ex)
        {
            result.Message = "获取失败：" + ex.Message;
            return result;
        }
        finally
        {
            sw.Stop();
            result.LatencyMs = sw.ElapsedMilliseconds;
        }
    }

    private static string NormalizeProtocol(string? protocol)
    {
        var p = (protocol ?? "openai").Trim().ToLowerInvariant();
        return p is "anthropic" or "claude" ? "anthropic"
             : p is "gemini" or "google" ? "gemini"
             : p is "openai-responses" or "responses" or "openai_responses" ? "openai-responses"
             : "openai";
    }

    private async Task<string?> ChatAsync(LlmConfig cfg, string userPrompt, string systemPrompt)
    {
        var (ok, text, error, _) = await SendWithRetryAsync(cfg, systemPrompt, userPrompt, 800, 30, jsonMode: true, ct: default);
        if (!ok)
        {
            if (error is not null)
                _log.LogWarning("LLM 调用失败（{Protocol}）：{Error}", cfg.Protocol, error);
            return null;
        }
        return text; // 重试层已做 JSON 校验与代码块剥离
    }

    private enum FailKind { None, Empty, Format, Quota, RateLimit, Server, Timeout, Network, Auth, NotFound, Other }

    private static readonly string[] DefaultRetryOn = ["empty", "rate_limit", "server", "timeout", "network"];

    private static string RetryKey(FailKind k) => k switch
    {
        FailKind.Empty => "empty",
        FailKind.Format => "format",
        FailKind.Quota => "quota",
        FailKind.RateLimit => "rate_limit",
        FailKind.Server => "server",
        FailKind.Timeout => "timeout",
        FailKind.Network => "network",
        _ => "other"
    };

    /// <summary>是否重试：all=所有错误；default=内置临时错误集合；transient=按用户勾选集合。</summary>
    private static bool ShouldRetry(LlmConfig cfg, FailKind kind)
    {
        if (kind == FailKind.None) return false;
        var policy = (cfg.RetryPolicy ?? "default").Trim().ToLowerInvariant();
        if (policy == "all") return true;
        if (policy is "default" or "")
            return DefaultRetryOn.Contains(RetryKey(kind));

        var set = (cfg.RetryOn ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return set.Contains(RetryKey(kind), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>带重试的发送：HTTP 失败按策略重试；jsonMode 下「响应不是合法 JSON」也算可重试错误。</summary>
    private async Task<(bool ok, string? text, string? error, FailKind kind)> SendWithRetryAsync(
        LlmConfig cfg, string systemPrompt, string userPrompt,
        int maxTokens, int timeoutSeconds, bool jsonMode, CancellationToken ct, int? retryOverride = null)
    {
        var maxRetries = Math.Clamp(retryOverride ?? cfg.MaxRetries, 0, 10);
        for (var attempt = 0; ; attempt++)
        {
            var (ok, raw, error, kind) = await SendOnceAsync(cfg, systemPrompt, userPrompt,
                maxTokens, timeoutSeconds, jsonMode, ct);

            if (ok && jsonMode)
            {
                var cleaned = CleanJson(raw);
                if (cleaned is null || !IsJson(cleaned))
                {
                    ok = false;
                    kind = FailKind.Format;
                    error = "响应不是合法 JSON";
                }
                else
                {
                    return (true, cleaned, null, FailKind.None);
                }
            }
            if (ok) return (true, raw, null, FailKind.None);

            if (attempt < maxRetries && ShouldRetry(cfg, kind))
            {
                var delay = (int)Math.Min(4000, 300 * Math.Pow(2, attempt)) + Random.Shared.Next(0, 150);
                _log.LogInformation("LLM 重试 {Attempt}/{Max}：{Kind} · {Error}",
                    attempt + 1, maxRetries, kind, error);
                await Task.Delay(delay, ct);
                continue;
            }
            return (false, raw, error, kind);
        }
    }

    private static bool IsJson(string s)
    {
        try { using var _ = JsonDocument.Parse(s); return true; }
        catch { return false; }
    }

    /// <summary>按协议发送一次请求并取出文本（不重试）。</summary>
    private async Task<(bool ok, string? text, string? error, FailKind kind)> SendOnceAsync(
        LlmConfig cfg, string systemPrompt, string userPrompt,
        int maxTokens, int timeoutSeconds, bool jsonMode, CancellationToken ct)
    {
        try
        {
            var protocol = NormalizeProtocol(cfg.Protocol);
            // 回环地址（本地 Ollama 等）不走系统代理
            var endpointHost = Uri.TryCreate((cfg.ApiEndpoint ?? "").Trim(), UriKind.Absolute, out var epUri) ? epUri.Host : "";
            var isLoopback = endpointHost is "localhost" or "127.0.0.1" or "::1" or "[::1]"
                             || endpointHost.StartsWith("127.", StringComparison.Ordinal);
            var client = _httpFactory.CreateClient(isLoopback ? "llm-direct" : string.Empty);
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

            var baseUrl = (cfg.ApiEndpoint ?? string.Empty).Trim().TrimEnd('/');
            HttpRequestMessage req;
            if (protocol == "anthropic")
            {
                var url = baseUrl.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) ? baseUrl
                    : baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl + "/messages"
                    : baseUrl + "/v1/messages";
                var body = new
                {
                    model = cfg.Model,
                    max_tokens = maxTokens,
                    system = systemPrompt,
                    messages = new[] { new { role = "user", content = userPrompt } }
                };
                req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                };
                req.Headers.TryAddWithoutValidation("x-api-key", cfg.ApiKey);
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
            else if (protocol == "gemini")
            {
                var url = baseUrl.Contains("/v1beta", StringComparison.OrdinalIgnoreCase)
                    ? $"{baseUrl}/models/{cfg.Model}:generateContent?key={Uri.EscapeDataString(cfg.ApiKey)}"
                    : $"{baseUrl}/v1beta/models/{cfg.Model}:generateContent?key={Uri.EscapeDataString(cfg.ApiKey)}";
                var body = new
                {
                    systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                    contents = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
                    generationConfig = new { maxOutputTokens = maxTokens, temperature = 0.3 }
                };
                req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                };
            }
            else if (protocol == "openai-responses")
            {
                var url = baseUrl.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)
                    ? baseUrl : baseUrl + "/responses";
                object body = jsonMode
                    ? new
                    {
                        model = cfg.Model,
                        instructions = systemPrompt,
                        input = new[]
                        {
                            new { role = "user", content = new[] { new { type = "input_text", text = userPrompt } } }
                        },
                        max_output_tokens = maxTokens,
                        temperature = 0.3,
                        text = new { format = new { type = "json_object" } }
                    }
                    : new
                    {
                        model = cfg.Model,
                        instructions = systemPrompt,
                        input = new[]
                        {
                            new { role = "user", content = new[] { new { type = "input_text", text = userPrompt } } }
                        },
                        max_output_tokens = maxTokens,
                        temperature = 0.3
                    };
                req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                };
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            }
            else
            {
                var url = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                    ? baseUrl : baseUrl + "/chat/completions";
                object body = jsonMode
                    ? new
                    {
                        model = cfg.Model,
                        messages = new[]
                        {
                            new { role = "system", content = systemPrompt },
                            new { role = "user", content = userPrompt }
                        },
                        temperature = 0.3,
                        max_tokens = maxTokens,
                        response_format = new { type = "json_object" }
                    }
                    : new
                    {
                        model = cfg.Model,
                        messages = new[]
                        {
                            new { role = "system", content = systemPrompt },
                            new { role = "user", content = userPrompt }
                        },
                        temperature = 0.3,
                        max_tokens = maxTokens
                    };
                req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                };
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            }

            using (req)
            {
                using var resp = await client.SendAsync(req, ct);
                var raw = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var kind = Classify((int)resp.StatusCode, raw);
                    return (false, null, $"HTTP {(int)resp.StatusCode} {Truncate(raw, 180)}", kind);
                }

                using var doc = JsonDocument.Parse(raw);
                var text = protocol switch
                {
                    "anthropic" => doc.RootElement.TryGetProperty("content", out var content)
                                   && content.ValueKind == JsonValueKind.Array
                                   && content.GetArrayLength() > 0
                                   && content[0].TryGetProperty("text", out var t1) ? t1.GetString() : null,
                    "gemini" => doc.RootElement.TryGetProperty("candidates", out var cand)
                                && cand.ValueKind == JsonValueKind.Array
                                && cand.GetArrayLength() > 0
                                && cand[0].TryGetProperty("content", out var gc)
                                && gc.TryGetProperty("parts", out var parts)
                                && parts.ValueKind == JsonValueKind.Array
                                && parts.GetArrayLength() > 0
                                && parts[0].TryGetProperty("text", out var t2) ? t2.GetString() : null,
                    "openai-responses" => ParseResponsesText(doc.RootElement),
                    _ => doc.RootElement.TryGetProperty("choices", out var choices)
                         && choices.ValueKind == JsonValueKind.Array
                         && choices.GetArrayLength() > 0
                         && choices[0].TryGetProperty("message", out var msg)
                         && msg.TryGetProperty("content", out var t3) ? t3.GetString() : null
                };
                return string.IsNullOrWhiteSpace(text)
                    ? (false, null, "响应中没有可解析的文本内容", FailKind.Empty)
                    : (true, text, null, FailKind.None);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return (false, null, "请求超时：" + ex.Message, FailKind.Timeout);
        }
        catch (HttpRequestException ex)
        {
            return (false, null, "网络错误：" + ex.Message, FailKind.Network);
        }
        catch (JsonException ex)
        {
            return (false, null, "响应格式错误：" + ex.Message, FailKind.Format);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LLM 调用异常（{Protocol}）", cfg.Protocol);
            return (false, null, ex.Message, FailKind.Other);
        }
    }

    /// <summary>HTTP 状态码归类；429 进一步区分「配额」与「限流」。</summary>
    private static FailKind Classify(int status, string body)
    {
        if (status is 401 or 403) return FailKind.Auth;
        if (status == 402) return FailKind.Quota;
        if (status == 404) return FailKind.NotFound;
        if (status == 429)
        {
            var b = body.ToLowerInvariant();
            return b.Contains("quota") || b.Contains("insufficient") || b.Contains("exceeded")
                ? FailKind.Quota : FailKind.RateLimit;
        }
        if (status >= 500) return FailKind.Server;
        return FailKind.Other;
    }

    /// <summary>OpenAI Responses API 的文本在 output[].content[].text（type=output_text）。</summary>
    private static string? ParseResponsesText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var ot) && ot.ValueKind == JsonValueKind.String)
            return ot.GetString();
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return null;

        var sb = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var c in content.EnumerateArray())
            {
                if (c.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                    sb.Append(tx.GetString());
            }
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static string? CleanJson(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        // 兼容 ```json 包裹，以及模型在 JSON 前后加说明文字
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start >= 0 && end > start) return s[start..(end + 1)];
        return s;
    }

    private static string? GetStr(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
