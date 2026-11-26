package main

import (
	"database/sql"
	"encoding/json"
	"io"
	"net/http"
	"strings"
	"time"
)

// ---------- 规范化（与 C# 侧保持一致） ----------

var knownRetryOn = map[string]bool{
	"empty": true, "format": true, "quota": true, "rate_limit": true,
	"server": true, "stream_end": true, "timeout": true, "network": true,
}

func normalizeProtocol(p string) string {
	v := strings.ToLower(strings.TrimSpace(p))
	switch {
	case v == "anthropic" || v == "claude":
		return "anthropic"
	case v == "gemini" || v == "google":
		return "gemini"
	case v == "":
		return "openai"
	default:
		return "openai"
	}
}

func normalizeRetryPolicy(p string) string {
	v := strings.ToLower(strings.TrimSpace(p))
	if v == "default" || v == "transient" || v == "all" {
		return v
	}
	return "default"
}

func normalizeRetryOn(items []string) string {
	var out []string
	seen := map[string]bool{}
	for _, raw := range items {
		v := strings.ToLower(strings.TrimSpace(raw))
		if knownRetryOn[v] && !seen[v] {
			seen[v] = true
			out = append(out, v)
		}
	}
	if len(out) == 0 {
		return "empty,rate_limit,server,timeout,network"
	}
	return strings.Join(out, ",")
}

// normalizeModels 去空白/去重（忽略大小写）/限长 120/最多 30 个，保留顺序。
func normalizeModels(items []string) []string {
	seen := map[string]bool{}
	out := []string{}
	for _, raw := range items {
		v := strings.TrimSpace(raw)
		if v == "" {
			continue
		}
		if len([]rune(v)) > 120 {
			v = string([]rune(v)[:120])
		}
		key := strings.ToLower(v)
		if seen[key] {
			continue
		}
		seen[key] = true
		out = append(out, v)
		if len(out) >= 30 {
			break
		}
	}
	return out
}

func parseModels(c *LlmConfig) []string {
	if strings.TrimSpace(c.ModelsJson) != "" {
		var arr []string
		if err := json.Unmarshal([]byte(c.ModelsJson), &arr); err == nil {
			if list := normalizeModels(arr); len(list) > 0 {
				return list
			}
		}
	}
	if strings.TrimSpace(c.Model) == "" {
		return []string{}
	}
	return []string{c.Model}
}

func maskAPIKey(key string) string {
	if strings.TrimSpace(key) == "" {
		return ""
	}
	tail := key
	if len(key) > 4 {
		tail = key[len(key)-4:]
	}
	return "****" + tail
}

func toLlmDto(c *LlmConfig) LlmConfigDto {
	hasKey := strings.TrimSpace(c.ApiKey) != ""
	protocol := c.Protocol
	if strings.TrimSpace(protocol) == "" {
		protocol = "openai"
	}
	policy := c.RetryPolicy
	if strings.TrimSpace(policy) == "" {
		policy = "default"
	}
	retryOn := []string{}
	for _, part := range strings.Split(c.RetryOn, ",") {
		if s := strings.TrimSpace(part); s != "" {
			retryOn = append(retryOn, s)
		}
	}
	return LlmConfigDto{
		ID: c.ID, Name: c.Name, Protocol: protocol, RetryPolicy: policy,
		MaxRetries: c.MaxRetries, RetryOn: retryOn,
		ApiEndpoint: c.ApiEndpoint, ApiKey: maskAPIKey(c.ApiKey), Model: c.Model,
		Models: parseModels(c), IsConfigured: c.IsConfigured, IsDefault: c.IsDefault, HasApiKey: hasKey,
	}
}

// ---------- 数据访问 ----------

func (a *App) queryLlmConfigs(where string, args ...any) ([]LlmConfig, error) {
	q := `SELECT "Id","Name","Protocol","RetryPolicy","MaxRetries","RetryOn","ApiEndpoint","ApiKey","Model","ModelsJson","IsConfigured","IsDefault","UpdatedAt" FROM "LlmConfigs"` + where
	rows, err := a.db.sql.Query(a.db.Rebind(q), args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []LlmConfig{}
	for rows.Next() {
		var (
			c                   LlmConfig
			isConfigured, isDef any
			updatedAt           any
		)
		if err := rows.Scan(&c.ID, &c.Name, &c.Protocol, &c.RetryPolicy, &c.MaxRetries, &c.RetryOn,
			&c.ApiEndpoint, &c.ApiKey, &c.Model, &c.ModelsJson, &isConfigured, &isDef, &updatedAt); err != nil {
			return nil, err
		}
		c.IsConfigured = scanBool(isConfigured)
		c.IsDefault = scanBool(isDef)
		c.UpdatedAt = scanTime(updatedAt)
		out = append(out, c)
	}
	return out, rows.Err()
}

func (a *App) getLlmConfig(id int) (*LlmConfig, error) {
	list, err := a.queryLlmConfigs(` WHERE "Id" = ?`, id)
	if err != nil {
		return nil, err
	}
	if len(list) == 0 {
		return nil, nil
	}
	return &list[0], nil
}

// handleListLlmConfigs GET /api/admin/llm/configs
func (a *App) handleListLlmConfigs(w http.ResponseWriter, r *http.Request) {
	list, err := a.queryLlmConfigs(` ORDER BY "IsDefault" DESC, "Id"`)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	out := make([]LlmConfigDto, 0, len(list))
	for i := range list {
		out = append(out, toLlmDto(&list[i]))
	}
	writeOK(w, out)
}

// handleGetLlmConfig GET /api/admin/llm/config（旧接口：返回默认/首个配置）
func (a *App) handleGetLlmConfig(w http.ResponseWriter, r *http.Request) {
	list, err := a.queryLlmConfigs(` ORDER BY "IsDefault" DESC, "Id"`)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	var chosen *LlmConfig
	for i := range list {
		if list[i].IsConfigured {
			chosen = &list[i]
			break
		}
	}
	if chosen == nil && len(list) > 0 {
		chosen = &list[0]
	}
	if chosen == nil {
		writeOK(w, toLlmDto(&LlmConfig{Protocol: "openai", RetryPolicy: "default", MaxRetries: 8, RetryOn: "empty,rate_limit,server,timeout,network"}))
		return
	}
	writeOK(w, toLlmDto(chosen))
}

// handleSaveLlmConfig POST /api/admin/llm/configs（Id=0 新建；密钥留空/掩码表示不修改）
func (a *App) handleSaveLlmConfig(w http.ResponseWriter, r *http.Request) {
	var dto LlmConfigDto
	if err := decodeJSON(r, &dto, 2<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	isNew := dto.ID <= 0
	var cur LlmConfig
	if !isNew {
		existing, err := a.getLlmConfig(dto.ID)
		if err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
		if existing == nil {
			writeErr(w, http.StatusNotFound, "配置不存在")
			return
		}
		cur = *existing
	}

	if strings.TrimSpace(dto.Name) != "" {
		cur.Name = strings.TrimSpace(dto.Name)
	}
	cur.Protocol = normalizeProtocol(dto.Protocol)
	cur.ApiEndpoint = strings.TrimSpace(dto.ApiEndpoint)
	if strings.TrimSpace(dto.ApiKey) != "" && !strings.HasPrefix(dto.ApiKey, "****") {
		cur.ApiKey = strings.TrimSpace(dto.ApiKey)
	}

	models := normalizeModels(dto.Models)
	active := strings.TrimSpace(dto.Model)
	if len(models) == 0 && active != "" {
		models = append(models, active)
	}
	found := false
	for _, m := range models {
		if strings.EqualFold(m, active) {
			active = m
			found = true
			break
		}
	}
	if active == "" || !found {
		if len(models) > 0 {
			active = models[0]
		} else {
			active = ""
		}
	}
	cur.Model = active
	if len(models) == 0 {
		cur.ModelsJson = ""
	} else {
		raw, _ := json.Marshal(models)
		cur.ModelsJson = string(raw)
	}
	cur.RetryPolicy = normalizeRetryPolicy(dto.RetryPolicy)
	cur.MaxRetries = clamp(dto.MaxRetries, 0, 10)
	cur.RetryOn = normalizeRetryOn(dto.RetryOn)
	cur.IsConfigured = strings.TrimSpace(cur.ApiEndpoint) != "" && strings.TrimSpace(cur.ApiKey) != "" && strings.TrimSpace(cur.Model) != ""
	if strings.TrimSpace(cur.Name) == "" {
		if strings.TrimSpace(cur.Model) == "" {
			cur.Name = "未命名配置"
		} else {
			cur.Name = cur.Model
		}
	}

	// 是否还有其它默认项
	var otherDefaults int
	if isNew {
		_ = a.db.sql.QueryRow(`SELECT COUNT(*) FROM "LlmConfigs" WHERE "IsDefault" = ` + a.db.True()).Scan(&otherDefaults)
	} else {
		_ = a.db.sql.QueryRow(a.db.Rebind(`SELECT COUNT(*) FROM "LlmConfigs" WHERE "IsDefault" = `+a.db.True()+` AND "Id" <> ?`), dto.ID).Scan(&otherDefaults)
	}
	cur.IsDefault = dto.IsDefault || (!isNew && cur.IsDefault) || otherDefaults == 0

	now := a.db.now()
	var id int
	if isNew {
		if a.db.pg {
			err := a.db.sql.QueryRow(a.db.Rebind(`INSERT INTO "LlmConfigs"
				("Name","Protocol","RetryPolicy","MaxRetries","RetryOn","ApiEndpoint","ApiKey","Model","ModelsJson","IsConfigured","IsDefault","UpdatedAt")
				VALUES (?,?,?,?,?,?,?,?,?,?,?,?) RETURNING "Id"`),
				cur.Name, cur.Protocol, cur.RetryPolicy, cur.MaxRetries, cur.RetryOn, cur.ApiEndpoint, cur.ApiKey,
				cur.Model, cur.ModelsJson, a.db.BoolValue(cur.IsConfigured), a.db.BoolValue(cur.IsDefault), now).Scan(&id)
			if err != nil {
				writeErr(w, http.StatusInternalServerError, err.Error())
				return
			}
		} else {
			res, err := a.db.sql.Exec(a.db.Rebind(`INSERT INTO "LlmConfigs"
				("Name","Protocol","RetryPolicy","MaxRetries","RetryOn","ApiEndpoint","ApiKey","Model","ModelsJson","IsConfigured","IsDefault","UpdatedAt")
				VALUES (?,?,?,?,?,?,?,?,?,?,?,?)`),
				cur.Name, cur.Protocol, cur.RetryPolicy, cur.MaxRetries, cur.RetryOn, cur.ApiEndpoint, cur.ApiKey,
				cur.Model, cur.ModelsJson, a.db.BoolValue(cur.IsConfigured), a.db.BoolValue(cur.IsDefault), now)
			if err != nil {
				writeErr(w, http.StatusInternalServerError, err.Error())
				return
			}
			last, _ := res.LastInsertId()
			id = int(last)
		}
	} else {
		id = dto.ID
		_, err := a.db.sql.Exec(a.db.Rebind(`UPDATE "LlmConfigs" SET "Name"=?,"Protocol"=?,"RetryPolicy"=?,"MaxRetries"=?,"RetryOn"=?,
			"ApiEndpoint"=?,"ApiKey"=?,"Model"=?,"ModelsJson"=?,"IsConfigured"=?,"IsDefault"=?,"UpdatedAt"=? WHERE "Id"=?`),
			cur.Name, cur.Protocol, cur.RetryPolicy, cur.MaxRetries, cur.RetryOn, cur.ApiEndpoint, cur.ApiKey,
			cur.Model, cur.ModelsJson, a.db.BoolValue(cur.IsConfigured), a.db.BoolValue(cur.IsDefault), now, id)
		if err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
	}
	if cur.IsDefault {
		a.clearOtherDefaults(id)
	}
	saved, _ := a.getLlmConfig(id)
	if saved == nil {
		writeErr(w, http.StatusInternalServerError, "保存后读取失败")
		return
	}
	writeOK(w, toLlmDto(saved))
}

func (a *App) clearOtherDefaults(keepID int) {
	_, _ = a.db.sql.Exec(a.db.Rebind(`UPDATE "LlmConfigs" SET "IsDefault" = ? WHERE "IsDefault" = `+a.db.True()+` AND "Id" <> ?`),
		a.db.BoolValue(false), keepID)
}

// handleDeleteLlmConfig DELETE /api/admin/llm/configs/{id}
func (a *App) handleDeleteLlmConfig(w http.ResponseWriter, r *http.Request) {
	id, ok := pathID(r, "id")
	if !ok {
		writeErr(w, http.StatusNotFound, "配置不存在")
		return
	}
	c, err := a.getLlmConfig(id)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if c == nil {
		writeErr(w, http.StatusNotFound, "配置不存在")
		return
	}
	wasDefault := c.IsDefault
	if _, err := a.db.sql.Exec(a.db.Rebind(`DELETE FROM "LlmConfigs" WHERE "Id" = ?`), id); err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if wasDefault {
		var nextID sql.NullInt64
		_ = a.db.sql.QueryRow(`SELECT "Id" FROM "LlmConfigs" WHERE "IsConfigured" = ` + a.db.True() + ` ORDER BY "Id" LIMIT 1`).Scan(&nextID)
		if !nextID.Valid {
			_ = a.db.sql.QueryRow(`SELECT "Id" FROM "LlmConfigs" ORDER BY "Id" LIMIT 1`).Scan(&nextID)
		}
		if nextID.Valid {
			_, _ = a.db.sql.Exec(a.db.Rebind(`UPDATE "LlmConfigs" SET "IsDefault" = ? WHERE "Id" = ?`), a.db.BoolValue(true), nextID.Int64)
		}
	}
	writeOK(w, map[string]int{"deleted": id})
}

// handleSetDefaultLlmConfig POST /api/admin/llm/configs/{id}/default
func (a *App) handleSetDefaultLlmConfig(w http.ResponseWriter, r *http.Request) {
	id, ok := pathID(r, "id")
	if !ok {
		writeErr(w, http.StatusNotFound, "配置不存在")
		return
	}
	c, err := a.getLlmConfig(id)
	if err != nil || c == nil {
		writeErr(w, http.StatusNotFound, "配置不存在")
		return
	}
	if _, err := a.db.sql.Exec(a.db.Rebind(`UPDATE "LlmConfigs" SET "IsDefault" = ? WHERE "Id" = ?`), a.db.BoolValue(true), id); err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	a.clearOtherDefaults(id)
	writeOK(w, map[string]int{"defaultId": id})
}

// handleSetLlmModel POST /api/admin/llm/configs/{id}/model
func (a *App) handleSetLlmModel(w http.ResponseWriter, r *http.Request) {
	id, ok := pathID(r, "id")
	if !ok {
		writeErr(w, http.StatusNotFound, "配置不存在")
		return
	}
	var req SetModelRequest
	if err := decodeJSON(r, &req, 1<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	c, err := a.getLlmConfig(id)
	if err != nil || c == nil {
		writeErr(w, http.StatusNotFound, "配置不存在")
		return
	}
	target := strings.TrimSpace(req.Model)
	hit := ""
	for _, m := range parseModels(c) {
		if strings.EqualFold(m, target) {
			hit = m
			break
		}
	}
	if hit == "" {
		writeErr(w, http.StatusBadRequest, "该模型不在站点的模型清单中，请先在编辑里添加")
		return
	}
	if _, err := a.db.sql.Exec(a.db.Rebind(`UPDATE "LlmConfigs" SET "Model" = ?, "UpdatedAt" = ? WHERE "Id" = ?`), hit, a.db.now(), id); err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	updated, _ := a.getLlmConfig(id)
	writeOK(w, toLlmDto(updated))
}

// handleLegacySetLlmConfig PUT /api/admin/llm/config
func (a *App) handleLegacySetLlmConfig(w http.ResponseWriter, r *http.Request) {
	list, err := a.queryLlmConfigs(` ORDER BY "IsDefault" DESC, "Id"`)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if len(list) > 0 {
		var body LlmConfigDto
		if err := decodeJSON(r, &body, 2<<20); err != nil {
			writeErr(w, http.StatusBadRequest, err.Error())
			return
		}
		body.ID = list[0].ID
		body.IsDefault = true
		raw, _ := json.Marshal(body)
		r2 := r.Clone(r.Context())
		r2.Body = io.NopCloser(strings.NewReader(string(raw)))
		a.handleSaveLlmConfig(w, r2)
		return
	}
	writeErr(w, http.StatusNotFound, "还没有 LLM 配置")
}

// ---------- 连通性测试与模型列表 ----------

func (a *App) resolveKey(dto LlmConfigDto) string {
	key := strings.TrimSpace(dto.ApiKey)
	if key != "" && !strings.HasPrefix(key, "****") {
		return key
	}
	if dto.ID > 0 {
		if c, _ := a.getLlmConfig(dto.ID); c != nil {
			return c.ApiKey
		}
	}
	list, _ := a.queryLlmConfigs(` WHERE "IsConfigured" = ` + a.db.True() + ` ORDER BY "IsDefault" DESC, "Id"`)
	if len(list) > 0 {
		return list[0].ApiKey
	}
	return ""
}

func baseEndpoint(endpoint string) string {
	e := strings.TrimSpace(endpoint)
	e = strings.TrimSuffix(e, "/")
	for _, suffix := range []string{"/chat/completions", "/completions", "/messages", "/responses"} {
		if strings.HasSuffix(strings.ToLower(e), suffix) {
			return e[:len(e)-len(suffix)]
		}
	}
	return e
}

var llmClient = &http.Client{
	Timeout: 35 * time.Second,
	CheckRedirect: safeLLMRedirect,
	Transport: &http.Transport{
		// 本地模型（Ollama/LM Studio）走回环，必须绕过系统代理
		Proxy:               nil,
		MaxIdleConns:        16,
		MaxIdleConnsPerHost: 4,
		IdleConnTimeout:     90 * time.Second,
	},
}

func llmHTTPClient() *http.Client { return llmClient }

type llmProbeResult struct {
	Ok        bool
	Message   string
	LatencyMs int64
	Models    []string
}

// probeLLM 用 GET {base}/models 探测连通性并尽量列出模型。
func (a *App) probeLLM(endpoint, apiKey, protocol string) llmProbeResult {
	base := baseEndpoint(endpoint)
	if _, err := validateEndpointURL(base); err != nil {
		return llmProbeResult{Message: err.Error()}
	}
	url := base + "/models"
	req, err := http.NewRequest(http.MethodGet, url, nil)
	if err != nil {
		return llmProbeResult{Message: "接口地址无效: " + err.Error()}
	}
	switch normalizeProtocol(protocol) {
	case "anthropic":
		req.Header.Set("x-api-key", apiKey)
		req.Header.Set("anthropic-version", "2023-06-01")
	case "gemini":
		req.Header.Set("x-goog-api-key", apiKey)
	default:
		if apiKey != "" {
			req.Header.Set("Authorization", "Bearer "+apiKey)
		}
	}
	start := time.Now()
	resp, err := llmHTTPClient().Do(req)
	latency := time.Since(start).Milliseconds()
	if err != nil {
		return llmProbeResult{Message: "连接失败: " + err.Error(), LatencyMs: latency}
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(io.LimitReader(resp.Body, 2<<20))
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return llmProbeResult{Message: "HTTP " + itoa(resp.StatusCode) + ": " + truncate(string(body), 300), LatencyMs: latency}
	}

	var parsed struct {
		Data []struct {
			ID          string `json:"id"`
			DisplayName string `json:"display_name"`
		} `json:"data"`
		Models []struct {
			Name        string `json:"name"`
			DisplayName string `json:"displayName"`
		} `json:"models"`
	}
	_ = json.Unmarshal(body, &parsed)
	var models []string
	for _, m := range parsed.Data {
		if strings.TrimSpace(m.ID) != "" {
			models = append(models, m.ID)
		}
	}
	for _, m := range parsed.Models {
		name := strings.TrimPrefix(m.Name, "models/")
		if strings.TrimSpace(name) != "" {
			models = append(models, name)
		}
	}
	return llmProbeResult{Ok: true, Message: "连接成功", LatencyMs: latency, Models: models}
}

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n] + "…"
}

// handleTestLlm POST /api/admin/llm/test
func (a *App) handleTestLlm(w http.ResponseWriter, r *http.Request) {
	var dto LlmConfigDto
	if err := decodeJSON(r, &dto, 2<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	key := a.resolveKey(dto)
	res := a.probeLLM(dto.ApiEndpoint, key, dto.Protocol)
	writeOK(w, map[string]any{"ok": res.Ok, "message": res.Message, "latencyMs": res.LatencyMs})
}

// handleListLlmModels POST /api/admin/llm/models
func (a *App) handleListLlmModels(w http.ResponseWriter, r *http.Request) {
	var dto LlmConfigDto
	if err := decodeJSON(r, &dto, 2<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	key := a.resolveKey(dto)
	res := a.probeLLM(dto.ApiEndpoint, key, dto.Protocol)
	models := make([]LlmModelDto, 0, len(res.Models))
	for _, id := range res.Models {
		models = append(models, LlmModelDto{ID: id})
	}
	writeOK(w, LlmModelsResult{Ok: res.Ok, Message: res.Message, Models: models, LatencyMs: res.LatencyMs})
}
