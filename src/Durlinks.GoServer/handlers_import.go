package main

import (
	"database/sql"
	"fmt"
	"io"
	"mime/multipart"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/xuri/excelize/v2"
)

// ======================= Excel 解析（等价 ExcelImportService） =======================

type xlsxRow map[string]string

var tagSeparators = []string{";", "；", ",", "，"}

// maxXlsxRows 单次导入的最大数据行数（压缩炸弹防护）。
const maxXlsxRows = 500000

// parseXlsx 读取工作簿：优先选名称含「导入」的工作表，首行作表头。
func parseXlsx(r io.Reader) ([]xlsxRow, error) {
	f, err := excelize.OpenReader(r)
	if err != nil {
		return nil, fmt.Errorf("无法读取 Excel：%w", err)
	}
	defer f.Close()

	sheets := f.GetSheetList()
	if len(sheets) == 0 {
		return nil, fmt.Errorf("Excel 中没有工作表")
	}
	sheet := sheets[0]
	for _, s := range sheets {
		if strings.Contains(s, "导入") {
			sheet = s
			break
		}
	}

	rows, err := f.Rows(sheet)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var (
		header map[int]string
		out    []xlsxRow
	)
	for rows.Next() {
		cols, err := rows.Columns()
		if err != nil {
			continue
		}
		if header == nil {
			header = map[int]string{}
			for i, v := range cols {
				name := strings.TrimSpace(v)
				if name == "" {
					continue
				}
				if _, exists := header[i]; !exists {
					header[i] = name
				}
			}
			continue
		}
		item := xlsxRow{}
		empty := true
		for i, name := range header {
			val := ""
			if i < len(cols) {
				val = strings.TrimSpace(cols[i])
			}
			item[name] = val
			if val != "" {
				empty = false
			}
		}
		if empty {
			continue
		}
		out = append(out, item)
		// 行数上限：xlsx 是压缩包，解压后可能远超上传体积（zip 炸弹），必须有天花板
		if len(out) > maxXlsxRows {
			return out, fmt.Errorf("数据行超过 %d，请拆分后再导入", maxXlsxRows)
		}
	}
	return out, rows.Error()
}

type parsedLink struct {
	SortOrder   int
	LinkType    string
	GroupName   string
	Title       string
	URL         string
	ExtractCode *string
	ExpireAt    *string
	Platform    *string
	Version     *string
	Remark      *string
	Summary     *string
	CreatedAt   time.Time
	URLs        []string
	SubLinks    []struct {
		URL   string
		Title *string
	}
	Tags []string
}

// mapXlsxRow 列名映射（与 C# MapRow 一致）。
func mapXlsxRow(cells xlsxRow, rowNo, sortOrder int, warnings *[]string) *parsedLink {
	get := func(name string) string {
		for k, v := range cells {
			if strings.EqualFold(strings.TrimSpace(k), name) {
				return strings.TrimSpace(v)
			}
		}
		return ""
	}
	title := get("名称")
	url := get("链接")
	if title == "" && url == "" {
		return nil
	}
	l := &parsedLink{
		SortOrder:   sortOrder,
		LinkType:    nz(get("链接形式"), "官网/普通网址"),
		GroupName:   nz(get("分类"), "未分类"),
		Title:       nz(title, nz(url, "(未命名)")),
		URL:         url,
		ExtractCode: strPtrOrNil(get("提取码")),
		ExpireAt:    strPtrOrNil(get("网盘过期时间")),
		Platform:    strPtrOrNil(get("平台")),
		Version:     strPtrOrNil(get("版本")),
		Remark:      strPtrOrNil(get("备注")),
		Summary:     strPtrOrNil(get("AI摘要")),
	}
	if raw := get("序号"); raw != "" {
		if n, err := strconv.Atoi(raw); err == nil {
			l.SortOrder = n
		}
	}
	if created := get("添加时间"); created != "" {
		l.CreatedAt = parseExcelTime(created)
	}

	for i, line := range splitLines(url) {
		l.URLs = append(l.URLs, line)
		_ = i
	}
	for _, line := range splitLines(get("附属链接")) {
		l.SubLinks = append(l.SubLinks, struct {
			URL   string
			Title *string
		}{URL: line})
	}
	if raw := get("标签"); raw != "" {
		seen := map[string]bool{}
		for _, part := range splitMulti(raw, tagSeparators) {
			if part == "" || seen[strings.ToLower(part)] {
				continue
			}
			seen[strings.ToLower(part)] = true
			l.Tags = append(l.Tags, part)
		}
	}

	if warnings != nil {
		if l.LinkType == "网盘" && (l.ExtractCode == nil || *l.ExtractCode == "") {
			*warnings = append(*warnings, fmt.Sprintf("第 %d 行：网盘链接未填提取码（已导入，提取码留空）", rowNo))
		}
		if l.LinkType == "下载直链" && (l.Platform == nil || *l.Platform == "") {
			*warnings = append(*warnings, fmt.Sprintf("第 %d 行：下载直链未填平台（已导入，平台留空）", rowNo))
		}
	}
	return l
}

func splitLines(s string) []string {
	var out []string
	for _, part := range strings.FieldsFunc(s, func(r rune) bool { return r == '\n' || r == '\r' }) {
		if v := strings.TrimSpace(part); v != "" {
			out = append(out, v)
		}
	}
	return out
}

func splitMulti(s string, seps []string) []string {
	parts := []string{s}
	for _, sep := range seps {
		var next []string
		for _, p := range parts {
			next = append(next, strings.Split(p, sep)...)
		}
		parts = next
	}
	var out []string
	for _, p := range parts {
		if v := strings.TrimSpace(p); v != "" {
			out = append(out, v)
		}
	}
	return out
}

// parseExcelTime 支持 Excel 序列号与常见日期格式；表格时间是北京时间，按 +08:00 解释。
func parseExcelTime(raw string) time.Time {
	raw = strings.TrimSpace(raw)
	if serial, err := strconv.ParseFloat(raw, 64); err == nil && serial > 20000 && serial < 80000 {
		// Excel 序列号 → 时间（1900 日期系统）
		base := time.Date(1899, 12, 30, 0, 0, 0, 0, time.UTC)
		t := base.Add(time.Duration(serial*24) * time.Hour)
		return t.Add(-8 * time.Hour)
	}
	for _, layout := range []string{
		"2006-01-02 15:04:05", "2006/1/2 15:04:05", "2006-01-02T15:04:05",
		"2006-01-02", "2006/1/2", time.RFC3339,
	} {
		if t, err := time.ParseInLocation(layout, raw, time.FixedZone("CST", 8*3600)); err == nil {
			return t.UTC()
		}
	}
	return time.Time{}
}

// ======================= 导入（同步 + 后台任务） =======================

// importParsedLinks 把解析结果写入目标库；按 URL 去重，批量提交。
func (a *App) importParsedLinks(links []*parsedLink, libraryID int, source ImportSource, progress func(processed, imported, skipped, failed int), cancelled func() bool) (imported, skipped, failed int) {
	seen := map[string]bool{}
	if rows, err := a.db.sql.Query(a.db.Rebind(`SELECT "Url" FROM "Links" WHERE "IsDeleted" = `+a.db.False()+` AND "LibraryId" = ?`), libraryID); err == nil {
		for rows.Next() {
			var u string
			if err := rows.Scan(&u); err == nil {
				if k := normalizeURLKey(u); k != "" {
					seen[k] = true
				}
			}
		}
		rows.Close()
	}

	processed := 0
	var pending []*parsedLink
	flush := func() {
		if len(pending) == 0 {
			return
		}
		tx, err := a.db.sql.Begin()
		if err != nil {
			failed += len(pending)
			pending = nil
			return
		}
		committed := 0
		for _, pl := range pending {
			created := pl.CreatedAt
			if created.IsZero() {
				created = time.Now().UTC()
			}
			id, err := a.insertLink(tx, &Link{
				SortOrder: pl.SortOrder, LinkType: pl.LinkType, GroupName: pl.GroupName, Title: pl.Title,
				URL: pl.URL, ExtractCode: pl.ExtractCode, ExpireAt: pl.ExpireAt, Platform: pl.Platform,
				Version: pl.Version, Remark: pl.Remark, Summary: pl.Summary, ImportSource: source,
				LibraryID: &libraryID, CreatedAt: created,
			})
			if err != nil {
				failed++
				continue
			}
			for i, u := range pl.URLs {
				_, _ = tx.Exec(a.db.Rebind(`INSERT INTO "LinkUrls" ("LinkId","Url","SortOrder") VALUES (?,?,?)`), id, u, i)
			}
			for i, s := range pl.SubLinks {
				_, _ = tx.Exec(a.db.Rebind(`INSERT INTO "LinkSubLinks" ("LinkId","Url","Title","SortOrder") VALUES (?,?,?,?)`), id, s.URL, nptr(s.Title), i)
			}
			if err := a.replaceTags(tx, id, pl.Tags); err != nil {
				failed++
				continue
			}
			committed++
		}
		if err := tx.Commit(); err != nil {
			failed += committed
			imported -= committed
		} else {
			imported += committed
		}
		pending = nil
	}

	for _, pl := range links {
		if cancelled != nil && cancelled() {
			break
		}
		processed++
		// Excel 里同样可能有 javascript:/data: 之类的脏链接，导入阶段就挡掉
		if safe, err := sanitizeLinkURL(pl.URL); err != nil {
			failed++
			continue
		} else {
			pl.URL = safe
		}
		key := normalizeURLKey(pl.URL)
		if key != "" && seen[key] {
			skipped++
			if progress != nil && processed%200 == 0 {
				progress(processed, imported, skipped, failed)
			}
			continue
		}
		if key != "" {
			seen[key] = true
		}
		pending = append(pending, pl)
		if len(pending) >= 500 {
			flush()
			if progress != nil {
				progress(processed, imported, skipped, failed)
			}
		}
	}
	flush()
	if progress != nil {
		progress(processed, imported, skipped, failed)
	}
	return
}

// handleImportTemplate GET /api/admin/import/template
func (a *App) handleImportTemplate(w http.ResponseWriter, r *http.Request) {
	candidates := []string{
		filepath.Join(a.baseDir, "templates", "导入模板.xlsx"),
		filepath.Join(a.baseDir, "..", "src", "Durlinks.Server", "templates", "导入模板.xlsx"),
	}
	for _, p := range candidates {
		if fileExists(p) {
			w.Header().Set("Content-Type", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
			w.Header().Set("Content-Disposition", `attachment; filename="import-template.xlsx"`)
			http.ServeFile(w, r, p)
			return
		}
	}
	writeErr(w, http.StatusNotFound, "导入模板文件不存在")
}

func readUpload(r *http.Request, maxBytes int64) (multipart.File, *multipart.FileHeader, error) {
	if err := r.ParseMultipartForm(maxBytes); err != nil {
		return nil, nil, err
	}
	file, header, err := r.FormFile("file")
	return file, header, err
}

// handleImportExcel POST /api/admin/import/excel?libraryId=（≤10MB 同步导入）
func (a *App) handleImportExcel(w http.ResponseWriter, r *http.Request) {
	file, header, err := readUpload(r, 16<<20)
	if err != nil {
		writeErr(w, http.StatusBadRequest, "未上传文件")
		return
	}
	defer file.Close()
	if header.Size > 10*1024*1024 {
		writeErr(w, http.StatusBadRequest, "文件不能超过 10MB")
		return
	}
	libID, err := a.db.resolveLibraryID(queryIntPtr(r, "libraryId"))
	if err != nil || libID == nil {
		writeErr(w, http.StatusBadRequest, "发布库不存在")
		return
	}
	rows, err := parseXlsx(file)
	if err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	var warnings []string
	var parsed []*parsedLink
	for i, row := range rows {
		if pl := mapXlsxRow(row, i+2, i+1, &warnings); pl != nil {
			parsed = append(parsed, pl)
		}
	}
	imported, skipped, failed := a.importParsedLinks(parsed, *libID, ImportManual, nil, nil)
	msg := fmt.Sprintf("已导入 %d 条，跳过重复 %d 条，失败 %d 条", imported, skipped, failed)
	if len(warnings) > 0 {
		if len(warnings) > 20 {
			warnings = warnings[:20]
		}
		msg += "；警告：" + strings.Join(warnings, "；")
	}
	writeOK(w, map[string]any{"imported": imported, "skipped": skipped, "failed": failed, "message": msg})
}

// ---------- 后台任务（大表） ----------

type importJob struct {
	mu     sync.Mutex
	status ExcelImportStatus
	cancel bool
}

var globalImportJob = &importJob{}

func (j *importJob) snapshot() ExcelImportStatus {
	j.mu.Lock()
	defer j.mu.Unlock()
	return j.status
}

func (j *importJob) start(fileName string) bool {
	j.mu.Lock()
	defer j.mu.Unlock()
	if j.status.Running {
		return false
	}
	now := isoTime(time.Now())
	j.status = ExcelImportStatus{Running: true, FileName: fileName, Message: "准备中…", StartedAt: &now}
	j.cancel = false
	return true
}

func (j *importJob) requestCancel() bool {
	j.mu.Lock()
	defer j.mu.Unlock()
	if !j.status.Running {
		return false
	}
	j.cancel = true
	j.status.CancelRequested = true
	j.status.Message = "正在取消…"
	return true
}

func (j *importJob) update(fn func(*ExcelImportStatus)) {
	j.mu.Lock()
	defer j.mu.Unlock()
	fn(&j.status)
}

func (j *importJob) isCancelled() bool {
	j.mu.Lock()
	defer j.mu.Unlock()
	return j.cancel
}

// handleStartExcelImport POST /api/admin/import/excel/jobs
func (a *App) handleStartExcelImport(w http.ResponseWriter, r *http.Request) {
	file, header, err := readUpload(r, 64<<20)
	if err != nil {
		writeErr(w, http.StatusBadRequest, "未上传文件")
		return
	}
	defer file.Close()
	if header.Size > 64*1024*1024 {
		writeErr(w, http.StatusBadRequest, "文件不能超过 64MB")
		return
	}
	libID, err := a.db.resolveLibraryID(queryIntPtr(r, "libraryId"))
	if err != nil || libID == nil {
		writeErr(w, http.StatusBadRequest, "发布库不存在")
		return
	}
	tmp, err := os.CreateTemp("", "durlinks-import-*.xlsx")
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	tmpPath := tmp.Name()
	if _, err := io.Copy(tmp, file); err != nil {
		tmp.Close()
		os.Remove(tmpPath)
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	tmp.Close()

	if !globalImportJob.start(header.Filename) {
		os.Remove(tmpPath)
		writeErr(w, http.StatusConflict, "已有导入任务在运行")
		return
	}
	go func() {
		defer os.Remove(tmpPath)
		defer func() {
			if rec := recover(); rec != nil {
				globalImportJob.update(func(s *ExcelImportStatus) {
					s.Running = false
					s.Message = fmt.Sprintf("导入异常：%v", rec)
					now := isoTime(time.Now())
					s.FinishedAt = &now
				})
			}
		}()
		f, err := os.Open(tmpPath)
		if err != nil {
			globalImportJob.update(func(s *ExcelImportStatus) {
				s.Running = false
				s.Message = "读取临时文件失败：" + err.Error()
				now := isoTime(time.Now())
				s.FinishedAt = &now
			})
			return
		}
		rows, err := parseXlsx(f)
		f.Close()
		if err != nil {
			globalImportJob.update(func(s *ExcelImportStatus) {
				s.Running = false
				s.Message = "解析失败：" + err.Error()
				now := isoTime(time.Now())
				s.FinishedAt = &now
			})
			return
		}
		globalImportJob.update(func(s *ExcelImportStatus) { s.Message = "解析完成，开始入库…" })
		var warnings []string
		parsed := make([]*parsedLink, 0, len(rows))
		for i, row := range rows {
			if globalImportJob.isCancelled() {
				break
			}
			if pl := mapXlsxRow(row, i+2, i+1, &warnings); pl != nil {
				parsed = append(parsed, pl)
			}
		}
		imported, skipped, failed := a.importParsedLinks(parsed, *libID, ImportManual,
			func(processed, imp, skip, fail int) {
				globalImportJob.update(func(s *ExcelImportStatus) {
					s.Processed = processed
					s.Imported = imp
					s.Skipped = skip
					s.Failed = fail
					s.Message = fmt.Sprintf("处理中… %d 条", processed)
				})
			}, globalImportJob.isCancelled)
		globalImportJob.update(func(s *ExcelImportStatus) {
			s.Running = false
			s.Imported = imported
			s.Skipped = skipped
			s.Failed = failed
			// 取消时不要用总行数覆盖已处理数，否则进度会显示成"全部处理完"
			if !s.CancelRequested {
				s.Processed = len(parsed)
			}
			now := isoTime(time.Now())
			s.FinishedAt = &now
			if s.CancelRequested {
				s.Message = fmt.Sprintf("已取消：导入 %d 条", imported)
			} else {
				s.Message = fmt.Sprintf("完成：导入 %d 条，跳过 %d 条，失败 %d 条", imported, skipped, failed)
			}
		})
	}()
	writeOK(w, map[string]any{"started": true, "status": globalImportJob.snapshot()})
}

// handleExcelImportStatus GET /api/admin/import/excel/jobs/status
func (a *App) handleExcelImportStatus(w http.ResponseWriter, r *http.Request) {
	writeOK(w, globalImportJob.snapshot())
}

// handleCancelExcelImport POST /api/admin/import/excel/jobs/cancel
func (a *App) handleCancelExcelImport(w http.ResponseWriter, r *http.Request) {
	if !globalImportJob.requestCancel() {
		writeErr(w, http.StatusBadRequest, "没有正在运行的导入任务")
		return
	}
	writeOK(w, map[string]any{"cancelling": true})
}

// excelSafe 防公式注入：以 = + - @ 或制表/回车开头的字符串，Excel 会当公式执行。
// 前面加一个单引号，Excel 会强制作文本显示。
func excelSafe(v any) any {
	s, ok := v.(string)
	if !ok {
		return v
	}
	if s == "" {
		return s
	}
	switch s[0] {
	case '=', '+', '-', '@', '\t', '\r':
		return "'" + s
	}
	return s
}

// collectAllLinks 分页拉取全量链接（导出与数据包共用）。
func (a *App) collectAllLinks(limit int, libraryID *int) ([]LinkDto, error) {
	if limit <= 0 || limit > 200000 {
		limit = 200000
	}
	var items []LinkDto
	for page := 1; len(items) < limit; page++ {
		itemsPage, total, err := a.db.queryLinks(LinkQuery{
			Page: page, PageSize: adminMaxPageSize, LibraryID: libraryID,
		}, false)
		if err != nil {
			return nil, err
		}
		items = append(items, itemsPage...)
		if len(itemsPage) == 0 || len(itemsPage) < adminMaxPageSize || len(items) >= total {
			break
		}
	}
	if len(items) > limit {
		items = items[:limit]
	}
	return items, nil
}

// buildLinksXLSX 生成导入模板同结构的 xlsx（14 列，可直接再次导入）。
func (a *App) buildLinksXLSX(items []LinkDto) ([]byte, error) {
	f := excelize.NewFile()
	sheet := "链接"
	idx, _ := f.NewSheet(sheet)
	f.SetActiveSheet(idx)
	// excelize.NewFile 默认带一个空 Sheet1，不删的话用户打开先看到一张空表
	_ = f.DeleteSheet("Sheet1")
	headers := []string{"序号", "链接形式", "分类", "名称", "链接", "提取码", "网盘过期时间", "平台", "版本", "备注", "AI摘要", "标签", "添加时间"}
	for i, h := range headers {
		cell, _ := excelize.CoordinatesToCellName(i+1, 1)
		_ = f.SetCellValue(sheet, cell, h)
	}
	for i, it := range items {
		row := i + 2
		values := []any{it.SortOrder, it.LinkType, it.GroupName, it.Title, it.URL,
			ptrStr(it.ExtractCode), ptrStr(it.ExpireAt), ptrStr(it.Platform), ptrStr(it.Version),
			ptrStr(it.Remark), ptrStr(it.Summary), strings.Join(it.Tags, ";"), it.CreatedAt}
		for c, v := range values {
			cell, _ := excelize.CoordinatesToCellName(c+1, row)
			_ = f.SetCellValue(sheet, cell, excelSafe(v))
		}
	}
	buf, err := f.WriteToBuffer()
	if err != nil {
		return nil, err
	}
	return buf.Bytes(), nil
}

// handleExportEnriched GET /api/admin/ai/enrich/export（导出补料结果 xlsx）
func (a *App) handleExportEnriched(w http.ResponseWriter, r *http.Request) {
	items, err := a.collectAllLinks(queryInt(r, "limit", 200000), queryIntPtr(r, "libraryId"))
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	blob, err := a.buildLinksXLSX(items)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	w.Header().Set("Content-Type", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
	w.Header().Set("Content-Disposition", `attachment; filename="links-export.xlsx"`)
	w.Header().Set("Content-Length", itoa(len(blob)))
	_, _ = w.Write(blob)
}

var _ = sql.ErrNoRows
