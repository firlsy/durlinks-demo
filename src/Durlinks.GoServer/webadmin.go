package main

import (
	"bytes"
	"embed"
	"fmt"
	"html/template"
	"net/http"
	"net/url"
	"strconv"
	"strings"
)

//go:embed webadmin/templates/*.html webadmin/static
var adminAssets embed.FS

var adminTemplate = template.Must(template.New("admin.html").Funcs(template.FuncMap{
	"ptr":  func(p *string) string { return ptrStr(p) },
	"isAI": func(s ImportSource) bool { return s == ImportAI },
	"join": strings.Join,
	"sub":  func(a, b int) int { return a - b },
	// yyyy-MM-dd（与 C# 的 CreatedAt.ToString("yyyy-MM-dd") 一致）
	"date": func(v string) string {
		if len(v) >= 10 {
			return v[:10]
		}
		return v
	},
}).ParseFS(adminAssets, "webadmin/templates/admin.html"))

var navTitles = map[string]string{
	"dashboard":  "数据看板",
	"publish":    "发布管理",
	"categories": "分组管理",
	"users":      "用户管理",
	"llm":        "LLM 设置",
	"account":    "账号设置",
}

var adminPageSizes = []int{20, 50, 100, 200, 500, 1000}

// adminData 是管理控制台的渲染上下文。
type adminData struct {
	View      string
	Title     string
	User      string
	Role      string
	Initial   string
	NavFolded bool

	Stats                                     map[string]int
	BarManual, BarAI, BarNetdisk, BarDownload int

	Libs       []LibraryDto
	CurLib     int
	CurLibName string

	Links   []LinkDto
	Editing *LinkDto
	Cats    CategoryOptionsDto

	Q         string
	Src       string
	Page      int
	PageSize  int
	Total     int
	MaxPage   int
	PrevPage  int
	NextPage  int
	PageSizes []int

	BackQuery string

	CatKind     string
	CatTitle    string
	CatValues   []string
	CatCount    int
	CatSections []catSection
	CatFolded   bool
	CatFoldNext string

	LibFolded    bool
	FoldNext     string
	PageNums     []int
	Users        []UserDto
	MyMfa        bool
	LlmConfigs   []LlmConfigDto
	IsSuperAdmin bool

	// 发布库就地新建/编辑（与 .NET 版一致：编辑在原卡片展开，新建在列表下方）
	LibEditing bool
	LibEditID  int
	LibNew     bool

	base url.Values
}

// catSection 分组管理左侧「分类」卡片。
type catSection struct {
	Kind     string
	Title    string
	Count    int
	Selected int
}

// Num 读统计值（模板里避免 nil map 报错）。
func (d adminData) Num(key string) int {
	if d.Stats == nil {
		return 0
	}
	return d.Stats[key]
}

// Link 同用户端：保留当前查询参数，按需覆盖（值 0/空 表示删除该参数）。
func (d adminData) Link(kv ...any) string {
	v := url.Values{}
	for k, vals := range d.base {
		for _, x := range vals {
			v.Add(k, x)
		}
	}
	for i := 0; i+1 < len(kv); i += 2 {
		k := fmt.Sprint(kv[i])
		val := fmt.Sprint(kv[i+1])
		if val == "" || val == "0" {
			v.Del(k)
			continue
		}
		v.Set(k, val)
	}
	if len(v) == 0 {
		v.Set("view", "publish")
	}
	return "/console?" + v.Encode()
}

// barHeight 把某一类链接数换算成柱状图高度百分比（最低 6%，避免看不见）。
func barHeight(v, max int) int {
	if max <= 0 || v <= 0 {
		return 6
	}
	h := v * 100 / max
	if h < 6 {
		h = 6
	}
	return h
}

// authFromRequest 校验 cookie 里的 JWT（页面导航没有 Authorization 头，靠登录时下发的 cookie）。
func (a *App) authFromRequest(r *http.Request) (*User, *JWTClaims) {
	ck, err := r.Cookie(authCookieName)
	if err == nil && strings.TrimSpace(ck.Value) != "" {
		if claims, err := a.auth.ParseToken(ck.Value); err == nil {
			if u, err := a.findUserByUsername(claims.UniqueName); err == nil && u != nil && u.TokenVersion == claims.TokenVersion {
				claims.Role = u.Role.String()
				return u, claims
			}
		}
	}
	// 兼容直接带 Authorization 的访问（例如脚本调试）
	header := r.Header.Get("Authorization")
	if strings.HasPrefix(strings.ToLower(header), "bearer ") {
		if claims, err := a.auth.ParseToken(strings.TrimSpace(header[7:])); err == nil {
			if u, err := a.findUserByUsername(claims.UniqueName); err == nil && u != nil && u.TokenVersion == claims.TokenVersion {
				claims.Role = u.Role.String()
				return u, claims
			}
		}
	}
	return nil, nil
}

// handleConsole GET /console —— 服务端渲染管理控制台。
func (a *App) handleConsole(w http.ResponseWriter, r *http.Request) {
	user, claims := a.authFromRequest(r)
	if user == nil {
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}
	qs := r.URL.Query()
	view := strings.ToLower(strings.TrimSpace(qs.Get("view")))
	if _, ok := navTitles[view]; !ok {
		view = "dashboard"
	}

	page := queryInt(r, "page", 1)
	if page < 1 {
		page = 1
	}
	size := queryInt(r, "size", 20)
	if size <= 0 || size > adminMaxPageSize {
		size = 20
	}

	data := adminData{
		View: view, Title: navTitles[view],
		User: claims.UniqueName, Role: claims.Role,
		Initial: strings.ToUpper(substrRunes(claims.UniqueName, 0, 1)),
		Q:       strings.TrimSpace(qs.Get("q")),
		Src:     strings.TrimSpace(qs.Get("src")),
		Page:    page, PageSize: size, PageSizes: adminPageSizes,
		base: qs,
	}

	switch view {
	case "categories":
		kind := strings.ToLower(strings.TrimSpace(qs.Get("kind")))
		if _, ok := categoryKinds[kind]; !ok {
			kind = "group"
		}
		data.CatKind = kind
		data.CatFolded = qs.Get("cfold") == "1"
		data.CatFoldNext = "0"
		if data.CatFolded {
			data.CatFoldNext = "0"
		} else {
			data.CatFoldNext = "1"
		}
		if cats, err := a.categoryOptions(); err == nil {
			data.Cats = cats
			switch kind {
			case "platform":
				data.CatValues = cats.Platforms
			case "tag":
				data.CatValues = cats.Tags
			case "type":
				data.CatValues = cats.LinkTypes
			default:
				data.CatValues = cats.Groups
			}
			data.CatCount = len(data.CatValues)
			data.CatSections = []catSection{
				{Kind: "group", Title: "分组", Count: len(cats.Groups)},
				{Kind: "platform", Title: "平台", Count: len(cats.Platforms)},
				{Kind: "type", Title: "链接形式", Count: len(cats.LinkTypes)},
				{Kind: "tag", Title: "标签", Count: len(cats.Tags)},
			}
			for _, cs := range data.CatSections {
				if cs.Kind == kind {
					data.CatTitle = cs.Title
				}
			}
		}

	case "users":
		users, err := a.listUsers()
		if err != nil {
			writeInternalErr(w, err)
			return
		}
		data.Users = users

	case "llm":
		cfgs, err := a.queryLlmConfigs(` ORDER BY "IsDefault" DESC, "Id"`)
		if err != nil {
			writeInternalErr(w, err)
			return
		}
		out := make([]LlmConfigDto, 0, len(cfgs))
		for i := range cfgs {
			out = append(out, toLlmDto(&cfgs[i]))
		}
		data.LlmConfigs = out

	case "dashboard":
		stats, err := a.queryStatsMap()
		if err != nil {
			writeInternalErr(w, err)
			return
		}
		data.Stats = stats
		manual := stats["total"] - stats["aiCount"]
		if manual < 0 {
			manual = 0
		}
		max := manual
		for _, v := range []int{stats["aiCount"], stats["netdisk"], stats["download"]} {
			if v > max {
				max = v
			}
		}
		data.BarManual = barHeight(manual, max)
		data.BarAI = barHeight(stats["aiCount"], max)
		data.BarNetdisk = barHeight(stats["netdisk"], max)
		data.BarDownload = barHeight(stats["download"], max)

	case "publish":
		libs, err := a.queryLibrariesForExport()
		if err != nil {
			writeInternalErr(w, err)
			return
		}
		data.Libs = libs
		data.LibFolded = qs.Get("fold") == "1"
		data.FoldNext = "1"
		if data.LibFolded {
			data.FoldNext = "0"
		}
		// 就地新建/编辑：编辑在原卡片展开，新建在列表下方（对应 C# 的 libFormOpen/libEditing）
		data.LibEditID = queryInt(r, "libedit", 0)
		data.LibEditing = data.LibEditID > 0
		data.LibNew = qs.Get("libnew") == "1"
		// 与 C# 一致：始终落在某一个库上，没有「全部发布库」这种入口
		data.CurLib = queryInt(r, "lib", 0)
		if data.CurLib == 0 && len(libs) > 0 {
			data.CurLib = libs[0].ID
		}
		for _, lb := range libs {
			if lb.ID == data.CurLib {
				data.CurLibName = lb.Name
				break
			}
		}
		libPtr := data.CurLib
		lq := LinkQuery{
			Q: data.Q, Channel: "web", Page: page, PageSize: size,
			LibraryID: &libPtr,
		}
		if data.Src == "0" || data.Src == "1" {
			if n, err := strconv.Atoi(data.Src); err == nil {
				s := ImportSource(n)
				lq.ImportSource = &s
			}
		}
		items, total, err := a.db.queryLinks(lq, false)
		if err != nil {
			writeInternalErr(w, err)
			return
		}
		data.Links = items
		data.Total = total
		data.MaxPage = 1
		if size > 0 && total > 0 {
			data.MaxPage = (total + size - 1) / size
		}
		if page > data.MaxPage {
			page = data.MaxPage
			data.Page = page
		}
		data.PrevPage = page - 1
		data.NextPage = page + 1
		data.PageNums = make([]int, 0, data.MaxPage)
		for p := 1; p <= data.MaxPage; p++ {
			data.PageNums = append(data.PageNums, p)
		}

		if cats, err := a.categoryOptions(); err == nil {
			data.Cats = cats
		}

		// 编辑：?edit=<id> 时在列表上方展开表单
		if editID := queryInt(r, "edit", 0); editID > 0 {
			for i := range items {
				if items[i].ID == editID {
					data.Editing = &items[i]
					break
				}
			}
			if data.Editing == nil {
				// 不在当前页时按 id 直查
				data.Editing = a.findLinkDto(editID)
			}
		}
		// 保存后回跳地址：去掉 edit 参数
		back := url.Values{}
		for k, vals := range qs {
			if k == "edit" {
				continue
			}
			for _, v := range vals {
				back.Add(k, v)
			}
		}
		data.BackQuery = "/console?" + back.Encode()
	}

	data.IsSuperAdmin = claims.Role == "SuperAdmin"
	data.MyMfa = user.MfaEnabled

	var buf bytes.Buffer
	if err := adminTemplate.Execute(&buf, data); err != nil {
		a.logf("渲染管理端失败 view=%s: %v", view, err)
		writeErr(w, http.StatusInternalServerError, "页面渲染失败")
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-cache")
	_, _ = w.Write(buf.Bytes())
}

// handleConsoleLogout 清掉登录 cookie 并回登录页。
// 仅当请求携带有效凭证时才下发清除指令，避免跨站 GET 触发登出骚扰（CSRF）。
// SameSite=Strict 下跨站请求不会带 cookie，因此这里校验能通过即视为本人操作。
func (a *App) handleConsoleLogout(w http.ResponseWriter, r *http.Request) {
	if u, claims := a.authFromRequest(r); u != nil && claims != nil {
		http.SetCookie(w, &http.Cookie{
			Name: authCookieName, Value: "", Path: "/", HttpOnly: true,
			SameSite: http.SameSiteStrictMode, MaxAge: -1, Secure: a.cookieSecure(r),
		})
	}
	http.Redirect(w, r, "/", http.StatusFound)
}

// handleAdminAssets 提供 /console/assets/ 下的内嵌静态资源。
func (a *App) handleAdminAssets(w http.ResponseWriter, r *http.Request) {
	rel := strings.TrimPrefix(r.URL.Path, "/console/assets/")
	if rel == "" || strings.Contains(rel, "..") {
		http.NotFound(w, r)
		return
	}
	data, err := adminAssets.ReadFile("webadmin/static/" + rel)
	if err != nil {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Content-Type", contentTypeFor(rel))
	w.Header().Set("Cache-Control", "public, max-age=3600")
	w.Header().Set("Content-Length", itoa(len(data)))
	_, _ = w.Write(data)
}

// findLinkDto 按 id 取单条链接（编辑时不在当前页也能打开）。
func (a *App) findLinkDto(id int) *LinkDto {
	items, _, err := a.db.queryLinks(LinkQuery{Page: 1, PageSize: 1, ID: id}, false)
	if err != nil || len(items) == 0 {
		return nil
	}
	return &items[0]
}

// listUsers 用户列表（与 /api/auth/users 同源）。
func (a *App) listUsers() ([]UserDto, error) {
	rows, err := a.db.sql.Query(`SELECT "Id","Username","Role","MfaEnabled","CreatedAt" FROM "Users" ORDER BY "Id"`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []UserDto{}
	for rows.Next() {
		var (
			id, role  int
			username  string
			mfa       any
			createdAt any
		)
		if err := rows.Scan(&id, &username, &role, &mfa, &createdAt); err != nil {
			return nil, err
		}
		out = append(out, UserDto{
			ID: id, Username: username, Role: UserRole(role).String(),
			MfaEnabled: scanBool(mfa), CreatedAt: isoTime(scanTime(createdAt)),
		})
	}
	return out, rows.Err()
}

// categoryOptions 读取四类字典（与 /api/admin/categories 同源）。
func (a *App) categoryOptions() (CategoryOptionsDto, error) {
	out := CategoryOptionsDto{Groups: []string{}, Platforms: []string{}, Tags: []string{}, LinkTypes: []string{}}
	load := func(table, orderBy string) ([]string, error) {
		rows, err := a.db.sql.Query(`SELECT "Name" FROM "` + table + `" ORDER BY ` + orderBy)
		if err != nil {
			return nil, err
		}
		defer rows.Close()
		items := []string{}
		for rows.Next() {
			var n string
			if err := rows.Scan(&n); err == nil {
				items = append(items, n)
			}
		}
		return items, nil
	}
	var err error
	if out.Groups, err = load("Groups", `"Name"`); err != nil {
		return out, err
	}
	if out.Platforms, err = load("Platforms", `"Name"`); err != nil {
		return out, err
	}
	if out.Tags, err = load("Tags", `"Name"`); err != nil {
		return out, err
	}
	out.LinkTypes, err = load("LinkTypes", `"Id"`)
	return out, err
}

func substrRunes(s string, from, to int) string {
	r := []rune(s)
	if from < 0 || from >= len(r) {
		return ""
	}
	if to > len(r) {
		to = len(r)
	}
	return string(r[from:to])
}
