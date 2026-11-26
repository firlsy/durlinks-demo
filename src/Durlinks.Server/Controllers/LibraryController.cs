using Durlinks.Server.Data;
using Durlinks.Server.Services;
using Durlinks.Shared.DTOs;
using Durlinks.Shared.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Durlinks.Server.Controllers;

/// <summary>发布库：管理端增删改查 + 公开端按渠道（web/client）列出可见库。</summary>
[ApiController]
[Route("api")]
public class LibraryController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<LibraryController> _log;
    public LibraryController(AppDbContext db, ILogger<LibraryController> log) { _db = db; _log = log; }

    // ---------- 公开：网页端/客户端可见的发布库 ----------
    [HttpGet("libraries")]
    public async Task<ActionResult<List<LibraryDto>>> PublicList([FromQuery] string? channel)
    {
        var isClient = string.Equals(channel, "client", StringComparison.OrdinalIgnoreCase);
        var q = _db.PublishLibraries.Where(x => !x.IsDeleted);
        q = isClient ? q.Where(x => x.VisibleOnClient) : q.Where(x => x.VisibleOnWeb);

        return Ok(await q.OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new LibraryDto
            {
                Id = x.Id, Name = x.Name, Description = x.Description,
                VisibleOnWeb = x.VisibleOnWeb, VisibleOnClient = x.VisibleOnClient,
                SortOrder = x.SortOrder, CreatedAt = x.CreatedAt
            }).ToListAsync());
    }

    // ---------- 管理端 ----------
    [Authorize]
    [HttpGet("admin/libraries")]
    public async Task<ActionResult<List<LibraryDto>>> List()
    {
        var libs = await _db.PublishLibraries.Where(x => !x.IsDeleted)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync();
        var counts = await _db.Links.Where(x => !x.IsDeleted && x.LibraryId != null)
            .GroupBy(x => x.LibraryId!.Value)
            .Select(g => new { LibraryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.LibraryId, x => x.Count);

        return Ok(libs.Select(x => new LibraryDto
        {
            Id = x.Id, Name = x.Name, Description = x.Description,
            VisibleOnWeb = x.VisibleOnWeb, VisibleOnClient = x.VisibleOnClient,
            SortOrder = x.SortOrder, CreatedAt = x.CreatedAt,
            LinkCount = counts.GetValueOrDefault(x.Id, 0)
        }).ToList());
    }

    [Authorize]
    [HttpPost("admin/libraries")]
    public async Task<ActionResult<LibraryDto>> Create([FromBody] LibrarySaveRequest dto)
    {
        var name = dto.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { message = "发布库名称不能为空" });
        if (name.Length > 100) return BadRequest(new { message = "名称不能超过 100 字" });
        if (await _db.PublishLibraries.AnyAsync(x => !x.IsDeleted && x.Name == name))
            return BadRequest(new { message = "已存在同名发布库" });

        var maxSort = await _db.PublishLibraries.Where(x => !x.IsDeleted)
            .Select(x => (int?)x.SortOrder).MaxAsync() ?? 0;

        var lib = new PublishLibrary
        {
            Name = name,
            Description = dto.Description?.Trim(),
            VisibleOnWeb = dto.VisibleOnWeb,
            VisibleOnClient = dto.VisibleOnClient,
            SortOrder = dto.SortOrder == 0 ? maxSort + 1 : dto.SortOrder
        };
        _db.PublishLibraries.Add(lib);
        await _db.SaveChangesAsync();

        return Ok(new LibraryDto
        {
            Id = lib.Id, Name = lib.Name, Description = lib.Description,
            VisibleOnWeb = lib.VisibleOnWeb, VisibleOnClient = lib.VisibleOnClient,
            SortOrder = lib.SortOrder, CreatedAt = lib.CreatedAt, LinkCount = 0
        });
    }

    [Authorize]
    [HttpPut("admin/libraries/{id}")]
    public async Task<ActionResult> Update(int id, [FromBody] LibrarySaveRequest dto)
    {
        var lib = await _db.PublishLibraries.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        if (lib is null) return NotFound();

        var name = dto.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { message = "发布库名称不能为空" });
        if (await _db.PublishLibraries.AnyAsync(x => !x.IsDeleted && x.Id != id && x.Name == name))
            return BadRequest(new { message = "已存在同名发布库" });

        lib.Name = name;
        lib.Description = dto.Description?.Trim();
        lib.VisibleOnWeb = dto.VisibleOnWeb;
        lib.VisibleOnClient = dto.VisibleOnClient;
        if (dto.SortOrder != 0) lib.SortOrder = dto.SortOrder;
        await _db.SaveChangesAsync();
        return Ok(new { message = "已保存" });
    }

    /// <summary>
    /// 删除发布库（软删除，连带该库下链接）。属于不可逆的高危操作，必须二次输入当前账号密码。
    /// 说明：MFA 账号同样只校验密码，不再要求动态码（按业务要求）。
    /// </summary>
    [Authorize]
    [HttpPost("admin/libraries/{id}/delete")]
    public async Task<ActionResult> Delete(int id, [FromBody] DeleteLibraryRequest? req)
    {
        var username = User.Identity?.Name ?? "";
        var user = await _db.Users.FirstOrDefaultAsync(x => x.Username == username);
        if (user is null) return Unauthorized(new { message = "登录状态已失效，请重新登录" });

        if (string.IsNullOrWhiteSpace(req?.Password) || !PasswordHelper.Verify(req.Password, user.PasswordHash))
        {
            _log.LogWarning("删除发布库被拒绝：{User} 密码校验未通过，库 Id={Id}", username, id);
            return BadRequest(new { message = "密码错误，未执行删除" });
        }

        var lib = await _db.PublishLibraries.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        if (lib is null) return NotFound(new { message = "发布库不存在或已删除" });

        var remaining = await _db.PublishLibraries.CountAsync(x => !x.IsDeleted && x.Id != id);
        if (remaining == 0) return BadRequest(new { message = "至少保留一个发布库" });

        var links = await _db.Links.Where(x => x.LibraryId == id && !x.IsDeleted).ToListAsync();
        lib.IsDeleted = true;
        // 该库下链接一并软删除，避免变成无归属的孤儿数据
        foreach (var l in links) l.IsDeleted = true;
        await _db.SaveChangesAsync();

        _log.LogWarning("发布库已删除：{Name}(Id={Id})，操作人 {User}，连带软删除链接 {Count} 条",
            lib.Name, lib.Id, username, links.Count);
        return Ok(new { message = "已删除", deletedLinks = links.Count });
    }

    /// <summary>旧 DELETE 入口保留提示，避免老缓存前端静默失败（不执行任何删除）。</summary>
    [Authorize]
    [HttpDelete("admin/libraries/{id}")]
    public ActionResult DeleteLegacy(int id) =>
        BadRequest(new { message = "删除发布库需密码验证，请刷新页面后重试" });
}
