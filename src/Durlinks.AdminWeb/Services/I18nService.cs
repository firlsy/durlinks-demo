using Microsoft.JSInterop;

namespace Durlinks.AdminWeb.Services;

/// <summary>轻量多语言：内置中英词条，默认中文，localStorage 记忆。切换时通知订阅组件刷新。</summary>
public class I18nService
{
    private readonly IJSRuntime _js;
    public string Lang { get; private set; } = "zh";
    public event Action? OnChange;

    public I18nService(IJSRuntime js) => _js = js;

    public async Task InitAsync()
    {
        try
        {
            var saved = await _js.InvokeAsync<string>("localStorage.getItem", "durlinks_lang");
            if (!string.IsNullOrWhiteSpace(saved)) Lang = saved;
        }
        catch { }
    }

    public async Task SetLangAsync(string lang)
    {
        Lang = lang is "zh" or "en" ? lang : "zh";
        try { await _js.InvokeVoidAsync("localStorage.setItem", "durlinks_lang", Lang); } catch { }
        OnChange?.Invoke();
    }

    private static readonly Dictionary<string, (string zh, string en)> Dict = new()
    {
        // 登录
        ["welcome"] = ("欢迎回来", "Welcome Back"),
        ["loginSubtitle"] = ("登录管理后台以继续", "Sign in to the admin console"),
        ["username"] = ("用户名", "Username"),
        ["password"] = ("登录密码", "Password"),
        ["captcha"] = ("验证码", "Captcha"),
        ["captchaPlaceholder"] = ("请输入验证码", "Enter captcha"),
        ["totp"] = ("MFA 动态码（若已开启）", "MFA code (if enabled)"),
        ["login"] = ("登录", "Sign In"),
        ["loggingIn"] = ("登录中…", "Signing in…"),
        ["forgotPwd"] = ("忘记密码？", "Forgot password?"),
        ["noAccount"] = ("没有账号？", "No account?"),
        ["register"] = ("立即注册", "Register"),
        ["loginFailed"] = ("用户名或密码错误", "Invalid username or password"),
        ["captchaWrong"] = ("验证码错误或已过期", "Captcha is wrong or expired"),
        // 导航
        ["dashboard"] = ("数据看板", "Dashboard"),
        ["links"] = ("链接管理", "Links"),
        ["import"] = ("导入", "Import"),
        ["users"] = ("用户管理", "Users"),
        ["publish"] = ("发布管理", "Publish"),
        ["publishLibraries"] = ("发布库", "Publish Libraries"),
        ["newLibrary"] = ("新建发布库", "New Library"),
        ["libraryName"] = ("发布库名称", "Library Name"),
        ["description"] = ("描述（可选）", "Description (optional)"),
        ["visibleOnWeb"] = ("网页端可见", "Visible on Web"),
        ["visibleOnClient"] = ("客户端可见", "Visible on Client"),
        ["web"] = ("网页端", "Web"),
        ["client"] = ("客户端", "Client"),
        ["newLink"] = ("新建链接", "New Link"),
        ["moveTo"] = ("移动到/复制到", "Move/Copy to"),
        ["move"] = ("移动", "Move"),
        ["copyLinks"] = ("复制", "Copy"),
        ["linkTitle"] = ("名称", "Title"),
        ["group"] = ("分组", "Group"),
        ["platform"] = ("平台", "Platform"),
        ["remark"] = ("备注", "Remark"),
        ["edit"] = ("编辑", "Edit"),
        ["delete"] = ("删除", "Delete"),
        ["goPublish"] = ("发布管理", "Publish"),
        ["llm"] = ("LLM 设置", "LLM Settings"),
        ["accountSettings"] = ("账号设置", "Account"),
        ["accountInfo"] = ("账号信息", "Account Info"),
        ["role"] = ("角色", "Role"),
        ["mfaSettings"] = ("两步验证（MFA）", "Two-Factor Auth (MFA)"),
        ["enableMfa"] = ("开启 MFA", "Enable MFA"),
        ["disableMfa"] = ("关闭 MFA", "Disable MFA"),
        ["confirmEnable"] = ("确认开启", "Confirm"),
        ["cancel"] = ("取消", "Cancel"),
        ["copy"] = ("复制", "Copy"),
        ["changePassword"] = ("修改密码", "Change Password"),
        ["currentPassword"] = ("当前密码", "Current Password"),
        ["newPassword"] = ("新密码", "New Password"),
        ["confirmPassword"] = ("确认新密码", "Confirm New Password"),
        ["save"] = ("保存", "Save"),
        ["system"] = ("系统设置", "System"),
        ["adminConsole"] = ("管理控制台", "ADMIN CONSOLE"),
        ["superAdmin"] = ("超级管理员", "Super Admin"),
        ["logout"] = ("退出", "Sign Out"),
        ["switchAccount"] = ("切换账号", "Switch Account"),
        ["refresh"] = ("刷新", "Refresh"),
        // 看板
        ["totalLinks"] = ("链接总数", "Total Links"),
        ["hiddenLinks"] = ("已隐藏", "Hidden"),
        ["aiLinks"] = ("AI 接入", "AI Imported"),
        ["groups"] = ("分组数", "Groups"),
        ["tags"] = ("标签数", "Tags"),
        ["netdisk"] = ("网盘链接", "Net Disk"),
        ["download"] = ("下载直链", "Direct Links"),
        ["linkOverview"] = ("链接概览", "Link Overview"),
        ["sourceStats"] = ("来源分布", "Source Distribution"),
        ["quickActions"] = ("快捷操作", "Quick Actions"),
        ["goImport"] = ("去导入", "Go Import"),
        ["goLinks"] = ("管理链接", "Manage Links"),
        // 通用
        ["search"] = ("搜索", "Search"),
        ["allSources"] = ("全部来源", "All Sources"),
        ["manual"] = ("人工导入", "Manual"),
        ["aiImported"] = ("AI接入", "AI Imported"),
        ["language"] = ("语言", "Language"),
    };

    public string T(string key)
    {
        if (!Dict.TryGetValue(key, out var v)) return key;
        return Lang == "en" ? v.en : v.zh;
    }

    public string this[string key] => T(key);
}
