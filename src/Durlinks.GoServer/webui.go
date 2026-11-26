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

//go:embed web/templates/*.html web/static
var webAssets embed.FS

var indexTemplate = template.Must(template.New("index.html").Funcs(template.FuncMap{
	"short": shortURL,
}).ParseFS(webAssets, "web/templates/index.html"))

// shortURL 显示用截断，完整地址仍在 href 里。
func shortURL(u string) string {
	r := []rune(u)
	if len(r) > 60 {
		return string(r[:60]) + "…"
	}
	return u
}

// linkView 是模板用的扁平结构：LinkDto 里大量 *string，直接进模板会输出指针地址。
type linkView struct {
	Title             string
	LinkType          string
	GroupName         string
	Platform          string
	Version           string
	Summary           string
	ExtractCode       string
	ExpireAt          string
	AllURLs           []string
	SubLinks          []SubLinkDto
	Tags              []string
	IsAI              bool
	ImportSourceLabel string
}

// webData 是用户端首页的渲染上下文。Link() 用来在保留当前筛选的前提下生成链接。
type webData struct {
	Q        string
	Group    string
	Tag      string
	Platform string
	Source   string

	Page       int
	Total      int
	TotalPages int
	MaxPage    int
	PrevPage   int
	NextPage   int

	Items      []linkView
	Filters    FilterOptionsDto
	Libraries  []LibraryDto
	SelLibrary int
	LibName    string

	base url.Values
}

// Link 生成带当前查询条件的 URL；传 ("libraryId", 0) 表示去掉该参数，page 默认清空后按需重设。
func (d webData) Link(kv ...any) string {
	v := url.Values{}
	for k, vals := range d.base {
		for _, x := range vals {
			v.Add(k, x)
		}
	}
	v.Del("page")
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
		return "/"
	}
	return "/?" + v.Encode()
}

func toLinkView(it LinkDto) linkView {
	v := linkView{
		Title: it.Title, LinkType: it.LinkType, GroupName: it.GroupName,
		Platform: ptrStr(it.Platform), Version: ptrStr(it.Version),
		Summary: ptrStr(it.Summary), ExtractCode: ptrStr(it.ExtractCode), ExpireAt: ptrStr(it.ExpireAt),
		SubLinks: it.SubLinks, Tags: it.Tags,
		IsAI:              it.ImportSource == ImportAI,
		ImportSourceLabel: it.ImportSourceLabel,
	}
	// 主 URL 与 LinkUrls 去重后一起展示
	seen := map[string]bool{}
	if strings.TrimSpace(it.URL) != "" {
		v.AllURLs = append(v.AllURLs, it.URL)
		seen[it.URL] = true
	}
	for _, u := range it.Urls {
		if u != "" && !seen[u] {
			v.AllURLs = append(v.AllURLs, u)
			seen[u] = true
		}
	}
	return v
}

// handleWebIndex GET / —— 服务端直出搜索页，交互靠普通表单与链接，不依赖任何前端框架。
func (a *App) handleWebIndex(w http.ResponseWriter, r *http.Request) {
	qs := r.URL.Query()
	page := queryInt(r, "page", 1)
	if page < 1 {
		page = 1
	}
	source := strings.TrimSpace(qs.Get("source"))

	lq := LinkQuery{
		Q:         strings.TrimSpace(qs.Get("q")),
		Group:     strings.TrimSpace(qs.Get("group")),
		Tag:       strings.TrimSpace(qs.Get("tag")),
		Platform:  strings.TrimSpace(qs.Get("platform")),
		Channel:   "web",
		Page:      page,
		PageSize:  20,
		LibraryID: queryIntPtr(r, "libraryId"),
	}
	if source == "0" || source == "1" {
		if n, err := strconv.Atoi(source); err == nil {
			s := ImportSource(n)
			lq.ImportSource = &s
		}
	}

	items, total, err := a.db.queryLinks(lq, true)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, "查询失败："+err.Error())
		return
	}

	totalPages := 0
	if lq.PageSize > 0 {
		totalPages = (total + lq.PageSize - 1) / lq.PageSize
	}
	maxPage := totalPages
	if maxPage < 1 {
		maxPage = 1
	}
	// 页码越界时夹回有效范围（否则空列表配「第 99/3 页」很怪）
	if page > maxPage {
		page = maxPage
	}

	libs, _ := a.publicLibraries("web")
	filters, _ := a.publicFilterOptions("web")

	data := webData{
		Q: lq.Q, Group: lq.Group, Tag: lq.Tag, Platform: lq.Platform, Source: source,
		Page: page, Total: total, TotalPages: totalPages, MaxPage: maxPage,
		PrevPage: page - 1, NextPage: page + 1,
		Filters: filters, Libraries: libs,
		base: qs,
	}
	if lq.LibraryID != nil {
		data.SelLibrary = *lq.LibraryID
		for _, l := range libs {
			if l.ID == *lq.LibraryID {
				data.LibName = l.Name
				break
			}
		}
	}
	for _, it := range items {
		data.Items = append(data.Items, toLinkView(it))
	}

	var buf bytes.Buffer
	if err := indexTemplate.Execute(&buf, data); err != nil {
		a.logf("渲染用户端首页失败：%v", err)
		writeErr(w, http.StatusInternalServerError, "页面渲染失败")
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-cache")
	_, _ = w.Write(buf.Bytes())
}

// handleWebAssets 提供 /assets/ 下的内嵌静态资源（css / favicon）。
func (a *App) handleWebAssets(w http.ResponseWriter, r *http.Request) {
	rel := strings.TrimPrefix(r.URL.Path, "/assets/")
	rel = strings.TrimPrefix(rel, "/")
	if rel == "" || strings.Contains(rel, "..") {
		http.NotFound(w, r)
		return
	}
	data, err := webAssets.ReadFile("web/static/" + rel)
	if err != nil {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Content-Type", contentTypeFor(rel))
	w.Header().Set("Cache-Control", "public, max-age=3600")
	w.Header().Set("Content-Length", itoa(len(data)))
	_, _ = w.Write(data)
}
