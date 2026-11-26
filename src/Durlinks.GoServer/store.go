package main

import (
	"database/sql"
	"fmt"
	"strings"
)

// likeOp PostgreSQL 的 LIKE 区分大小写，用 ILIKE 保持与 SQLite 一致的宽松匹配。
func (d *DB) likeOp() string {
	if d.pg {
		return "ILIKE"
	}
	return "LIKE"
}

func (d *DB) concat(parts ...string) string {
	return strings.Join(parts, " || ")
}

// buildLinkWhere 生成筛选条件。public=true 时附加"库可见 + 未隐藏"约束。
func (d *DB) buildLinkWhere(q LinkQuery, public bool) (string, []any) {
	var conds []string
	var args []any

	if public {
		conds = append(conds, `l."IsHidden" = `+d.False(), `l."IsDeleted" = `+d.False())
		conds = append(conds, `lib."Id" IS NOT NULL`, `lib."IsDeleted" = `+d.False())
		if q.Channel == "client" {
			conds = append(conds, `lib."VisibleOnClient" = `+d.True())
		} else {
			conds = append(conds, `lib."VisibleOnWeb" = `+d.True())
		}
	} else {
		conds = append(conds, `l."IsDeleted" = `+d.False())
	}

	if q.ID > 0 {
		conds = append(conds, `l."Id" = ?`)
		args = append(args, q.ID)
	}

	if q.LibraryID != nil && *q.LibraryID > 0 {
		conds = append(conds, `l."LibraryId" = ?`)
		args = append(args, *q.LibraryID)
	}

	if q.Q != "" {
		kw := "%" + q.Q + "%"
		like := d.likeOp()
		conds = append(conds, fmt.Sprintf(`(l."Title" %s ? OR l."Url" %s ? OR l."GroupName" %s ? OR COALESCE(l."Summary",'') %s ? OR COALESCE(l."Platform",'') %s ?)`,
			like, like, like, like, like))
		args = append(args, kw, kw, kw, kw, kw)
	}

	// 分组：多值优先，回退单值；逗号包裹做原子匹配
	if len(q.Groups) > 0 {
		var ors []string
		for _, g := range q.Groups {
			ors = append(ors, d.concat(`','`, `l."GroupName"`, `','`)+` LIKE ?`)
			args = append(args, "%,"+g+",%")
		}
		conds = append(conds, "("+strings.Join(ors, " OR ")+")")
	} else if q.Group != "" {
		conds = append(conds, d.concat(`','`, `l."GroupName"`, `','`)+` LIKE ?`)
		args = append(args, "%,"+q.Group+",%")
	}

	if len(q.Platforms) > 0 {
		var ors []string
		for _, p := range q.Platforms {
			ors = append(ors, d.concat(`','`, `COALESCE(l."Platform",'')`, `','`)+` LIKE ?`)
			args = append(args, "%,"+p+",%")
		}
		conds = append(conds, "("+strings.Join(ors, " OR ")+")")
	} else if q.Platform != "" {
		conds = append(conds, d.concat(`','`, `COALESCE(l."Platform",'')`, `','`)+` LIKE ?`)
		args = append(args, "%,"+q.Platform+",%")
	}

	if len(q.ImportSources) > 0 {
		ph := make([]string, len(q.ImportSources))
		for i, s := range q.ImportSources {
			ph[i] = "?"
			args = append(args, int(s))
		}
		conds = append(conds, `l."ImportSource" IN (`+strings.Join(ph, ",")+`)`)
	} else if q.ImportSource != nil {
		conds = append(conds, `l."ImportSource" = ?`)
		args = append(args, int(*q.ImportSource))
	}

	if len(q.Tags) > 0 {
		ph := make([]string, len(q.Tags))
		for i, t := range q.Tags {
			ph[i] = "?"
			args = append(args, t)
		}
		conds = append(conds, `EXISTS (SELECT 1 FROM "LinkTags" lt JOIN "Tags" tg ON tg."Id"=lt."TagId" WHERE lt."LinkId"=l."Id" AND tg."Name" IN (`+strings.Join(ph, ",")+`))`)
	} else if q.Tag != "" {
		conds = append(conds, `EXISTS (SELECT 1 FROM "LinkTags" lt JOIN "Tags" tg ON tg."Id"=lt."TagId" WHERE lt."LinkId"=l."Id" AND tg."Name" = ?)`)
		args = append(args, q.Tag)
	}

	return " WHERE " + strings.Join(conds, " AND "), args
}

// 每页条数上限：与前端下拉选项（20/50/100/200/500/1000）保持一致，
// 否则用户选 500 只会拿到 200 条，UI 与实际数据不符。
const (
	publicMaxPageSize = 1000
	adminMaxPageSize  = 1000
)

// queryLinks 分页查询链接并补齐 urls / subLinks / tags。
func (d *DB) queryLinks(q LinkQuery, public bool) ([]LinkDto, int, error) {
	q.Page = max(1, q.Page)
	limit := adminMaxPageSize
	if public {
		limit = publicMaxPageSize
	}
	q.PageSize = clamp(q.PageSize, 1, limit)
	// 限制页数上限，避免 (page-1)*pageSize 整数溢出导致 offset 变负：
	// SQLite 负 offset 视为 0（错返首页），PostgreSQL 直接报错 500。
	const maxPage = 1000000
	if q.Page > maxPage {
		q.Page = maxPage
	}
	if q.Page < 1 {
		q.Page = 1
	}

	where, args := d.buildLinkWhere(q, public)
	base := ` FROM "Links" l LEFT JOIN "PublishLibraries" lib ON lib."Id" = l."LibraryId"` + where

	var total int
	if err := d.sql.QueryRow(d.Rebind(`SELECT COUNT(*)`+base), args...).Scan(&total); err != nil {
		return nil, 0, err
	}
	items := []LinkDto{}
	if total == 0 {
		return items, 0, nil
	}

	offset := (q.Page - 1) * q.PageSize
	listSQL := d.Rebind(`SELECT l."Id", l."SortOrder", l."LinkType", l."GroupName", l."Title", l."Url",
		l."ExtractCode", l."ExpireAt", l."Platform", l."Version", l."Remark", l."Summary",
		l."CreatedAt", l."ImportSource", l."IsHidden", l."IsDeleted", l."LibraryId", lib."Name"` + base +
		` ORDER BY l."SortOrder", l."Id" LIMIT ? OFFSET ?`)
	rows, err := d.sql.Query(listSQL, append(args, q.PageSize, offset)...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()

	for rows.Next() {
		var (
			it                                 LinkDto
			sortOrder, importSource            int
			extract, expire, platform, version sql.NullString
			remark, summary, libraryName       sql.NullString
			createdAt                          any
			isHidden, isDeleted                any
			libraryID                          sql.NullInt64
		)
		if err := rows.Scan(&it.ID, &sortOrder, &it.LinkType, &it.GroupName, &it.Title, &it.URL,
			&extract, &expire, &platform, &version, &remark, &summary,
			&createdAt, &importSource, &isHidden, &isDeleted, &libraryID, &libraryName); err != nil {
			return nil, 0, err
		}
		it.SortOrder = sortOrder
		it.ExtractCode = nullableString(extract)
		it.ExpireAt = nullableString(expire)
		it.Platform = nullableString(platform)
		it.Version = nullableString(version)
		it.Remark = nullableString(remark)
		it.Summary = nullableString(summary)
		it.CreatedAt = isoTime(scanTime(createdAt))
		it.ImportSource = ImportSource(importSource)
		if it.ImportSource == ImportAI {
			it.ImportSourceLabel = "AI接入"
		} else {
			it.ImportSourceLabel = "人工导入"
		}
		it.IsHidden = scanBool(isHidden)
		it.IsDeleted = scanBool(isDeleted)
		it.LibraryID = nullableInt(libraryID)
		it.LibraryName = nullableString(libraryName)
		it.Urls = []string{}
		it.SubLinks = []SubLinkDto{}
		it.Tags = []string{}
		items = append(items, it)
	}
	if err := rows.Err(); err != nil {
		return nil, 0, err
	}

	if err := d.attachDetails(items); err != nil {
		return nil, 0, err
	}
	return items, total, nil
}

// attachDetails 批量补 urls / subLinks / tags（避免 N+1）。
func (d *DB) attachDetails(items []LinkDto) error {
	if len(items) == 0 {
		return nil
	}
	ids := make([]any, len(items))
	index := make(map[int]*LinkDto, len(items))
	for i := range items {
		ids[i] = items[i].ID
		index[items[i].ID] = &items[i]
	}
	ph := strings.TrimSuffix(strings.Repeat("?,", len(ids)), ",")

	rows, err := d.sql.Query(d.Rebind(`SELECT "LinkId","Url" FROM "LinkUrls" WHERE "LinkId" IN (`+ph+`) ORDER BY "SortOrder","Id"`), ids...)
	if err != nil {
		return err
	}
	for rows.Next() {
		var linkID int
		var u string
		if err := rows.Scan(&linkID, &u); err != nil {
			rows.Close()
			return err
		}
		if it, ok := index[linkID]; ok {
			it.Urls = append(it.Urls, u)
		}
	}
	rows.Close()

	rows2, err := d.sql.Query(d.Rebind(`SELECT "LinkId","Url","Title" FROM "LinkSubLinks" WHERE "LinkId" IN (`+ph+`) ORDER BY "SortOrder","Id"`), ids...)
	if err != nil {
		return err
	}
	for rows2.Next() {
		var (
			linkID int
			u      string
			title  sql.NullString
		)
		if err := rows2.Scan(&linkID, &u, &title); err != nil {
			rows2.Close()
			return err
		}
		if it, ok := index[linkID]; ok {
			it.SubLinks = append(it.SubLinks, SubLinkDto{URL: u, Title: nullableString(title)})
		}
	}
	rows2.Close()

	rows3, err := d.sql.Query(d.Rebind(`SELECT lt."LinkId", tg."Name" FROM "LinkTags" lt JOIN "Tags" tg ON tg."Id"=lt."TagId" WHERE lt."LinkId" IN (`+ph+`) ORDER BY tg."Name"`), ids...)
	if err != nil {
		return err
	}
	for rows3.Next() {
		var (
			linkID int
			name   string
		)
		if err := rows3.Scan(&linkID, &name); err != nil {
			rows3.Close()
			return err
		}
		if it, ok := index[linkID]; ok {
			it.Tags = append(it.Tags, name)
		}
	}
	rows3.Close()
	return nil
}

// resolveLibraryID 未指定或无效时回退第一个可用库；指定的库不存在返回 nil。
func (d *DB) resolveLibraryID(libraryID *int) (*int, error) {
	if libraryID != nil && *libraryID > 0 {
		var n int
		err := d.sql.QueryRow(d.Rebind(`SELECT COUNT(*) FROM "PublishLibraries" WHERE "Id" = ? AND "IsDeleted" = `+d.False()), *libraryID).Scan(&n)
		if err != nil {
			return nil, err
		}
		if n == 0 {
			return nil, nil
		}
		return libraryID, nil
	}
	var id sql.NullInt64
	err := d.sql.QueryRow(`SELECT "Id" FROM "PublishLibraries" WHERE "IsDeleted" = ` + d.False() + ` ORDER BY "SortOrder","Id" LIMIT 1`).Scan(&id)
	if err != nil {
		if err == sql.ErrNoRows {
			return nil, nil
		}
		return nil, err
	}
	if !id.Valid {
		return nil, nil
	}
	v := int(id.Int64)
	return &v, nil
}

func (d *DB) maxSortOrder(libraryID int) (int, error) {
	var maxSort sql.NullInt64
	err := d.sql.QueryRow(d.Rebind(`SELECT MAX("SortOrder") FROM "Links" WHERE "LibraryId" = ?`), libraryID).Scan(&maxSort)
	if err != nil {
		return 0, err
	}
	if maxSort.Valid {
		return int(maxSort.Int64), nil
	}
	return 0, nil
}

// ensureTag 取同名标签，不存在则创建。调用方负责处理并发唯一索引冲突。
func (d *DB) ensureTag(name string) (int, error) {
	var id int
	err := d.sql.QueryRow(d.Rebind(`SELECT "Id" FROM "Tags" WHERE "Name" = ?`), name).Scan(&id)
	if err == nil {
		return id, nil
	}
	if err != sql.ErrNoRows {
		return 0, err
	}
	if d.pg {
		if err := d.sql.QueryRow(d.Rebind(`INSERT INTO "Tags" ("Name") VALUES (?) RETURNING "Id"`), name).Scan(&id); err == nil {
			return id, nil
		}
	} else {
		if res, err := d.sql.Exec(d.Rebind(`INSERT INTO "Tags" ("Name") VALUES (?)`), name); err == nil {
			if last, err2 := res.LastInsertId(); err2 == nil {
				return int(last), nil
			}
		}
	}
	// 竞态：别人刚插入，重查
	if err := d.sql.QueryRow(d.Rebind(`SELECT "Id" FROM "Tags" WHERE "Name" = ?`), name).Scan(&id); err != nil {
		return 0, err
	}
	return id, nil
}
