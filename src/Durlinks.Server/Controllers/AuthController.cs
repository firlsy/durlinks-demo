using Durlinks.Server.Data;
using Durlinks.Server.Services;
using Durlinks.Shared.DTOs;
using Durlinks.Shared.Entities;
using Durlinks.Shared.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Durlinks.Server.Controllers;

/// <summary>管理端鉴权：登录（+MFA）、用户管理、MFA 绑定。仅 :8788 可访问。</summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuthService _auth;
    private readonly CaptchaService _captcha;
    public AuthController(AppDbContext db, AuthService auth, CaptchaService captcha)
    { _db = db; _auth = auth; _captcha = captcha; }

    [HttpGet("captcha")]
    public ActionResult<CaptchaDto> GetCaptcha()
    {
        var (id, svg) = _captcha.Generate();
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        return Ok(new CaptchaDto { Id = id, Svg = svg, ImageUrl = "data:image/svg+xml;base64," + b64 });
    }

    /// <summary>
    /// 图片直出接口：一个 HTTP 请求同时生成验证码并返回 SVG 图片，ticket 写入 HttpOnly Cookie。
    /// 前端只需 &lt;img src="api/auth/captcha/image"&gt;，刷新加随机 query 即可，完全不依赖 JS/WASM 时序。
    /// </summary>
    [HttpGet("captcha/image")]
    public IActionResult GetCaptchaImage()
    {
        // 复用浏览器带来的 ticket（刷新时覆盖答案），没有则新建
        var ticket = Request.Cookies["durlinks_captcha"];
        if (string.IsNullOrWhiteSpace(ticket)) ticket = Guid.NewGuid().ToString("N");
        var (id, svg) = _captcha.Generate(ticket);

        Response.Cookies.Append("durlinks_captcha", id, new CookieOptions
        {
            HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Strict,
            Path = "/", MaxAge = TimeSpan.FromMinutes(5)
        });
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        return Content(svg, "image/svg+xml");
    }

    [HttpGet("mfa/check")]
    public async Task<ActionResult<MfaCheckDto>> CheckMfa([FromQuery] string username)
    {
        var enabled = await _db.Users.AnyAsync(u => u.Username == username && u.MfaEnabled);
        return Ok(new MfaCheckDto { MfaEnabled = enabled });
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest req)
    {
        // 显式传入的 CaptchaId 优先；未传时回退到图片直出方案写入的 Cookie ticket
        var captchaId = !string.IsNullOrWhiteSpace(req.CaptchaId)
            ? req.CaptchaId
            : Request.Cookies["durlinks_captcha"];
        if (!_captcha.Verify(captchaId, req.CaptchaCode))
            return BadRequest(new { message = "验证码错误或已过期" });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);
        if (user is null || !PasswordHelper.Verify(req.Password, user.PasswordHash))
            return Unauthorized(new { message = "用户名或密码错误" });

        // 开启了 MFA 则必须提供正确动态码
        if (user.MfaEnabled)
        {
            if (string.IsNullOrWhiteSpace(req.TotpCode) || !AuthService.VerifyTotp(user.MfaSecret!, req.TotpCode))
                return Unauthorized(new { message = "MFA 动态码错误", mfaRequired = true });
        }

        return Ok(new LoginResponse
        {
            Token = _auth.IssueToken(user),
            Username = user.Username,
            Role = user.Role.ToString(),
            MfaEnabled = user.MfaEnabled,
            MfaRequired = user.MfaEnabled
        });
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<object>> Me()
    {
        var username = User.Identity?.Name;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        return Ok(new
        {
            username = User.Identity?.Name,
            role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value,
            mfaEnabled = user?.MfaEnabled ?? false
        });
    }

    /// <summary>关闭当前账号的 MFA：必须提供正确的动态码或登录密码，防止令牌被盗后直接关闭。</summary>
    [Authorize]
    [HttpPost("mfa/disable")]
    public async Task<ActionResult> MfaDisable([FromBody] MfaDisableRequest dto)
    {
        var username = User.Identity?.Name;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user is null) return Unauthorized();
        if (!user.MfaEnabled) return BadRequest(new { message = "MFA 未开启" });

        var codeOk = !string.IsNullOrWhiteSpace(dto.Code) && !string.IsNullOrEmpty(user.MfaSecret)
                     && AuthService.VerifyTotp(user.MfaSecret, dto.Code);
        var pwdOk = !string.IsNullOrWhiteSpace(dto.Password) && PasswordHelper.Verify(dto.Password, user.PasswordHash);
        if (!codeOk && !pwdOk)
            return BadRequest(new { message = "动态码或密码错误" });

        user.MfaEnabled = false;
        user.MfaSecret = null;
        await _db.SaveChangesAsync();
        return Ok(new { message = "MFA 已关闭" });
    }

    /// <summary>修改自己的登录密码。</summary>
    [Authorize]
    [HttpPost("password")]
    public async Task<ActionResult> ChangePassword([FromBody] ChangePasswordRequest dto)
    {
        if (string.IsNullOrWhiteSpace(dto.NewPassword) || dto.NewPassword.Length < 8)
            return BadRequest(new { message = "新密码至少 8 位" });
        var username = User.Identity?.Name;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user is null) return Unauthorized();
        if (!PasswordHelper.Verify(dto.CurrentPassword, user.PasswordHash))
            return BadRequest(new { message = "当前密码错误" });

        user.PasswordHash = PasswordHelper.Hash(dto.NewPassword);
        await _db.SaveChangesAsync();
        return Ok(new { message = "密码已修改" });
    }

    [Authorize]
    [HttpPost("mfa/setup")]
    public async Task<ActionResult<MfaSetupDto>> MfaSetup()
    {
        var username = User.Identity?.Name;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user is null) return Unauthorized();

        // 已开启 MFA 时禁止直接重置密钥（否则仅凭被盗令牌即可绕过第二因素）
        if (user.MfaEnabled)
            return BadRequest(new { message = "MFA 已开启，如需重新绑定请先关闭 MFA" });

        var secret = AuthService.GenerateMfaSecret();
        user.MfaSecret = secret; // 待 verify 后才启用
        await _db.SaveChangesAsync();
        return Ok(new MfaSetupDto
        {
            Secret = secret,
            QrUri = AuthService.BuildOtpAuthUri(user.Username, secret)
        });
    }

    [Authorize]
    [HttpPost("mfa/verify")]
    public async Task<ActionResult> MfaVerify([FromBody] TotpVerifyDto dto)
    {
        var username = User.Identity?.Name;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user is null || string.IsNullOrEmpty(user.MfaSecret)) return BadRequest();
        if (!AuthService.VerifyTotp(user.MfaSecret, dto.Code))
            return BadRequest(new { message = "动态码错误" });

        user.MfaEnabled = true;
        await _db.SaveChangesAsync();
        return Ok(new { message = "MFA 已开启" });
    }

    [Authorize(Roles = "SuperAdmin")]
    [HttpGet("users")]
    public async Task<ActionResult<List<UserDto>>> ListUsers()
    {
        return await _db.Users.Select(u => new UserDto
        {
            Id = u.Id, Username = u.Username, Role = u.Role.ToString(),
            MfaEnabled = u.MfaEnabled, CreatedAt = u.CreatedAt
        }).ToListAsync();
    }

    [Authorize(Roles = "SuperAdmin")]
    [HttpPost("users")]
    public async Task<ActionResult<UserDto>> CreateUser([FromBody] CreateUserRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { message = "用户名与密码不能为空" });
        if (await _db.Users.AnyAsync(u => u.Username == req.Username))
            return Conflict(new { message = "用户名已存在" });

        var user = new User
        {
            Username = req.Username.Trim(),
            PasswordHash = PasswordHelper.Hash(req.Password),
            Role = UserRole.Admin
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return Ok(new UserDto
        {
            Id = user.Id, Username = user.Username, Role = user.Role.ToString(),
            MfaEnabled = false, CreatedAt = user.CreatedAt
        });
    }

    public class TotpVerifyDto { public string Code { get; set; } = string.Empty; }
}
