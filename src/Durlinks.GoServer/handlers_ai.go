package main

import (
	"bytes"
	"database/sql"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"sync"
	"time"
)

// ======================= LLM 调用（OpenAI 兼容协议） =======================

// chatURL 把用户填的 endpoint 归一成 chat/completions 地址。
// 兼容三种填法：…/v1、…/v1/chat/completions、以及只填域名（自动补 /v1）。
func chatURL(endpoint string) string {
	e := strings.TrimSuffix(strings.TrimSpace(endpoint), "/")
	low := strings.ToLower(e)
	if strings.HasSuffix(low, "/chat/completions") {
		return e
	}
	if strings.HasSuffix(low, "/v1") || strings.HasSuffix(low, "/v1beta") {
		return e + "/chat/completions"
	}
	return e + "/v1/chat/completions"
}

type chatChoice struct {
	Message struct {
		Content string `json:"content"`
	} `json:"message"`
}

type chatResponse struct {
	Choices []chatChoice `json:"choices"`
	Error   *struct {
		Message string `json:"message"`
	} `json:"error"`
}

// callChat 发一次 chat 请求，返回模型输出的原文。
func (a *App) callChat(cfg *LlmConfig, systemPrompt, userPrompt string, maxTokens int) (string, error) {
	if cfg == nil {
		return "", fmt.Errorf("未配置 LLM")
	}
	if !cfg.IsConfigured || strings.TrimSpace(cfg.ApiEndpoint) == "" || strings.TrimSpace(cfg.Model) == "" {
		return "", fmt.Errorf("LLM 尚未配置完成（需要接口地址、密钥与模型）")
	}
	if maxTokens <= 0 {
		maxTokens = 800
	}
	body := map[string]any{
		"model":       cfg.Model,
		"messages":    []map[string]string{{"role": "system", "content": systemPrompt}, {"role": "user", "content": userPrompt}},
		"temperature": 0.3,
		"max_tokens":  maxTokens,
	}
	if normalizeProtocol(cfg.Protocol) == "openai" {
		// 多数兼容站支持 JSON 模式；不支持时会忽略该字段
		body["response_format"] = map[string]string{"type": "json_object"}
	}
	raw, err := json.Marshal(body)
	if err != nil {
		return "", err
	}
	req, err := http.NewRequest(http.MethodPost, chatURL(cfg.ApiEndpoint), bytes.NewReader(raw))
	if err != nil {
		return "", err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", "Bearer "+cfg.ApiKey)

	resp, err := llmHTTPClient().Do(req)
	if err != nil {
		return "", fmt.Errorf("请求失败: %w", err)
	}
	defer resp.Body.Close()
	payload, _ := io.ReadAll(io.LimitReader(resp.Body, 4<<20))
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return "", fmt.Errorf("HTTP %d: %s", resp.StatusCode, truncate(string(payload), 300))
	}
	var parsed chatResponse
	if err := json.Unmarshal(payload, &parsed); err != nil {
		return "", fmt.Errorf("响应不是合法 JSON: %w", err)
	}
	if parsed.Error != nil && parsed.Error.Message != "" {
		return "", fmt.Errorf("模型返回错误: %s", parsed.Error.Message)
	}
	if len(parsed.Choices) == 0 {
		return "", fmt.Errorf("模型没有返回内容")
	}
	return parsed.Choices[0].Message.Content, nil
}

// extractJSONBlock 兼容 ```json 包裹与前后夹带说明文字。
func extractJSONBlock(text string) string {
	s := strings.TrimSpace(text)
	if i := strings.Index(s, "```"); i >= 0 {
		s = s[i+3:]
		s = strings.TrimPrefix(s, "json")
		s = strings.TrimPrefix(s, "JSON")
		if j := strings.Index(s, "```"); j >= 0 {
			s = s[:j]
		}
		s = strings.TrimSpace(s)
	}
	start := strings.Index(s, "{")
	end := strings.LastIndex(s, "}")
	if start >= 0 && end > start {
		return s[start : end+1]
	}
	return s
}

type enrichResult struct {
	Tags    []string
	Summary string
}

// enrichOne 为单条链接生成标签与摘要（提示词与 C# 版一致）。
func (a *App) enrichOne(cfg *LlmConfig, title, linkURL, remark string) (enrichResult, error) {
	schema := `{"tags":["标签1","标签2","标签3"],"summary":"一句话摘要"}`
	prompt := "为下面的链接生成标签和一句话摘要，输出 JSON（只输出 JSON）：\n" +
		schema + "\n名称：" + title + "\n链接：" + linkURL + "\n备注：" + remark +
		"\n标签最多5个，摘要不超过50字。"

	text, err := a.callChat(cfg, "你是信息整理助手，只输出合法 JSON。", prompt, 600)
	if err != nil {
		return enrichResult{}, err
	}
	var parsed struct {
		Tags    []string `json:"tags"`
		Summary string   `json:"summary"`
	}
	if err := json.Unmarshal([]byte(extractJSONBlock(text)), &parsed); err != nil {
		return enrichResult{}, fmt.Errorf("解析模型输出失败: %w", err)
	}
	out := enrichResult{Summary: strings.TrimSpace(parsed.Summary)}
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
	return out, nil
}

// defaultLLMConfig 取默认可用配置：优先 IsDefault，其次最早创建。
func (a *App) defaultLLMConfig() (*LlmConfig, error) {
	list, err := a.queryLlmConfigs(` WHERE "IsConfigured" = ` + a.db.True() + ` ORDER BY "IsDefault" DESC, "Id" LIMIT 1`)
	if err != nil {
		return nil, err
	}
	if len(list) == 0 {
		return nil, nil
	}
	return &list[0], nil
}

// applyEnrich 把补料结果写回链接（标签只增不删，摘要为空时不覆盖）。
func (a *App) applyEnrich(linkID int, r enrichResult) error {
	tx, err := a.db.sql.Begin()
	if err != nil {
		return err
	}
	defer tx.Rollback()

	if r.Summary != "" {
		if _, err := tx.Exec(a.db.Rebind(`UPDATE "Links" SET "Summary" = ? WHERE "Id" = ?`), r.Summary, linkID); err != nil {
			return err
		}
	}
	// 已有关联标签先查出来，避免复合主键冲突（C# 版正是踩了这个坑）
	existing := map[string]bool{}
	rows, err := tx.Query(a.db.Rebind(`SELECT tg."Name" FROM "LinkTags" lt JOIN "Tags" tg ON tg."Id"=lt."TagId" WHERE lt."LinkId" = ?`), linkID)
	if err != nil {
		return err
	}
	for rows.Next() {
		var n string
		if err := rows.Scan(&n); err == nil {
			existing[strings.ToLower(n)] = true
		}
	}
	rows.Close()

	for _, name := range r.Tags {
		if existing[strings.ToLower(name)] {
			continue
		}
		existing[strings.ToLower(name)] = true
		var tagID int
		if err := tx.QueryRow(a.db.Rebind(`SELECT "Id" FROM "Tags" WHERE "Name" = ?`), name).Scan(&tagID); err == sql.ErrNoRows {
			if _, err := tx.Exec(a.db.Rebind(`INSERT INTO "Tags" ("Name") VALUES (?) ON CONFLICT ("Name") DO NOTHING`), name); err != nil {
				return err
			}
			if err := tx.QueryRow(a.db.Rebind(`SELECT "Id" FROM "Tags" WHERE "Name" = ?`), name).Scan(&tagID); err != nil {
				return err
			}
		} else if err != nil {
			return err
		}
		if _, err := tx.Exec(a.db.Rebind(`INSERT INTO "LinkTags" ("LinkId","TagId") VALUES (?,?) ON CONFLICT ("LinkId","TagId") DO NOTHING`), linkID, tagID); err != nil {
			return err
		}
	}
	return tx.Commit()
}

// handleEnrich POST /api/admin/ai/enrich/{id}
func (a *App) handleEnrich(w http.ResponseWriter, r *http.Request) {
	id, ok := pathID(r, "id")
	if !ok {
		writeErr(w, http.StatusNotFound, "链接不存在")
		return
	}
	var (
		title, linkURL string
		remark         sql.NullString
	)
	err := a.db.sql.QueryRow(a.db.Rebind(`SELECT "Title","Url",COALESCE("Remark",'') FROM "Links" WHERE "Id" = ? AND "IsDeleted" = `+a.db.False()), id).
		Scan(&title, &linkURL, &remark)
	if err == sql.ErrNoRows {
		writeErr(w, http.StatusNotFound, "链接不存在")
		return
	}
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}

	cfg, err := a.defaultLLMConfig()
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if cfg == nil {
		writeErr(w, http.StatusBadRequest, "尚未配置可用的 LLM，请先到「LLM 设置」填写接口与密钥")
		return
	}

	result, err := a.enrichOne(cfg, title, linkURL, remark.String)
	if err != nil {
		writeErr(w, http.StatusBadGateway, "AI 补料失败："+err.Error())
		return
	}
	if err := a.applyEnrich(id, result); err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeOK(w, map[string]any{"tags": result.Tags, "summary": result.Summary})
}

// ======================= 批量补料（后台任务） =======================

type batchEnricher struct {
	mu     sync.Mutex
	status BatchEnrichStatus
	cancel bool
}

var globalBatch = &batchEnricher{}

func (b *batchEnricher) snapshot() BatchEnrichStatus {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.status
}

func (b *batchEnricher) start(mode string) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.status.Running {
		return false
	}
	now := isoTime(time.Now())
	b.status = BatchEnrichStatus{Running: true, Mode: mode, Message: "准备中…", StartedAt: &now}
	b.cancel = false
	return true
}

func (b *batchEnricher) requestCancel() bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	if !b.status.Running {
		return false
	}
	b.cancel = true
	b.status.CancelRequested = true
	b.status.Message = "正在取消…"
	return true
}

func (b *batchEnricher) isCancelled() bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.cancel
}

func (b *batchEnricher) update(fn func(*BatchEnrichStatus)) {
	b.mu.Lock()
	defer b.mu.Unlock()
	fn(&b.status)
}

// handleStartBatchEnrich POST /api/admin/ai/enrich/batch
func (a *App) handleStartBatchEnrich(w http.ResponseWriter, r *http.Request) {
	var req BatchEnrichRequest
	if err := decodeJSON(r, &req, 1<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	cfg, err := a.defaultLLMConfig()
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if cfg == nil {
		writeErr(w, http.StatusBadRequest, "尚未配置可用的 LLM，请先到「LLM 设置」填写接口与密钥")
		return
	}

	mode := strings.ToLower(strings.TrimSpace(req.Mode))
	if mode != "all" {
		mode = "missing"
	}
	limit := req.Limit
	if limit <= 0 {
		limit = 100
	}
	if limit > 200000 {
		limit = 200000
	}
	concurrency := req.Concurrency
	if concurrency <= 0 {
		concurrency = 3
	}
	concurrency = clamp(concurrency, 1, 8)

	if !globalBatch.start(mode) {
		writeJSON(w, http.StatusConflict, map[string]any{"message": "已有批量补料任务正在运行", "status": globalBatch.snapshot()})
		return
	}

	go func() {
		defer func() {
			if rec := recover(); rec != nil {
				globalBatch.update(func(s *BatchEnrichStatus) {
					s.Running = false
					s.Message = fmt.Sprintf("任务异常：%v", rec)
					now := isoTime(time.Now())
					s.FinishedAt = &now
				})
			}
		}()
		a.runBatchEnrich(cfg, mode, limit, concurrency, req.LibraryID)
	}()

	writeOK(w, map[string]any{"started": true, "status": globalBatch.snapshot()})
}

// runBatchEnrich 拉取候选链接后按并发度消费。
func (a *App) runBatchEnrich(cfg *LlmConfig, mode string, limit, concurrency, libraryID int) {
	cond := `"IsDeleted" = ` + a.db.False()
	if libraryID > 0 {
		cond += fmt.Sprintf(` AND "LibraryId" = %d`, libraryID)
	}
	if mode == "missing" {
		// 摘要为空 或 没有任何标签
		cond += ` AND (COALESCE("Summary",'') = '' OR NOT EXISTS (SELECT 1 FROM "LinkTags" lt WHERE lt."LinkId" = "Links"."Id"))`
	}
	rows, err := a.db.sql.Query(a.db.Rebind(`SELECT "Id" FROM "Links" WHERE `+cond+` ORDER BY "SortOrder","Id" LIMIT ?`), limit)
	if err != nil {
		globalBatch.update(func(s *BatchEnrichStatus) {
			s.Running = false
			s.Message = "查询待补料链接失败：" + err.Error()
			now := isoTime(time.Now())
			s.FinishedAt = &now
		})
		return
	}
	var ids []int
	for rows.Next() {
		var id int
		if err := rows.Scan(&id); err == nil {
			ids = append(ids, id)
		}
	}
	rows.Close()

	globalBatch.update(func(s *BatchEnrichStatus) {
		s.Total = len(ids)
		s.Message = fmt.Sprintf("共 %d 条待补料", len(ids))
	})
	if len(ids) == 0 {
		globalBatch.update(func(s *BatchEnrichStatus) {
			s.Running = false
			s.Message = "没有需要补料的链接"
			now := isoTime(time.Now())
			s.FinishedAt = &now
		})
		return
	}

	jobs := make(chan int)
	var wg sync.WaitGroup
	for i := 0; i < concurrency; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for id := range jobs {
				if globalBatch.isCancelled() {
					return
				}
				var (
					title, linkURL string
					remark         sql.NullString
				)
				qerr := a.db.sql.QueryRow(a.db.Rebind(`SELECT "Title","Url",COALESCE("Remark",'') FROM "Links" WHERE "Id" = ?`), id).
					Scan(&title, &linkURL, &remark)
				if qerr != nil {
					globalBatch.update(func(s *BatchEnrichStatus) {
						s.Processed++
						s.Failed++
					})
					continue
				}
				res, eerr := a.enrichOne(cfg, title, linkURL, remark.String)
				if eerr != nil {
					a.logf("批量补料失败 id=%d: %v", id, eerr)
					globalBatch.update(func(s *BatchEnrichStatus) {
						s.Processed++
						s.Failed++
						s.Message = fmt.Sprintf("进行中… %d/%d（失败 %d）", s.Processed, s.Total, s.Failed)
					})
					continue
				}
				aerr := a.applyEnrich(id, res)
				globalBatch.update(func(s *BatchEnrichStatus) {
					s.Processed++
					if aerr != nil {
						s.Failed++
					} else if res.Summary == "" && len(res.Tags) == 0 {
						s.Skipped++
					} else {
						s.Succeeded++
					}
					s.Message = fmt.Sprintf("进行中… %d/%d", s.Processed, s.Total)
				})
			}
		}()
	}
	for _, id := range ids {
		if globalBatch.isCancelled() {
			break
		}
		jobs <- id
	}
	close(jobs)
	wg.Wait()

	globalBatch.update(func(s *BatchEnrichStatus) {
		s.Running = false
		now := isoTime(time.Now())
		s.FinishedAt = &now
		if s.CancelRequested {
			s.Message = fmt.Sprintf("已取消：处理 %d 条，成功 %d 条", s.Processed, s.Succeeded)
		} else {
			s.Message = fmt.Sprintf("完成：处理 %d 条，成功 %d，失败 %d，跳过 %d", s.Processed, s.Succeeded, s.Failed, s.Skipped)
		}
	})
}

// handleBatchEnrichStatus GET /api/admin/ai/enrich/batch/status
func (a *App) handleBatchEnrichStatus(w http.ResponseWriter, r *http.Request) {
	writeOK(w, globalBatch.snapshot())
}

// handleCancelBatchEnrich POST /api/admin/ai/enrich/batch/cancel
func (a *App) handleCancelBatchEnrich(w http.ResponseWriter, r *http.Request) {
	ok := globalBatch.requestCancel()
	writeOK(w, map[string]any{"requested": ok, "status": globalBatch.snapshot()})
}
