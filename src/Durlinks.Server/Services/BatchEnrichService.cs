using Durlinks.Server.Data;
using Durlinks.Shared.DTOs;
using Durlinks.Shared.Entities;
using Microsoft.EntityFrameworkCore;

namespace Durlinks.Server.Services;

/// <summary>
/// 批量 AI 补料：遍历未补料链接，并发调用 LlmService.EnrichAsync 生成标签/摘要，
/// 串行回写数据库（避开 SQLite 单写者 + Tag 唯一索引冲突），提供进度查询与取消。
/// 单例服务，所有 DbContext/LlmService 通过 IServiceScopeFactory 按需创建，绝不跨线程共享。
/// </summary>
public class BatchEnrichService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<BatchEnrichService> _log;
    private readonly IConfiguration _cfg;

    private readonly object _statusLock = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1); // 串行化 DB 回写（异步等待，不占线程）
    private readonly object _rateLock = new();            // 全局最小间隔限流
    private CancellationTokenSource? _cts;
    private BatchEnrichStatus _status = new();
    private DateTime _lastCallAt = DateTime.MinValue;

    public BatchEnrichService(IServiceScopeFactory scopes, ILogger<BatchEnrichService> log, IConfiguration cfg)
    {
        _scopes = scopes; _log = log; _cfg = cfg;
    }

    /// <summary>返回当前进度的副本（避免外部改动内部状态）。</summary>
    public BatchEnrichStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new BatchEnrichStatus
            {
                Running = _status.Running, Mode = _status.Mode, Total = _status.Total,
                Processed = _status.Processed, Succeeded = _status.Succeeded, Failed = _status.Failed,
                Skipped = _status.Skipped, Message = _status.Message,
                StartedAt = _status.StartedAt, FinishedAt = _status.FinishedAt,
                CancelRequested = _status.CancelRequested
            };
        }
    }

    /// <summary>启动任务；已有任务在跑时返回 false，不重复启动。</summary>
    public bool Start(BatchEnrichRequest req)
    {
        lock (_statusLock)
        {
            if (_status.Running) return false;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var mode = req.Mode == "all" ? "all" : "missing";
            _status = new BatchEnrichStatus
            {
                Running = true, Mode = mode, Message = "准备中…", StartedAt = DateTime.UtcNow
            };
        }
        var token = _cts.Token;
        _ = Task.Run(() => RunAsync(req, token));
        return true;
    }

    /// <summary>请求取消（协作式，当前请求结束后停止）。</summary>
    public bool Cancel()
    {
        lock (_statusLock)
        {
            if (!_status.Running || _cts is null) return false;
            _status.CancelRequested = true;
            _status.Message = "正在取消…";
            _cts.Cancel();
            return true;
        }
    }

    private async Task RunAsync(BatchEnrichRequest req, CancellationToken ct)
    {
        var concurrency = Math.Clamp(req.Concurrency <= 0 ? 3 : req.Concurrency, 1, 8);
        var maxItems = Math.Clamp(_cfg.GetValue<int>("BatchEnrich:MaxItems", 200000), 1, 500000);
        var minIntervalMs = Math.Clamp(_cfg.GetValue<int>("BatchEnrich:MinIntervalMs", 100), 0, 60000);
        // Limit<=0 视为未指定：用安全默认 100，绝不理解为"全量"，避免误触烧掉大量额度
        var requested = req.Limit <= 0 ? 100 : req.Limit;
        var limit = Math.Min(requested, maxItems);

        try
        {
            LlmConfig? cfg;
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // 与 LlmService.GetConfig 保持一致：优先默认站点，其次最早配置的一条
                cfg = await db.LlmConfigs.AsNoTracking()
                    .Where(x => x.IsConfigured)
                    .OrderByDescending(x => x.IsDefault).ThenBy(x => x.Id)
                    .FirstOrDefaultAsync(ct);
            }
            if (cfg is null)
            {
                Finish("未配置 LLM：请先在「设置 · LLM」填写接口地址、密钥与模型", 0);
                return;
            }

            List<int> ids;
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var q = db.Links.AsNoTracking().Where(x => !x.IsDeleted);
                if (req.LibraryId > 0) q = q.Where(x => x.LibraryId == req.LibraryId);
                if (req.Mode != "all")
                    q = q.Where(x => string.IsNullOrWhiteSpace(x.Summary) || !x.LinkTags.Any());
                ids = await q.OrderBy(x => x.Id).Take(limit).Select(x => x.Id).ToListAsync(ct);
            }
            SetTotal(ids.Count);
            if (ids.Count == 0)
            {
                Finish("没有需要补料的链接", 0);
                return;
            }
            _log.LogInformation("批量补料启动：{Count} 条，并发 {Conc}，最小间隔 {Ms}ms", ids.Count, concurrency, minIntervalMs);

            await Parallel.ForEachAsync(ids,
                new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
                async (id, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    await WaitRateLimitAsync(minIntervalMs, token);
                    try
                    {
                        await ProcessOneAsync(id, cfg, token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "批量补料单条失败 id={Id}", id);
                        Bump(failed: true);
                    }
                });

            Finish(GetStatus().CancelRequested ? "已取消" : "完成", ids.Count);
        }
        catch (OperationCanceledException)
        {
            Finish("已取消", GetStatus().Total);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "批量补料异常终止");
            Finish("异常终止：" + ex.Message, GetStatus().Total);
        }
    }

    /// <summary>单条：同一作用域内读取 → 调 LLM → 串行写回，把每条链接的 DB 往返降到 1 读 + 1 标签批查 + 1 写。</summary>
    private async Task ProcessOneAsync(int id, LlmConfig cfg, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var llm = scope.ServiceProvider.GetRequiredService<LlmService>();

        // 读实体（含已有标签），一次查询拿到 LLM 输入与去重基准
        var link = await db.Links
            .Include(x => x.LinkTags).ThenInclude(lt => lt.Tag)
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (link is null) { Bump(skipped: true); return; }

        var r = await llm.EnrichAsync(cfg, link.Title, link.Url, link.Remark);
        var tags = r.Tags?.Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var summary = r.Summary;

        if (tags.Length == 0 && string.IsNullOrWhiteSpace(summary))
        {
            Bump(skipped: true); // 配置缺失或 LLM 无有效返回
            return;
        }

        var existing = new HashSet<string>(
            link.LinkTags.Where(lt => lt.Tag != null).Select(lt => lt.Tag.Name),
            StringComparer.OrdinalIgnoreCase);
        var toAdd = tags.Where(existing.Add).ToArray(); // Add 返回 true 表示原本没有

        await _writeGate.WaitAsync(ct);
        try
        {
            // 一次查出本批涉及的已有标签（1 次查询替代 N 次），大小写不敏感匹配交给内存字典
            var found = toAdd.Length == 0
                ? new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase)
                : (await db.Tags.Where(t => toAdd.Contains(t.Name)).ToListAsync(ct))
                    .ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);

            foreach (var name in toAdd)
            {
                if (!found.TryGetValue(name, out var tag))
                {
                    tag = new Tag { Name = name };
                    found[name] = tag;
                }
                link.LinkTags.Add(new LinkTag { Tag = tag });
            }
            if (!string.IsNullOrWhiteSpace(summary)) link.Summary = summary;
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
        Bump(succeeded: true);
    }

    private async Task WaitRateLimitAsync(int intervalMs, CancellationToken ct)
    {
        if (intervalMs <= 0) return;
        while (true)
        {
            DateTime waitUntil;
            lock (_rateLock)
            {
                waitUntil = _lastCallAt.AddMilliseconds(intervalMs);
                if (waitUntil <= DateTime.UtcNow)
                {
                    _lastCallAt = DateTime.UtcNow;
                    return;
                }
            }
            await Task.Delay(waitUntil - DateTime.UtcNow, ct);
        }
    }

    private void SetTotal(int total)
    {
        lock (_statusLock) { _status.Total = total; _status.Message = "补料中…"; }
    }

    private void Bump(bool succeeded = false, bool failed = false, bool skipped = false)
    {
        lock (_statusLock)
        {
            _status.Processed++;
            if (succeeded) _status.Succeeded++;
            else if (failed) _status.Failed++;
            else if (skipped) _status.Skipped++;
        }
    }

    private void Finish(string message, int total)
    {
        lock (_statusLock)
        {
            _status.Running = false;
            _status.Message = message;
            if (total > 0) _status.Total = total;
            _status.FinishedAt = DateTime.UtcNow;
        }
        _log.LogInformation("批量补料结束：{Msg}，处理 {Total} 条", message, total);
    }
}
