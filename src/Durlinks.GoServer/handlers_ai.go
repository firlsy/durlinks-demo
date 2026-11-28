package main

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"regexp"
	"sort"
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

// llmThrottle 全局最小请求间隔，避免批量补料以最大并发猛打 LLM API 耗尽额度。
var llmThrottle struct {
	mu     sync.Mutex
	lastAt time.Time
}

func (a *App) throttleLLM(ctx context.Context) error {
	minInterval := 100 * time.Millisecond
	if a.cfg != nil && a.cfg.BatchEnrich.MinIntervalMs > 0 {
		minInterval = time.Duration(a.cfg.BatchEnrich.MinIntervalMs) * time.Millisecond
	}
	if minInterval <= 0 {
		return nil
	}
	// 锁内只计算本次应等待到的时间点，绝不在持锁时 sleep，
	// 避免单个长等待阻塞所有 LLM 请求。
	llmThrottle.mu.Lock()
	wait := minInterval - time.Since(llmThrottle.lastAt)
	llmThrottle.lastAt = time.Now().Add(wait)
	llmThrottle.mu.Unlock()
	if wait > 0 {
		timer := time.NewTimer(wait)
		defer timer.Stop()
		select {
		case <-timer.C:
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	return nil
}

// callChat 发一次 chat 请求，返回模型输出的原文。
func (a *App) callChat(ctx context.Context, cfg *LlmConfig, systemPrompt, userPrompt string, maxTokens int) (string, error) {
	if cfg == nil {
		return "", fmt.Errorf("未配置 LLM")
	}
	if err := a.throttleLLM(ctx); err != nil {
		return "", err
	}
	if !cfg.IsConfigured || strings.TrimSpace(cfg.ApiEndpoint) == "" || strings.TrimSpace(cfg.Model) == "" {
		return "", fmt.Errorf("LLM 尚未配置完成（需要接口地址、密钥与模型）")
	}
	if _, err := validateEndpointURL(cfg.ApiEndpoint); err != nil {
		return "", fmt.Errorf("LLM 接口地址被拒绝：%v", err)
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
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, chatURL(cfg.ApiEndpoint), bytes.NewReader(raw))
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

var (
	tagBareVersionRE = regexp.MustCompile(`^(?:\d+(?:\.\d+)*|版本\d+(?:\.\d+)*)$`)
	// 组织名、站点名、状态词都不适合做内容标签；它们的存在会让标签库迅速膨胀。
	tagNoiseRE = regexp.MustCompile(`(?i)(基金会|社区|有限公司|股份|科技|软件|ltd|inc|llc|gmbh|co\.?kg|corporation|company|team|project|foundation|systems|solutions|official|官方|https?|www\.|镜像站|镜像源|下载站)`)
	tagGenericRE = regexp.MustCompile(`(?i)^(下载|镜像|镜像站|官网|普通网址|网址|链接|网络|repo|org|com|net|html|iso|img|vhd|qcow2|下载镜像|安装镜像|系统安装镜像|启动安装镜像|云服务器镜像|最新|稳定版|正式版|滚动版|有效|待验证|已归档|需授权|常用|linux发行版|linux 发行版|dvd)$`)
	// 把长期积累的同义/带组织后缀标签收敛到统一名称。
	tagCanonical = map[string]string{
		"arm64": "aarch64", "amd64": "x86_64", "x64": "x86_64", "x86-64": "x86_64",
		"debianproject（spi）": "Debian", "debianproject(spi)": "Debian",
		"canonical ltd.": "Ubuntu", "canonical ltd": "Ubuntu", "canonicalltd.": "Ubuntu", "canonicalltd": "Ubuntu",
		"openatomopeneuler（开放原子开源基金会）": "openEuler",
		"rockyenterprisesoftwarefoundation": "Rocky Linux",
		"almalinuxosfoundation": "AlmaLinux",
		"archlinux社区": "Arch Linux",
		"thefreebsdproject": "FreeBSD",
		"fedoraproject（redhat赞助）": "Fedora",
		"linuxmintteam": "Linux Mint",
		"suse/opensuseproject": "openSUSE",
		"麒麟软件有限公司": "Kylin",
		"redhat/centosproject": "CentOS",
		"腾讯云": "Tencent Cloud",
		"ixsystems·inc.": "TrueNAS",
		"武汉深之度科技有限公司": "Deepin",
		"统信软件技术有限公司": "UOS",
		"proxmoxserversolutionsgmbh": "Proxmox VE",
		"offensivesecurity": "Kali Linux",
		"阿里云计算有限公司": "Alibaba Cloud Linux",
		"manjarogmbh&co.kg": "Manjaro",
		"飞牛网络科技（深圳）有限公司": "飞牛 NAS",
		"dvd": "DVD ISO", "dvd镜像": "DVD ISO", "dvd安装镜像": "DVD ISO", "dvd iso": "DVD ISO",
		"iso镜像": "ISO", "iso": "ISO",
		"netinst安装镜像": "Netinst ISO", "网络安装镜像": "Netinst ISO", "网络安装": "Netinst ISO",
		"minimal安装镜像": "Minimal ISO", "最小化安装": "Minimal ISO", "最小安装": "Minimal ISO",
		"最小安装iso": "Minimal ISO", "minimal镜像": "Minimal ISO", "minimal iso": "Minimal ISO",
		"almalinux 10.2": "AlmaLinux 10", "almalinux 10.3": "AlmaLinux 10",
		"alibaba cloud linux 2": "Alibaba Cloud Linux", "alibaba cloud linux 3": "Alibaba Cloud Linux",
		"alibaba cloud linux 4": "Alibaba Cloud Linux",
	}
)

// cleanTag 先做规范名映射，再做空白/标点清理；映射必须在噪声过滤之前，
// 否则「ArchLinux社区」这类标签会被整条丢弃，而不是收敛成「Arch Linux」。
func cleanTag(raw string) string {
	name := strings.TrimSpace(raw)
	name = strings.Trim(name, `"'“”‘’[](){}，,。;；:：`)
	name = strings.Join(strings.Fields(name), " ")
	if name == "" {
		return ""
	}
	if canonical, ok := tagCanonical[strings.ToLower(name)]; ok {
		return canonical
	}
	return name
}

// normalizeTag 判断一个标签是否应该进入标签库，并返回规范名；返回空表示丢弃。
func normalizeTag(raw string) string {
	name := cleanTag(raw)
	if name == "" {
		return ""
	}
	if tagBareVersionRE.MatchString(name) || tagNoiseRE.MatchString(name) || tagGenericRE.MatchString(name) {
		return ""
	}
	if strings.ContainsAny(name, "/\\") || strings.Contains(name, "://") {
		return ""
	}
	return name
}

// tagWorkflowKeep 是人工维护的状态标签，不属于 LLM 内容标签；整理历史标签时必须保留。
var tagWorkflowKeep = map[string]bool{
	"待验证": true, "稳定版": true, "已归档": true, "有效": true,
	"需授权": true, "正式版": true, "滚动版": true, "最新": true, "常用": true, "商业版": true,
}

// normalizeExistingTag 与 normalizeTag 的区别：保留人工状态标签，不把它们当成噪声删除。
func normalizeExistingTag(raw string) string {
	name := strings.TrimSpace(raw)
	if tagWorkflowKeep[strings.ToLower(name)] {
		return name
	}
	return normalizeTag(raw)
}

// normalizeTags 把模型给出的标签收敛成少量、规范、互不包含的标签。
func normalizeTags(raw []string, max int) []string {
	type candidate struct {
		name string
		key  string
	}
	var candidates []candidate
	seen := map[string]bool{}
	for _, t := range raw {
		name := normalizeTag(t)
		if name == "" {
			continue
		}
		key := strings.ToLower(name)
		if seen[key] {
			continue
		}
		seen[key] = true
		candidates = append(candidates, candidate{name: name, key: key})
	}

	// 互相包含的标签只留更具体的一个：AlmaLinux + AlmaLinux 10 => AlmaLinux 10。
	kept := make([]candidate, 0, len(candidates))
	for i, c := range candidates {
		redundant := false
		for j, other := range candidates {
			if i == j || len(other.key) <= len(c.key) {
				continue
			}
			if strings.HasPrefix(other.key, c.key+" ") {
				redundant = true
				break
			}
		}
		if !redundant {
			kept = append(kept, c)
		}
	}
	if max > 0 && len(kept) > max {
		kept = kept[:max]
	}
	out := make([]string, 0, len(kept))
	for _, c := range kept {
		out = append(out, c.name)
	}
	return out
}

// enrichOne 为单条链接生成标签与摘要。
func (a *App) enrichOne(ctx context.Context, cfg *LlmConfig, title, linkURL, remark string) (enrichResult, error) {
	schema := `{"tags":["标签1","标签2","标签3"],"summary":"一句话摘要"}`
	prompt := "请依据【名称】和【链接指向的实际内容】为这条下载链接生成标签与摘要。\n" +
		"标签规则：\n" +
		"1. 最多4个短标签，只描述资源本身：项目/发行版名（AlmaLinux、Debian、Windows、Chrome）、大版本（AlmaLinux 10，禁止单独写 10）、架构（x86_64、aarch64）、镜像或安装包类型（DVD ISO、Netinst ISO、Minimal ISO、QCOW2、VHD）。\n" +
		"2. 不要输出公司名、基金会、社区、项目组、域名、镜像站名、状态词（稳定/最新/待验证/有效/归档）、孤立数字或泛词（下载、镜像、官网、Linux发行版）。\n" +
		"3. 名称优先，链接只用于补充确认版本、架构和镜像形态；两者不一致时以名称为准。\n" +
		"4. 标签之间不要互相包含，同类只保留最具体的一个。\n" +
		"摘要：不超过40字，说明这是什么资源，尽量包含项目名、版本、架构或镜像类型中可确认的信息。\n" +
		"只输出 JSON：" + schema + "\n\n名称：" + title + "\n链接：" + linkURL + "\n备注：" + remark

	text, err := a.callChat(ctx, cfg, "你是软件发行版与下载资源整理助手，只输出合法 JSON，不要解释。", prompt, 600)
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
	return enrichResult{
		Tags:    normalizeTags(parsed.Tags, 4),
		Summary: strings.TrimSpace(parsed.Summary),
	}, nil
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

// applyEnrich 把补料结果写回链接（标签只增不删）。
// overwriteSummary 控制是否覆盖已有摘要：用户主动单条补料/批量 all 为 true；
// 批量 missing 必须为 false，否则会静默覆盖用户手写的摘要（数据丢失）。
func (a *App) applyEnrich(linkID int, r enrichResult, overwriteSummary bool) error {
	tx, err := a.db.sql.Begin()
	if err != nil {
		return err
	}
	defer tx.Rollback()

	if r.Summary != "" {
		summarySQL := `UPDATE "Links" SET "Summary" = ? WHERE "Id" = ?`
		if !overwriteSummary {
			// 仅当原摘要为空时才写入，绝不触碰用户手写内容
			summarySQL = `UPDATE "Links" SET "Summary" = ? WHERE "Id" = ? AND COALESCE("Summary",'') = '' AND "IsDeleted" = ` + a.db.False()
		}
		if _, err := tx.Exec(a.db.Rebind(summarySQL), r.Summary, linkID); err != nil {
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
		writeInternalErr(w, err)
		return
	}

	cfg, err := a.defaultLLMConfig()
	if err != nil {
		writeInternalErr(w, err)
		return
	}
	if cfg == nil {
		writeErr(w, http.StatusBadRequest, "尚未配置可用的 LLM，请先到「LLM 设置」填写接口与密钥")
		return
	}

	result, err := a.enrichOne(r.Context(), cfg, title, linkURL, remark.String)
	if err != nil {
		writeErr(w, http.StatusBadGateway, "AI 补料失败："+err.Error())
		return
	}
	if err := a.applyEnrich(id, result, true); err != nil {
		writeInternalErr(w, err)
		return
	}
	writeOK(w, map[string]any{"tags": result.Tags, "summary": result.Summary})
}

// ======================= 批量补料（后台任务） =======================

type batchEnricher struct {
	mu         sync.Mutex
	status     BatchEnrichStatus
	cancel     bool
	cancelFn   context.CancelFunc
	jobContext context.Context
}

var globalBatch = &batchEnricher{}

func (b *batchEnricher) snapshot() BatchEnrichStatus {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.status
}

func (b *batchEnricher) start(mode string) (context.Context, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.status.Running {
		return nil, false
	}
	now := isoTime(time.Now())
	b.status = BatchEnrichStatus{Running: true, Mode: mode, Message: "准备中…", StartedAt: &now}
	b.cancel = false
	ctx, cf := context.WithCancel(context.Background())
	b.cancelFn = cf
	b.jobContext = ctx
	return ctx, true
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
	if b.cancelFn != nil {
		b.cancelFn() // 立即中断在途 LLM HTTP 请求
	}
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
		writeInternalErr(w, err)
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

	// 表格勾选优先：只补选中的 ID，忽略「条数」上限
	var linkIDs []int
	seen := make(map[int]bool, len(req.LinkIDs))
	for _, id := range req.LinkIDs {
		if id <= 0 || seen[id] {
			continue
		}
		seen[id] = true
		linkIDs = append(linkIDs, id)
		if len(linkIDs) >= 200000 {
			break
		}
	}

	jobCtx, started := globalBatch.start(mode)
	if !started {
		writeJSON(w, http.StatusConflict, map[string]any{"message": "已有批量补料任务正在运行", "status": globalBatch.snapshot()})
		return
	}
	if len(linkIDs) > 0 {
		globalBatch.update(func(s *BatchEnrichStatus) {
			s.Selected = len(linkIDs)
			s.Message = fmt.Sprintf("已选 %d 条，准备中…", len(linkIDs))
		})
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
		a.runBatchEnrich(jobCtx, cfg, mode, limit, concurrency, req.LibraryID, linkIDs)
	}()

	writeOK(w, map[string]any{"started": true, "status": globalBatch.snapshot()})
}

// runBatchEnrich 拉取候选链接后按并发度消费。linkIDs 非空时只处理表格勾选的链接。
func (a *App) runBatchEnrich(ctx context.Context, cfg *LlmConfig, mode string, limit, concurrency, libraryID int, linkIDs []int) {
	cond := `"IsDeleted" = ` + a.db.False()
	args := make([]any, 0, len(linkIDs)+1)
	if libraryID > 0 {
		cond += fmt.Sprintf(` AND "LibraryId" = %d`, libraryID)
	}
	if len(linkIDs) > 0 {
		marks := make([]string, len(linkIDs))
		for i, id := range linkIDs {
			marks[i] = "?"
			args = append(args, id)
		}
		cond += ` AND "Id" IN (` + strings.Join(marks, ",") + `)`
	}
	if mode == "missing" {
		// 摘要为空 或 没有任何标签
		cond += ` AND (COALESCE("Summary",'') = '' OR NOT EXISTS (SELECT 1 FROM "LinkTags" lt WHERE lt."LinkId" = "Links"."Id"))`
	}
	queryLimit := limit
	if len(linkIDs) > 0 {
		queryLimit = len(linkIDs) // 勾选模式不受「条数」输入限制
	}
	rows, err := a.db.sql.Query(a.db.Rebind(`SELECT "Id" FROM "Links" WHERE `+cond+` ORDER BY "SortOrder","Id" LIMIT ?`), append(args, queryLimit)...)
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

	// 勾选模式按用户勾选顺序处理，便于进度与选择一一对应
	if len(linkIDs) > 0 {
		order := make(map[int]int, len(linkIDs))
		for i, id := range linkIDs {
			order[id] = i
		}
		sort.SliceStable(ids, func(i, j int) bool { return order[ids[i]] < order[ids[j]] })
	}

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
				qerr := a.db.sql.QueryRow(a.db.Rebind(`SELECT "Title","Url",COALESCE("Remark",'') FROM "Links" WHERE "Id" = ? AND "IsDeleted" = `+a.db.False()), id).
					Scan(&title, &linkURL, &remark)
				if qerr != nil {
					globalBatch.update(func(s *BatchEnrichStatus) {
						s.Processed++
						s.Failed++
					})
					continue
				}
				res, eerr := a.enrichOne(ctx, cfg, title, linkURL, remark.String)
				if eerr != nil {
					a.logf("批量补料失败 id=%d: %v", id, eerr)
					globalBatch.update(func(s *BatchEnrichStatus) {
						s.Processed++
						s.Failed++
						s.Message = fmt.Sprintf("进行中… %d/%d（失败 %d）", s.Processed, s.Total, s.Failed)
					})
					continue
				}
				// mode=all 才覆盖摘要；mode=missing 只填空缺，保护用户手写内容
				aerr := a.applyEnrich(id, res, mode == "all")
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
