using Durlinks.Server.Data;
using Durlinks.Shared.DTOs;
using Durlinks.Shared.Entities;
using Durlinks.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace Durlinks.Server.Services;

/// <summary>
/// Excel 后台导入：流式读取 xlsx，按 500 条一批入库，内存占用与行数无关。
/// 单例 + IServiceScopeFactory，每批用独立作用域；进度/取消放内存，单任务互斥。
/// 目的：8 万行大表在 1.7G 内存的机器上也能安全导入，且不卡住 HTTP 请求。
/// </summary>
public class ExcelImportJobService
{
    private const int BatchSize = 500;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ExcelImportJobService> _log;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private ExcelImportStatus _status = new();

    public ExcelImportJobService(IServiceScopeFactory scopes, ILogger<ExcelImportJobService> log)
    {
        _scopes = scopes; _log = log;
    }

    public ExcelImportStatus GetStatus()
    {
        lock (_lock)
        {
            return new ExcelImportStatus
            {
                Running = _status.Running, FileName = _status.FileName,
                Processed = _status.Processed, Imported = _status.Imported, Failed = _status.Failed,
                Skipped = _status.Skipped,
                Message = _status.Message, CancelRequested = _status.CancelRequested,
                StartedAt = _status.StartedAt, FinishedAt = _status.FinishedAt
            };
        }
    }

    /// <summary>启动任务；已有任务在跑返回 false。tempPath 由任务在结束时删除。</summary>
    public bool Start(string tempPath, string fileName, int libraryId, string user)
    {
        lock (_lock)
        {
            if (_status.Running) return false;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _status = new ExcelImportStatus
            {
                Running = true, FileName = fileName, Message = "准备中…", StartedAt = DateTime.UtcNow
            };
        }
        var token = _cts.Token;
        _ = Task.Run(() => RunAsync(tempPath, fileName, libraryId, user, token));
        return true;
    }

    public bool Cancel()
    {
        lock (_lock)
        {
            if (!_status.Running || _cts is null) return false;
            _status.CancelRequested = true;
            _status.Message = "正在取消…";
            _cts.Cancel();
            return true;
        }
    }

    private async Task RunAsync(string tempPath, string fileName, int libraryId, string user, CancellationToken ct)
    {
        var errors = new List<string>();
        var chunk = new List<Link>(BatchSize);
        var processed = 0;
        var imported = 0;
        var failed = 0;
        var skipped = 0;
        var sort = 0;
        var rowNo = 1;

        // 去重：一次性载入目标库已有 URL 的规范化键，避免每条都查库
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using (var loadScope = _scopes.CreateScope())
        {
            var loadDb = loadScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existing = await loadDb.Links.AsNoTracking()
                .Where(x => !x.IsDeleted && x.LibraryId == libraryId)
                .Select(x => x.Url).ToListAsync(ct);
            foreach (var u in existing)
            {
                var k = UrlKey.Normalize(u);
                if (!string.IsNullOrEmpty(k)) seen.Add(k);
            }
        }

        try
        {
            using var fs = File.OpenRead(tempPath);
            foreach (var cells in ExcelImportService.ReadRows(fs))
            {
                ct.ThrowIfCancellationRequested();
                rowNo++;
                processed++;

                Link? link;
                try
                {
                    link = ExcelImportService.MapRow(cells, rowNo, sort + 1, ImportSource.Manual, errors);
                }
                catch (Exception ex)
                {
                    failed++;
                    if (errors.Count < 200) errors.Add($"第 {rowNo} 行解析失败：{ex.Message}");
                    continue;
                }
                if (link is null) continue;

                // 按规范化 URL 去重：库中已存在或本文件已出现过的都跳过
                var key = UrlKey.Normalize(link.Url);
                if (!string.IsNullOrEmpty(key) && !seen.Add(key))
                {
                    skipped++;
                    continue;
                }

                sort++;
                chunk.Add(link);
                if (chunk.Count >= BatchSize)
                {
                    var saved = await SaveChunkAsync(chunk, libraryId, ct);
                    imported += saved;
                    chunk.Clear();
                    Update(processed, imported, failed, skipped);
                }
            }

            if (chunk.Count > 0)
                imported += await SaveChunkAsync(chunk, libraryId, ct);

            Finish($"导入完成：入库 {imported} 条，跳过重复 {skipped} 条，失败 {failed} 条", processed, imported, failed, skipped);
            _log.LogInformation("Excel 导入完成：{File}，入库 {Imported} 条，跳过重复 {Skipped} 条，处理 {Processed} 行，操作人 {User}",
                fileName, imported, skipped, processed, user);
        }
        catch (OperationCanceledException)
        {
            // 取消时把手里这一批存掉，避免用户看到“取消前白扫”
            try { if (chunk.Count > 0) imported += await SaveChunkAsync(chunk, libraryId, CancellationToken.None); }
            catch { /* 取消路径不再抛 */ }
            Finish($"已取消：已入库 {imported} 条，跳过重复 {skipped} 条", processed, imported, failed, skipped);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Excel 导入异常：{File}", fileName);
            // DbUpdateException 的外层消息永远是同一句，真正原因在 InnerException
            var reason = ex.InnerException?.Message ?? ex.Message;
            Finish("导入失败：" + reason, processed, imported, failed, skipped);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* 临时文件删不掉不影响业务 */ }
        }
    }

    /// <summary>一批入库：先确保标签存在，再用 TagId 建关联，最后批量写链接。</summary>
    private async Task<int> SaveChunkAsync(List<Link> chunk, int libraryId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var names = chunk.SelectMany(l => l.LinkTags.Select(lt => lt.Tag.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var map = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
        if (names.Count > 0)
        {
            foreach (var t in await db.Tags.Where(t => names.Contains(t.Name)).ToListAsync(ct))
                map[t.Name] = t;

            var missing = names.Where(n => !map.ContainsKey(n)).Select(n => new Tag { Name = n }).ToList();
            if (missing.Count > 0)
            {
                db.Tags.AddRange(missing);
                await db.SaveChangesAsync(ct);
                foreach (var t in missing) map[t.Name] = t;
            }
        }

        foreach (var link in chunk)
        {
            link.LibraryId = libraryId;
            var kept = new List<LinkTag>(link.LinkTags.Count);
            foreach (var lt in link.LinkTags)
            {
                if (!map.TryGetValue(lt.Tag.Name, out var tag)) continue;
                kept.Add(new LinkTag { TagId = tag.Id }); // 只带外键，避免 EF 误把 Tag 当新实体
            }
            link.LinkTags.Clear();
            foreach (var k in kept) link.LinkTags.Add(k);
            db.Links.Add(link);
        }

        await db.SaveChangesAsync(ct);
        return chunk.Count;
    }

    private void Update(int processed, int imported, int failed, int skipped)
    {
        lock (_lock)
        {
            _status.Processed = processed;
            _status.Imported = imported;
            _status.Failed = failed;
            _status.Skipped = skipped;
            _status.Message = $"导入中…已扫描 {processed} 行，入库 {imported} 条，跳过重复 {skipped} 条";
        }
    }

    private void Finish(string message, int processed, int imported, int failed, int skipped)
    {
        lock (_lock)
        {
            _status.Running = false;
            _status.Message = message;
            _status.Processed = processed;
            _status.Imported = imported;
            _status.Failed = failed;
            _status.Skipped = skipped;
            _status.FinishedAt = DateTime.UtcNow;
        }
    }
}
