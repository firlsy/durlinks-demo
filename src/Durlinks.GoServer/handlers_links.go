package main

import (
	"database/sql"
	"fmt"
	"net/http"
	"net/url"
	"regexp"
	"sort"
	"strings"
	"time"
)

// handleStats GET /api/admin/stats
func (a *App) handleStats(w http.ResponseWriter, r *http.Request) {
	type kv struct {
		sql string
		dst *int
	}
	var total, hidden, aiCount, groups, tags, netdisk, download int
	queries := []kv{
		{`SELECT COUNT(*) FROM "Links" WHERE "IsDeleted" = ` + a.db.False(), &total},
		{`SELECT COUNT(*) FROM "Links" WHERE "IsDeleted" = ` + a.db.False() + ` AND "IsHidden" = ` + a.db.True(), &hidden},
		{`SELECT COUNT(*) FROM "Links" WHERE "IsDeleted" = ` + a.db.False() + ` AND "ImportSource" = 1`, &aiCount},
		{`SELECT COUNT(DISTINCT "GroupName") FROM "Links" WHERE "IsDeleted" = ` + a.db.False(), &groups},
		{`SELECT COUNT(*) FROM "Tags"`, &tags},
		{`SELECT COUNT(*) FROM "Links" WHERE "IsDeleted" = ` + a.db.False() + ` AND "LinkType" = '网盘'`, &netdisk},
		{`SELECT COUNT(*) FROM "Links" WHERE "IsDeleted" = ` + a.db.False() + ` AND "LinkType" = '下载直链'`, &download},
	}
	for _, q := range queries {
		if err := a.db.sql.QueryRow(q.sql).Scan(q.dst); err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
	}
	writeOK(w, map[string]int{
		"total": total, "hidden": hidden, "aiCount": aiCount,
		"groups": groups, "tags": tags, "netdisk": netdisk, "download": download,
	})
}

// handleAdminLinks GET /api/admin/links
func (a *App) handleAdminLinks(w http.ResponseWriter, r *http.Request) {
	q := parseLinkQuery(r)
	items, total, err := a.db.queryLinks(q, false)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	page := max(1, q.Page)
	size := clamp(q.PageSize, 1, adminMaxPageSize)
	totalPages := 0
	if size > 0 {
		totalPages = (total + size - 1) / size
	}
	writeOK(w, PagedResult{Items: items, Total: total, Page: page, PageSize: size, TotalPages: totalPages})
}

// handleSetHidden PATCH /api/admin/links/{id}/hidden
func (a *App) handleSetHidden(w http.ResponseWriter, r *http.Request) {
	id, ok := pathID(r, "id")
	if !ok {
		writeErr(w, http.StatusNotFound, "链接不存在")
		return
	}
	var dto HiddenDto
	if err := decodeJSON(r, &dto, 1<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	res, err := a.db.sql.Exec(a.db.Rebind(`UPDATE "Links" SET "IsHidden" = ? WHERE "Id" = ?`), a.db.BoolValue(dto.Hidden), id)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if n, _ := res.RowsAffected(); n == 0 {
		writeErr(w, http.StatusNotFound, "链接不存在")
		return
	}
	writeOK(w, map[string]any{"id": id, "isHidden": dto.Hidden})
}

// handleSoftDelete DELETE /api/admin/links/{id}
func (a *App) handleSoftDelete(w http.ResponseWriter, r *http.Request) {
	id, ok := pathID(r, "id")
	if !ok {
		writeErr(w, http.StatusNotFound, "链接不存在")
		return
	}
	res, err := a.db.sql.Exec(a.db.Rebind(`UPDATE "Links" SET "IsDeleted" = ? WHERE "Id" = ?`), a.db.BoolValue(true), id)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if n, _ := res.RowsAffected(); n == 0 {
		writeErr(w, http.StatusNotFound, "链接不存在")
		return
	}
	writeOK(w, map[string]any{"id": id, "isDeleted": true})
}

func (a *App) handleBatchHidden(w http.ResponseWriter, r *http.Request) {
	var dto BatchIdsDto
	if err := decodeJSON(r, &dto, 8<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	affected, err := a.execOnIDs(dto.IDs, `UPDATE "Links" SET "IsHidden" = ? WHERE "Id" IN (%s)`, a.db.BoolValue(dto.Hidden))
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeOK(w, map[string]any{"affected": affected, "hidden": dto.Hidden})
}

func (a *App) handleBatchDelete(w http.ResponseWriter, r *http.Request) {
	var dto BatchIdsDto
	if err := decodeJSON(r, &dto, 8<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	affected, err := a.execOnIDs(dto.IDs, `UPDATE "Links" SET "IsDeleted" = ? WHERE "Id" IN (%s)`, a.db.BoolValue(true))
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeOK(w, map[string]any{"affected": affected, "deleted": true})
}

func (a *App) execOnIDs(ids []int, sqlFmt string, head ...any) (int, error) {
	if len(ids) == 0 {
		return 0, nil
	}
	ph := strings.TrimSuffix(strings.Repeat("?,", len(ids)), ",")
	args := append([]any{}, head...)
	for _, id := range ids {
		args = append(args, id)
	}
	res, err := a.db.sql.Exec(a.db.Rebind(strings.Replace(sqlFmt, "%s", ph, 1)), args...)
	if err != nil {
		return 0, err
	}
	n, _ := res.RowsAffected()
	return int(n), nil
}

// handleCreateLink POST /api/admin/links
func (a *App) handleCreateLink(w http.ResponseWriter, r *http.Request) {
	var dto LinkCreateRequest
	if err := decodeJSON(r, &dto, 2<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	if strings.TrimSpace(dto.Title) == "" && strings.TrimSpace(dto.URL) == "" {
		writeErr(w, http.StatusBadRequest, "标题和链接至少填一项")
		return
	}
	if len(dto.URL) > 2000 {
		writeErr(w, http.StatusBadRequest, "链接过长")
		return
	}
	libID, err := a.db.resolveLibraryID(dto.LibraryID)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if libID == nil {
		writeErr(w, http.StatusBadRequest, "发布库不存在")
		return
	}
	maxSort, err := a.db.maxSortOrder(*libID)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}

	urlText, err := sanitizeLinkURL(dto.URL)
	if err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	title := dto.Title
	if strings.TrimSpace(title) == "" {
		if d := extractDomain(urlText); d != "" {
			title = d
		} else {
			title = urlText
		}
	}
	linkType := nz(ptrStr(dto.LinkType), "官网/普通网址")
	groupName := nz(ptrStr(dto.GroupName), "未分类")

	tx, err := a.db.sql.Begin()
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	defer tx.Rollback()

	newID, err := a.insertLink(tx, &Link{
		SortOrder: maxSort + 1, LinkType: linkType, GroupName: groupName, Title: title, URL: urlText,
		ExtractCode: dto.ExtractCode, ExpireAt: dto.ExpireAt, Platform: dto.Platform, Version: dto.Version,
		Remark: dto.Remark, Summary: dto.Summary, ImportSource: ImportManual, LibraryID: libID,
	})
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if urlText != "" {
		if _, err := tx.Exec(a.db.Rebind(`INSERT INTO "LinkUrls" ("LinkId","Url","SortOrder") VALUES (?,?,?)`), newID, urlText, 0); err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
	}
	if err := a.replaceTags(tx, newID, dto.Tags); err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if err := tx.Commit(); err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeOK(w, map[string]int{"id": newID})
}

func (a *App) insertLink(tx *sql.Tx, l *Link) (int, error) {
	created := a.db.now()
	if !l.CreatedAt.IsZero() {
		if a.db.pg {
			created = l.CreatedAt.UTC()
		} else {
			created = l.CreatedAt.UTC().Format(time.RFC3339Nano)
		}
	}
	if a.db.pg {
		var id int
		err := tx.QueryRow(`INSERT INTO "Links"
			("SortOrder","LinkType","GroupName","Title","Url","ExtractCode","ExpireAt","Platform","Version","Remark","Summary","CreatedAt","ImportSource","IsHidden","IsDeleted","LibraryId")
			VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?) RETURNING "Id"`,
			l.SortOrder, l.LinkType, l.GroupName, l.Title, l.URL, nptr(l.ExtractCode), nptr(l.ExpireAt), nptr(l.Platform),
			nptr(l.Version), nptr(l.Remark), nptr(l.Summary), created, int(l.ImportSource), l.IsHidden, l.IsDeleted, l.LibraryID).Scan(&id)
		return id, err
	}
	res, err := tx.Exec(a.db.Rebind(`INSERT INTO "Links"
		("SortOrder","LinkType","GroupName","Title","Url","ExtractCode","ExpireAt","Platform","Version","Remark","Summary","CreatedAt","ImportSource","IsHidden","IsDeleted","LibraryId")
		VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`),
		l.SortOrder, l.LinkType, l.GroupName, l.Title, l.URL, nptr(l.ExtractCode), nptr(l.ExpireAt), nptr(l.Platform),
		nptr(l.Version), nptr(l.Remark), nptr(l.Summary), created, int(l.ImportSource),
		a.db.BoolValue(l.IsHidden), a.db.BoolValue(l.IsDeleted), l.LibraryID)
	if err != nil {
		return 0, err
	}
	last, err := res.LastInsertId()
	return int(last), err
}

// replaceTags 覆盖式写入链接标签（去重、自动建标签）。
func (a *App) replaceTags(tx *sql.Tx, linkID int, tags []string) error {
	if _, err := tx.Exec(a.db.Rebind(`DELETE FROM "LinkTags" WHERE "LinkId" = ?`), linkID); err != nil {
		return err
	}
	seen := map[string]bool{}
	for _, raw := range tags {
		name := strings.TrimSpace(raw)
		if name == "" || seen[strings.ToLower(name)] {
			continue
		}
		seen[strings.ToLower(name)] = true
		var tagID int
		err := tx.QueryRow(a.db.Rebind(`SELECT "Id" FROM "Tags" WHERE "Name" = ?`), name).Scan(&tagID)
		if err == sql.ErrNoRows {
			// 并发下同名标签可能已被别的请求插入：UPSERT 吞掉唯一索引冲突再回查。
			// 不能靠"插入失败后重查"——PostgreSQL 会把报错语句所在的事务直接标记为 aborted。
			if _, insErr := tx.Exec(a.db.Rebind(`INSERT INTO "Tags" ("Name") VALUES (?) ON CONFLICT ("Name") DO NOTHING`), name); insErr != nil {
				return insErr
			}
			if err := tx.QueryRow(a.db.Rebind(`SELECT "Id" FROM "Tags" WHERE "Name" = ?`), name).Scan(&tagID); err != nil {
				return err
			}
		} else if err != nil {
			return err
		}
		if _, err := tx.Exec(a.db.Rebind(`INSERT INTO "LinkTags" ("LinkId","TagId") VALUES (?,?)`), linkID, tagID); err != nil {
			return err
		}
	}
	return nil
}

// handleUpdateLink PUT /api/admin/links/{id}
func (a *App) handleUpdateLink(w http.ResponseWriter, r *http.Request) {
	id, ok := pathID(r, "id")
	if !ok {
		writeErr(w, http.StatusNotFound, "链接不存在")
		return
	}
	var dto LinkUpdateRequest
	if err := decodeJSON(r, &dto, 2<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	if dto.URL != nil && len(strings.TrimSpace(*dto.URL)) > 2000 {
		writeErr(w, http.StatusBadRequest, "链接过长")
		return
	}

	tx, err := a.db.sql.Begin()
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	defer tx.Rollback()

	var exists int
	if err := tx.QueryRow(a.db.Rebind(`SELECT COUNT(*) FROM "Links" WHERE "Id" = ? AND "IsDeleted" = `+a.db.False()), id).Scan(&exists); err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if exists == 0 {
		writeErr(w, http.StatusNotFound, "链接不存在")
		return
	}

	sets := []string{}
	args := []any{}
	add := func(col string, v any) {
		sets = append(sets, `"`+col+`" = ?`)
		args = append(args, v)
	}
	if dto.LibraryID != nil {
		libID, err := a.db.resolveLibraryID(dto.LibraryID)
		if err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
		if libID == nil {
			writeErr(w, http.StatusBadRequest, "发布库不存在")
			return
		}
		add("LibraryId", *libID)
	}
	if dto.Title != nil {
		add("Title", strings.TrimSpace(*dto.Title))
	}
	if dto.LinkType != nil {
		add("LinkType", *dto.LinkType)
	}
	if dto.GroupName != nil {
		add("GroupName", *dto.GroupName)
	}
	if dto.Platform != nil {
		add("Platform", *dto.Platform)
	}
	if dto.Version != nil {
		add("Version", *dto.Version)
	}
	if dto.ExtractCode != nil {
		add("ExtractCode", *dto.ExtractCode)
	}
	if dto.ExpireAt != nil {
		add("ExpireAt", *dto.ExpireAt)
	}
	if dto.Remark != nil {
		add("Remark", *dto.Remark)
	}
	if dto.Summary != nil {
		add("Summary", *dto.Summary)
	}
	if dto.IsHidden != nil {
		add("IsHidden", a.db.BoolValue(*dto.IsHidden))
	}
	if dto.URL != nil {
		u, err := sanitizeLinkURL(*dto.URL)
		if err != nil {
			writeErr(w, http.StatusBadRequest, err.Error())
			return
		}
		add("Url", u)
		if _, err := tx.Exec(a.db.Rebind(`DELETE FROM "LinkUrls" WHERE "LinkId" = ?`), id); err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
		if u != "" {
			if _, err := tx.Exec(a.db.Rebind(`INSERT INTO "LinkUrls" ("LinkId","Url","SortOrder") VALUES (?,?,?)`), id, u, 0); err != nil {
				writeErr(w, http.StatusInternalServerError, err.Error())
				return
			}
		}
	}
	if len(sets) > 0 {
		args = append(args, id)
		if _, err := tx.Exec(a.db.Rebind(`UPDATE "Links" SET `+strings.Join(sets, ", ")+` WHERE "Id" = ?`), args...); err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
	}
	if dto.Tags != nil {
		if err := a.replaceTags(tx, id, dto.Tags); err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
	}
	if err := tx.Commit(); err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeOK(w, map[string]string{"message": "已保存"})
}

// handleMoveLinks POST /api/admin/links/move
func (a *App) handleMoveLinks(w http.ResponseWriter, r *http.Request) {
	var req MoveCopyLinksRequest
	if err := decodeJSON(r, &req, 8<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	if len(req.IDs) == 0 {
		writeErr(w, http.StatusBadRequest, "请先选择链接")
		return
	}
	if len(req.IDs) > 500 {
		writeErr(w, http.StatusBadRequest, "单次最多 500 条")
		return
	}
	target, err := a.db.resolveLibraryID(&req.TargetLibraryID)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if target == nil {
		writeErr(w, http.StatusBadRequest, "目标发布库不存在")
		return
	}

	tx, err := a.db.sql.Begin()
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	defer tx.Rollback()

	ph := strings.TrimSuffix(strings.Repeat("?,", len(req.IDs)), ",")
	args := make([]any, 0, len(req.IDs))
	allInTarget := true
	rows, err := tx.Query(a.db.Rebind(`SELECT "Id","LibraryId" FROM "Links" WHERE "IsDeleted" = `+a.db.False()+` AND "Id" IN (`+ph+`)`), intsToArgs(req.IDs)...)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	var ids []int
	for rows.Next() {
		var (
			id  int
			lib sql.NullInt64
		)
		if err := rows.Scan(&id, &lib); err == nil {
			ids = append(ids, id)
			if !lib.Valid || int(lib.Int64) != *target {
				allInTarget = false
			}
		}
	}
	rows.Close()
	if len(ids) == 0 {
		writeErr(w, http.StatusBadRequest, "没有可移动的链接")
		return
	}
	if allInTarget {
		writeOK(w, map[string]any{"moved": 0, "message": "已全部在目标库中"})
		return
	}

	var maxSort sql.NullInt64
	_ = tx.QueryRow(a.db.Rebind(`SELECT MAX("SortOrder") FROM "Links" WHERE "LibraryId" = ? AND "IsDeleted" = `+a.db.False()), *target).Scan(&maxSort)
	next := int(maxSort.Int64)
	for _, id := range ids {
		next++
		args = args[:0]
		args = append(args, *target, next, id)
		if _, err := tx.Exec(a.db.Rebind(`UPDATE "Links" SET "LibraryId" = ?, "SortOrder" = ? WHERE "Id" = ?`), args...); err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
	}
	if err := tx.Commit(); err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeOK(w, map[string]int{"moved": len(ids)})
}

// handleCopyLinks POST /api/admin/links/copy（含 URL/附属链接/标签深拷贝）
func (a *App) handleCopyLinks(w http.ResponseWriter, r *http.Request) {
	var req MoveCopyLinksRequest
	if err := decodeJSON(r, &req, 8<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	if len(req.IDs) == 0 {
		writeErr(w, http.StatusBadRequest, "请先选择链接")
		return
	}
	if len(req.IDs) > 200 {
		writeErr(w, http.StatusBadRequest, "单次最多复制 200 条")
		return
	}
	target, err := a.db.resolveLibraryID(&req.TargetLibraryID)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if target == nil {
		writeErr(w, http.StatusBadRequest, "目标发布库不存在")
		return
	}

	tx, err := a.db.sql.Begin()
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	defer tx.Rollback()

	ph := strings.TrimSuffix(strings.Repeat("?,", len(req.IDs)), ",")
	rows, err := tx.Query(a.db.Rebind(`SELECT "Id","SortOrder","LinkType","GroupName","Title","Url","ExtractCode","ExpireAt","Platform","Version","Remark","Summary","ImportSource","IsHidden","IsDeleted"
		FROM "Links" WHERE "IsDeleted" = `+a.db.False()+` AND "Id" IN (`+ph+`)`), intsToArgs(req.IDs)...)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	var srcs []*Link
	for rows.Next() {
		var (
			l                                  Link
			importSource                       int
			extract, expire, platform, version sql.NullString
			remark, summary                    sql.NullString
			isHidden, isDeleted                any
		)
		if err := rows.Scan(&l.ID, &l.SortOrder, &l.LinkType, &l.GroupName, &l.Title, &l.URL,
			&extract, &expire, &platform, &version, &remark, &summary, &importSource, &isHidden, &isDeleted); err != nil {
			rows.Close()
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
		l.ExtractCode = nullableString(extract)
		l.ExpireAt = nullableString(expire)
		l.Platform = nullableString(platform)
		l.Version = nullableString(version)
		l.Remark = nullableString(remark)
		l.Summary = nullableString(summary)
		l.ImportSource = ImportSource(importSource)
		l.IsHidden = scanBool(isHidden)
		srcs = append(srcs, &l)
	}
	rows.Close()
	if len(srcs) == 0 {
		writeErr(w, http.StatusBadRequest, "没有可复制的链接")
		return
	}

	var maxSort sql.NullInt64
	_ = tx.QueryRow(a.db.Rebind(`SELECT MAX("SortOrder") FROM "Links" WHERE "LibraryId" = ? AND "IsDeleted" = `+a.db.False()), *target).Scan(&maxSort)
	next := int(maxSort.Int64)

	for _, src := range srcs {
		next++
		cp := *src
		cp.SortOrder = next
		cp.LibraryID = target
		newID, err := a.insertLink(tx, &cp)
		if err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
		// 深拷贝 URL / 附属链接 / 标签
		if err := copyChildren(tx, src.ID, newID, a); err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
	}
	if err := tx.Commit(); err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeOK(w, map[string]int{"copied": len(srcs)})
}

func copyChildren(tx *sql.Tx, fromID, toID int, a *App) error {
	rows, err := tx.Query(a.db.Rebind(`SELECT "Url","SortOrder" FROM "LinkUrls" WHERE "LinkId" = ?`), fromID)
	if err != nil {
		return err
	}
	type u struct {
		url string
		ord int
	}
	var urls []u
	for rows.Next() {
		var x u
		if err := rows.Scan(&x.url, &x.ord); err == nil {
			urls = append(urls, x)
		}
	}
	rows.Close()
	for _, x := range urls {
		if _, err := tx.Exec(a.db.Rebind(`INSERT INTO "LinkUrls" ("LinkId","Url","SortOrder") VALUES (?,?,?)`), toID, x.url, x.ord); err != nil {
			return err
		}
	}

	subRows, err := tx.Query(a.db.Rebind(`SELECT "Url","Title","SortOrder" FROM "LinkSubLinks" WHERE "LinkId" = ?`), fromID)
	if err != nil {
		return err
	}
	type s struct {
		url   string
		title sql.NullString
		ord   int
	}
	var subs []s
	for subRows.Next() {
		var x s
		if err := subRows.Scan(&x.url, &x.title, &x.ord); err == nil {
			subs = append(subs, x)
		}
	}
	subRows.Close()
	for _, x := range subs {
		if _, err := tx.Exec(a.db.Rebind(`INSERT INTO "LinkSubLinks" ("LinkId","Url","Title","SortOrder") VALUES (?,?,?,?)`), toID, x.url, nptr(nullableString(x.title)), x.ord); err != nil {
			return err
		}
	}

	_, _ = tx.Exec(a.db.Rebind(`INSERT INTO "LinkTags" ("LinkId","TagId") SELECT ?, "TagId" FROM "LinkTags" WHERE "LinkId" = ?`), toID, fromID)
	return nil
}

// handleImportLinks POST /api/admin/import/links?libraryId=
func (a *App) handleImportLinks(w http.ResponseWriter, r *http.Request) {
	var items []LinkInfoDto
	if err := decodeJSON(r, &items, 16<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	if len(items) == 0 {
		writeErr(w, http.StatusBadRequest, "没有可导入的链接")
		return
	}
	if len(items) > 500 {
		writeErr(w, http.StatusBadRequest, "单次最多导入 500 条")
		return
	}
	libID, err := a.db.resolveLibraryID(queryIntPtr(r, "libraryId"))
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if libID == nil {
		writeErr(w, http.StatusBadRequest, "发布库不存在")
		return
	}

	tx, err := a.db.sql.Begin()
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	defer tx.Rollback()

	count := 0
	for _, item := range items {
		urlText := extractURL(item.RawText)
		if urlText == "" {
			urlText = strings.TrimSpace(item.RawText)
		}
		if urlText == "" {
			urlText = strings.TrimSpace(item.URL)
		}
		if safe, err := sanitizeLinkURL(urlText); err != nil {
			// 单条危险协议直接跳过，不影响同批其它链接
			a.logf("导入跳过危险链接：%v", err)
			continue
		} else {
			urlText = safe
		}
		title := item.Title
		if strings.TrimSpace(title) == "" {
			if d := extractDomain(urlText); d != "" {
				title = d
			} else {
				title = urlText
			}
		}
		count++
		id, err := a.insertLink(tx, &Link{
			SortOrder: count, LinkType: nz(item.LinkType, "官网/普通网址"), GroupName: nz(item.GroupName, "未分类"),
			Title: title, URL: urlText, Platform: strPtrOrNil(item.Platform), Summary: strPtrOrNil(item.Summary),
			ImportSource: ImportAI, LibraryID: libID,
		})
		if err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
		if urlText != "" {
			if _, err := tx.Exec(a.db.Rebind(`INSERT INTO "LinkUrls" ("LinkId","Url","SortOrder") VALUES (?,?,?)`), id, urlText, 0); err != nil {
				writeErr(w, http.StatusInternalServerError, err.Error())
				return
			}
		}
		if err := a.replaceTags(tx, id, item.Tags); err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
	}
	if err := tx.Commit(); err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeOK(w, map[string]any{"imported": count, "source": "AI接入"})
}

// handleDedupe POST /api/admin/links/dedupe
func (a *App) handleDedupe(w http.ResponseWriter, r *http.Request) {
	var req DedupeRequest
	if err := decodeJSON(r, &req, 1<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	all := req.Scope == "all"
	cond := `"IsDeleted" = ` + a.db.False()
	args := []any{}
	if !all && req.LibraryID != nil && *req.LibraryID > 0 {
		cond += ` AND "LibraryId" = ?`
		args = append(args, *req.LibraryID)
	}
	rows, err := a.db.sql.Query(a.db.Rebind(`SELECT "Id","Url","Title","LibraryId" FROM "Links" WHERE `+cond), args...)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	type row struct {
		id    int
		url   string
		title string
		lib   sql.NullInt64
	}
	var all_rows []row
	for rows.Next() {
		var x row
		if err := rows.Scan(&x.id, &x.url, &x.title, &x.lib); err == nil {
			all_rows = append(all_rows, x)
		}
	}
	rows.Close()

	groups := map[string][]row{}
	for _, x := range all_rows {
		key := normalizeURLKey(x.url)
		if key == "" {
			continue
		}
		if all {
			libKey := "0"
			if x.lib.Valid {
				libKey = itoa(int(x.lib.Int64))
			}
			key = libKey + "|" + key
		}
		groups[key] = append(groups[key], x)
	}

	// 字段语义对齐 C# DedupeResult：
	//   Groups     = 有重复的组数（不是总分组数）
	//   Duplicates = 可清理的条数（每组 count-1 之和）
	//   Kept       = 保留的组数（每组留一条）
	result := DedupeResult{Scanned: len(all_rows)}
	var dupGroups [][]row
	for _, g := range groups {
		if len(g) > 1 {
			dupGroups = append(dupGroups, g)
		}
	}
	sort.Slice(dupGroups, func(i, j int) bool { return len(dupGroups[i]) > len(dupGroups[j]) })
	result.Groups = len(dupGroups)
	cleanable := 0
	for _, g := range dupGroups {
		cleanable += len(g) - 1
	}
	result.Duplicates = cleanable

	samples := make([]DedupeSampleDto, 0, min(8, len(dupGroups)))
	for i, g := range dupGroups {
		if i >= 8 {
			break
		}
		s := DedupeSampleDto{URL: g[0].url, Count: len(g)}
		for j, x := range g {
			if j >= 4 {
				break
			}
			s.Titles = append(s.Titles, x.title)
		}
		samples = append(samples, s)
	}
	result.Samples = samples

	toRemove := []int{}
	for _, g := range dupGroups {
		sort.Slice(g, func(i, j int) bool { return g[i].id < g[j].id })
		result.Kept++
		for _, x := range g[1:] {
			toRemove = append(toRemove, x.id)
		}
	}
	if req.DryRun {
		result.Kept = 0
		result.Message = fmt.Sprintf("预览：扫描 %d 条，发现 %d 组重复，可清理 %d 条", result.Scanned, result.Groups, result.Duplicates)
		writeOK(w, result)
		return
	}
	if len(toRemove) > 0 {
		removed, err := a.execOnIDs(toRemove, `UPDATE "Links" SET "IsDeleted" = ? WHERE "Id" IN (%s)`, a.db.BoolValue(true))
		if err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
		result.Removed = removed
	}
	if result.Removed > 0 {
		result.Message = fmt.Sprintf("去重完成：保留 %d 条，删除重复 %d 条", result.Kept, result.Removed)
	} else {
		result.Message = "没有发现重复链接"
	}
	writeOK(w, result)
}

func normalizeURLKey(u string) string {
	s := strings.ToLower(strings.TrimSpace(u))
	for len(s) > 1 && strings.HasSuffix(s, "/") {
		s = s[:len(s)-1]
	}
	return s
}

func intsToArgs(ids []int) []any {
	out := make([]any, len(ids))
	for i, v := range ids {
		out[i] = v
	}
	return out
}

func ptrStr(p *string) string {
	if p == nil {
		return ""
	}
	return *p
}

func strPtrOrNil(s string) *string {
	if strings.TrimSpace(s) == "" {
		return nil
	}
	return &s
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	neg := n < 0
	if neg {
		n = -n
	}
	var buf [20]byte
	i := len(buf)
	for n > 0 {
		i--
		buf[i] = byte('0' + n%10)
		n /= 10
	}
	if neg {
		i--
		buf[i] = '-'
	}
	return string(buf[i:])
}

// extractDomain 取 host，去掉 www.
func extractDomain(raw string) string {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return ""
	}
	u, err := url.Parse(raw)
	if err != nil || u.Host == "" {
		if !strings.Contains(raw, "://") {
			if u2, err2 := url.Parse("http://" + raw); err2 == nil {
				u = u2
			}
		}
	}
	if u == nil || u.Host == "" {
		return ""
	}
	return strings.TrimPrefix(u.Host, "www.")
}

var urlPattern = regexp.MustCompile(`(?i)(?:https?|ftp)://[^\s"'<>）】\]]+`)

func extractURL(text string) string {
	m := urlPattern.FindString(text)
	return m
}

// dangerousSchemes 是浏览器会当脚本/数据执行的协议，落库后一旦渲染成 href 就是存储型 XSS。
// 只拦黑名单不拦白名单：库里合法存在 magnet:、ed2k:、thunder: 等自定义协议。
var dangerousSchemes = map[string]bool{
	"javascript": true, "vbscript": true, "data": true,
	"file": true, "about": true, "blob": true, "jar": true,
}

// sanitizeLinkURL 拒绝危险协议；空串与相对路径放行。
func sanitizeLinkURL(raw string) (string, error) {
	u := strings.TrimSpace(raw)
	if u == "" {
		return "", nil
	}
	// 去掉协议前可能存在的空白/控制字符，防止 "java\tscript:" 之类的变形
	probe := strings.Map(func(r rune) rune {
		if r == '\t' || r == '\n' || r == '\r' || r == ' ' || r == 0 {
			return -1
		}
		return r
	}, u)
	if i := strings.Index(probe, ":"); i > 0 {
		scheme := strings.ToLower(probe[:i])
		valid := true
		for _, c := range scheme {
			if !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '+' && c != '-' && c != '.' {
				valid = false
				break
			}
		}
		if valid && dangerousSchemes[scheme] {
			return "", fmt.Errorf("不允许的链接协议：%s:", scheme)
		}
	}
	return u, nil
}
