using Durlinks.Server.Data;
using Durlinks.Shared.DTOs;
using Durlinks.Shared.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Durlinks.Server.Controllers;

[ApiController]
[Route("api/admin/categories")]
[Authorize]
public sealed class CategoryController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CategoryOptionsDto>> List()
    {
        var groups = await db.Groups.AsNoTracking().OrderBy(x => x.Name).Select(x => x.Name).ToListAsync();
        var platforms = await db.Platforms.AsNoTracking().OrderBy(x => x.Name).Select(x => x.Name).ToListAsync();
        var tags = await db.Tags.AsNoTracking().OrderBy(x => x.Name).Select(x => x.Name).ToListAsync();
        var linkTypes = await db.LinkTypes.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Name).ToListAsync();
        return Ok(new CategoryOptionsDto { Groups = groups, Platforms = platforms, Tags = tags, LinkTypes = linkTypes });
    }

    [HttpPost("{kind}")]
    public async Task<ActionResult> Create(string kind, [FromBody] CategoryNameRequest request)
    {
        var name = Normalize(request.Name);
        if (name is null) return BadRequest(new { message = "名称不能为空，且不能超过 200 个字符" });
        if (await Exists(kind, name)) return Conflict(new { message = "该名称已存在" });
        switch (kind.ToLowerInvariant())
        {
            case "group": db.Groups.Add(new Group { Name = name }); break;
            case "platform": db.Platforms.Add(new PlatformOption { Name = name }); break;
            case "tag": db.Tags.Add(new Tag { Name = name }); break;
            case "type": db.LinkTypes.Add(new LinkTypeOption { Name = name }); break;
            default: return BadRequest(new { message = "类别必须是 group、platform、tag 或 type" });
        }
        await db.SaveChangesAsync();
        return Ok(new { name });
    }

    [HttpPut("{kind}")]
    public async Task<ActionResult> Rename(string kind, [FromBody] CategoryRenameRequest request)
    {
        var oldName = Normalize(request.OldName);
        var newName = Normalize(request.NewName);
        if (oldName is null || newName is null) return BadRequest(new { message = "名称不能为空，且不能超过 200 个字符" });
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return Ok(new { name = newName });
        if (await Exists(kind, newName)) return Conflict(new { message = "目标名称已存在" });

        switch (kind.ToLowerInvariant())
        {
            case "group":
            {
                var item = await db.Groups.FirstOrDefaultAsync(x => x.Name == oldName);
                if (item is null) return NotFound();
                item.Name = newName;
                await db.Links.Where(x => x.GroupName == oldName).ExecuteUpdateAsync(x => x.SetProperty(l => l.GroupName, newName));
                break;
            }
            case "platform":
            {
                var item = await db.Platforms.FirstOrDefaultAsync(x => x.Name == oldName);
                if (item is null) return NotFound();
                item.Name = newName;
                await db.Links.Where(x => x.Platform == oldName).ExecuteUpdateAsync(x => x.SetProperty(l => l.Platform, newName));
                break;
            }
            case "tag":
            {
                var item = await db.Tags.FirstOrDefaultAsync(x => x.Name == oldName);
                if (item is null) return NotFound();
                item.Name = newName;
                break;
            }
            case "type":
            {
                var item = await db.LinkTypes.FirstOrDefaultAsync(x => x.Name == oldName);
                if (item is null) return NotFound();
                item.Name = newName;
                await db.Links.Where(x => x.LinkType == oldName).ExecuteUpdateAsync(x => x.SetProperty(l => l.LinkType, newName));
                break;
            }
            default: return BadRequest(new { message = "类别必须是 group、platform、tag 或 type" });
        }
        await db.SaveChangesAsync();
        return Ok(new { name = newName });
    }

    [HttpDelete("{kind}")]
    public async Task<ActionResult> Delete(string kind, [FromQuery] string name)
    {
        var normalized = Normalize(name);
        if (normalized is null) return BadRequest(new { message = "名称无效" });
        switch (kind.ToLowerInvariant())
        {
            case "group":
            {
                var item = await db.Groups.FirstOrDefaultAsync(x => x.Name == normalized);
                if (item is null) return NotFound();
                if (await db.Links.AnyAsync(x => x.GroupName == normalized)) return Conflict(new { message = "该分组仍被链接使用，请先调整链接分组" });
                db.Groups.Remove(item); break;
            }
            case "platform":
            {
                var item = await db.Platforms.FirstOrDefaultAsync(x => x.Name == normalized);
                if (item is null) return NotFound();
                if (await db.Links.AnyAsync(x => x.Platform == normalized)) return Conflict(new { message = "该平台仍被链接使用，请先调整链接平台" });
                db.Platforms.Remove(item); break;
            }
            case "tag":
            {
                var item = await db.Tags.FirstOrDefaultAsync(x => x.Name == normalized);
                if (item is null) return NotFound();
                // 先解除该标签与所有链接的关联，避免删除后迁移从 LinkTags 回填导致"复活"
                await db.LinkTags.Where(x => x.TagId == item.Id).ExecuteDeleteAsync();
                db.Tags.Remove(item); break;
            }
            case "type":
            {
                var item = await db.LinkTypes.FirstOrDefaultAsync(x => x.Name == normalized);
                if (item is null) return NotFound();
                if (await db.Links.AnyAsync(x => x.LinkType == normalized)) return Conflict(new { message = "该链接形式仍被链接使用，请先调整链接形式" });
                db.LinkTypes.Remove(item); break;
            }
            default: return BadRequest(new { message = "类别必须是 group、platform、tag 或 type" });
        }
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{kind}/batch-delete")]
    public async Task<ActionResult<CategoryBatchDeleteResult>> BatchDelete(string kind, [FromBody] CategoryBatchDeleteRequest request)
    {
        var names = (request.Names ?? [])
            .Select(Normalize)
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0) return BadRequest(new { message = "未选择有效项目" });

        var result = new CategoryBatchDeleteResult();
        switch (kind.ToLowerInvariant())
        {
            case "group":
            case "platform":
            case "type":
            {
                var used = kind.ToLowerInvariant() switch
                {
                    "group" => await db.Links.Where(x => names.Contains(x.GroupName)).Select(x => x.GroupName).Distinct().ToListAsync(),
                    "platform" => await db.Links.Where(x => names.Contains(x.Platform)).Select(x => x.Platform).Distinct().ToListAsync(),
                    _ => await db.Links.Where(x => names.Contains(x.LinkType)).Select(x => x.LinkType).Distinct().ToListAsync()
                };
                var toDelete = names.Where(x => !used.Contains(x, StringComparer.Ordinal)).ToList();
                switch (kind.ToLowerInvariant())
                {
                    case "group":
                        result.Deleted = await db.Groups.AsNoTracking().Where(x => toDelete.Contains(x.Name)).Select(x => x.Name).ToListAsync();
                        await db.Groups.Where(x => toDelete.Contains(x.Name)).ExecuteDeleteAsync();
                        break;
                    case "platform":
                        result.Deleted = await db.Platforms.AsNoTracking().Where(x => toDelete.Contains(x.Name)).Select(x => x.Name).ToListAsync();
                        await db.Platforms.Where(x => toDelete.Contains(x.Name)).ExecuteDeleteAsync();
                        break;
                    default:
                        result.Deleted = await db.LinkTypes.AsNoTracking().Where(x => toDelete.Contains(x.Name)).Select(x => x.Name).ToListAsync();
                        await db.LinkTypes.Where(x => toDelete.Contains(x.Name)).ExecuteDeleteAsync();
                        break;
                }
                result.Skipped = names.Where(x => !result.Deleted.Contains(x, StringComparer.Ordinal)).ToList();
                break;
            }
            case "tag":
            {
                // 删除标签前先解除其与所有链接的关联（LinkTags），避免迁移从关联回填导致标签"复活"
                var tagIds = await db.Tags.Where(x => names.Contains(x.Name)).Select(x => x.Id).ToListAsync();
                result.Deleted = await db.Tags.AsNoTracking().Where(x => names.Contains(x.Name)).Select(x => x.Name).ToListAsync();
                result.Skipped = names.Where(x => !result.Deleted.Contains(x, StringComparer.Ordinal)).ToList();
                if (tagIds.Count > 0)
                {
                    await db.LinkTags.Where(x => tagIds.Contains(x.TagId)).ExecuteDeleteAsync();
                    await db.Tags.Where(x => tagIds.Contains(x.Id)).ExecuteDeleteAsync();
                }
                break;
            }
            default: return BadRequest(new { message = "类别必须是 group、platform、tag 或 type" });
        }
        return Ok(result);
    }

    private async Task<bool> Exists(string kind, string name) => kind.ToLowerInvariant() switch
    {
        "group" => await db.Groups.AnyAsync(x => x.Name == name),
        "platform" => await db.Platforms.AnyAsync(x => x.Name == name),
        "tag" => await db.Tags.AnyAsync(x => x.Name == name),
        "type" => await db.LinkTypes.AnyAsync(x => x.Name == name),
        _ => false
    };

    private static string? Normalize(string? value)
    {
        var name = value?.Trim();
        return string.IsNullOrEmpty(name) || name.Length > 200 ? null : name;
    }
}

