package main

import (
	"encoding/json"
	"net/http"
	"strings"
	"sync"
)

// linkInfo 是 LLM 对一行文本的识别结果。
type linkInfo struct {
	Title      string
	LinkType   string
	GroupName  string
	Platform   string
	Summary    string
	Tags       []string
	Recognized bool
}

// recognizeLink 让模型把一段文本抽成结构化字段（提示词与 C# 版一致）。
func (a *App) recognizeLink(cfg *LlmConfig, rawText string) linkInfo {
	schema := `{"title":"标题","linkType":"下载直链|官网/普通网址|网盘|磁力","groupName":"分类","platform":"平台(Windows/Android/iOS/Mac/Linux/Web/跨平台等,不确定留空)","tags":["标签1","标签2"],"summary":"一句话摘要"}`
	prompt := "你是链接信息整理助手。根据下面的文本，输出 JSON（只输出 JSON，不要解释）：\n" +
		schema + "\n文本：\n" + rawText +
		"\n规则：无法确定的字段留空字符串，tags 最多 5 个。若文本里没有任何可判断信息，title 用 URL 域名。"

	text, err := a.callChat(cfg, "你是信息抽取助手，只输出合法 JSON。", prompt, 700)
	if err != nil {
		a.logf("链接识别失败：%v", err)
		return linkInfo{}
	}
	var parsed struct {
		Title     string   `json:"title"`
		LinkType  string   `json:"linkType"`
		GroupName string   `json:"groupName"`
		Platform  string   `json:"platform"`
		Summary   string   `json:"summary"`
		Tags      []string `json:"tags"`
	}
	if err := json.Unmarshal([]byte(extractJSONBlock(text)), &parsed); err != nil {
		a.logf("识别结果解析失败：%v", err)
		return linkInfo{}
	}

	out := linkInfo{
		Title:      strings.TrimSpace(parsed.Title),
		LinkType:   strings.TrimSpace(parsed.LinkType),
		GroupName:  strings.TrimSpace(parsed.GroupName),
		Platform:   strings.TrimSpace(parsed.Platform),
		Summary:    strings.TrimSpace(parsed.Summary),
		Recognized: true,
	}
	seen := map[string]bool{}
	for _, t := range parsed.Tags {
		name := strings.TrimSpace(t)
		if name == "" || seen[strings.ToLower(name)] {
			continue
		}
		seen[strings.ToLower(name)] = true
		out.Tags = append(out.Tags, name)
		if len(out.Tags) >= 5 {
			break
		}
	}
	return out
}

// handleParseLinks POST /api/admin/import/links/parse
// 逐行识别粘贴内容，只做预览不落库。未配置 LLM 时按未识别返回（前端仍可确认导入）。
func (a *App) handleParseLinks(w http.ResponseWriter, r *http.Request) {
	var req struct {
		Text string `json:"text"`
	}
	if err := decodeJSON(r, &req, 4<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}

	lines := strings.FieldsFunc(req.Text, func(c rune) bool { return c == '\n' || c == '\r' })
	cleaned := make([]string, 0, len(lines))
	for _, l := range lines {
		if s := strings.TrimSpace(l); s != "" {
			cleaned = append(cleaned, s)
		}
	}
	if len(cleaned) == 0 {
		writeErr(w, http.StatusBadRequest, "请先粘贴链接")
		return
	}
	// 每行一次模型调用，限制数量防止额度被打穿
	if len(cleaned) > 100 {
		writeErr(w, http.StatusBadRequest, "单次最多识别 100 行")
		return
	}

	cfg, err := a.defaultLLMConfig()
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}

	out := make([]map[string]any, len(cleaned))
	fallback := func(i int) map[string]any {
		raw := cleaned[i]
		urlText := extractURL(raw)
		if urlText == "" {
			urlText = raw
		}
		title := extractDomain(urlText)
		if title == "" {
			title = raw
		}
		return map[string]any{
			"rawText": raw, "title": title, "linkType": "官网/普通网址",
			"groupName": "未分类", "platform": "", "summary": "",
			"tags": []string{}, "recognized": false,
		}
	}

	if cfg == nil {
		// 没有可用 LLM：全部按未识别返回，让用户仍能确认导入
		for i := range cleaned {
			out[i] = fallback(i)
		}
		writeOK(w, out)
		return
	}

	// 串行版 100 行要等好几分钟，这里并发 4 路、按原序回填
	const workers = 4
	sem := make(chan struct{}, workers)
	results := make([]linkInfo, len(cleaned))
	var wg sync.WaitGroup
	for i := range cleaned {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			sem <- struct{}{}
			defer func() { <-sem }()
			results[i] = a.recognizeLink(cfg, cleaned[i])
		}(i)
	}
	wg.Wait()

	for i := range cleaned {
		info := results[i]
		item := fallback(i)
		if info.Recognized {
			item["recognized"] = true
			if info.Title != "" {
				item["title"] = info.Title
			}
			if info.LinkType != "" {
				item["linkType"] = info.LinkType
			}
			if info.GroupName != "" {
				item["groupName"] = info.GroupName
			}
			item["platform"] = info.Platform
			item["summary"] = info.Summary
			item["tags"] = info.Tags
			if item["tags"] == nil {
				item["tags"] = []string{}
			}
		}
		out[i] = item
	}
	writeOK(w, out)
}
