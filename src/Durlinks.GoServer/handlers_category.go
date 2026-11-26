package main

import (
	"database/sql"
	"net/http"
	"sort"
	"strings"
)

type categoryKind struct {
	table   string
	linkCol string
	label   string
	plural  string
}

var categoryKinds = map[string]categoryKind{
	"group":    {table: "Groups", linkCol: "GroupName", label: "分组", plural: "groups"},
	"platform": {table: "Platforms", linkCol: "Platform", label: "平台", plural: "platforms"},
	"tag":      {table: "Tags", linkCol: "", label: "标签", plural: "tags"},
	"type":     {table: "LinkTypes", linkCol: "LinkType", label: "链接形式", plural: "types"},
}

// 多值字段（分组/平台）以逗号分隔存储，与 store.go 的原子匹配口径一致。
// 单值字段（type）与 ID 关联字段（tag）不走这些辅助函数。
func isMultiValueCol(col string) bool { return col == "GroupName" || col == "Platform" }

// splitMultiValue 把逗号分隔的多值字段拆成去空白的原子列表。
func splitMultiValue(v string) []string {
	parts := strings.Split(v, ",")
	out := make([]string, 0, len(parts))
	for _, p := range parts {
		if s := strings.TrimSpace(p); s != "" {
			out = append(out, s)
		}
	}
	return out
}

// joinMultiValue 组装回逗号分隔（不带空格，与 store.go 的 LIKE '%,x,%' 匹配口径一致）。
func joinMultiValue(parts []string) string { return strings.Join(parts, ",") }

// dbQuerier 同时兼容 *sql.DB 与 *sql.Tx。
type dbQuerier interface {
	Exec(query string, args ...any) (sql.Result, error)
	Query(query string, args ...any) (*sql.Rows, error)
	QueryRow(query string, args ...any) *sql.Row
}

// multiValueInUse 判断多值字段中是否含有某个原子值（逗号包裹精确匹配）。
func (a *App) multiValueInUse(q dbQuerier, col, name string) bool {
	sql := a.db.Rebind(`SELECT COUNT(*) FROM "Links" WHERE ` + a.db.concat(`','`, `"`+col+`"`, `','`) + ` LIKE ?`)
	var n int
	if err := q.QueryRow(sql, "%,"+name+",%").Scan(&n); err != nil {
		return false
	}
	return n > 0
}

// renameMultiValue 在多值字段中把 old 原子替换为 new，逐行拆分重组，避免误伤同串里的其它值。
func (a *App) renameMultiValue(q dbQuerier, col, old, new string) error {
	rows, err := q.Query(a.db.Rebind(`SELECT "Id", "`+col+`" FROM "Links" WHERE `+
		a.db.concat(`','`, `"`+col+`"`, `','`)+` LIKE ?`), "%,"+old+",%")
	if err != nil {
		return err
	}
	type update struct {
		id  int
		val string
	}
	var updates []update
	for rows.Next() {
		var id int
		var v sql.NullString
		if err := rows.Scan(&id, &v); err != nil {
			rows.Close()
			return err
		}
		if !v.Valid {
			continue
		}
		parts := splitMultiValue(v.String)
		changed := false
		for i, p := range parts {
			if p == old {
				parts[i] = new
				changed = true
			}
		}
		if changed {
			updates = append(updates, update{id, joinMultiValue(parts)})
		}
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return err
	}
	for _, u := range updates {
		if _, err := q.Exec(a.db.Rebind(`UPDATE "Links" SET "`+col+`" = ? WHERE "Id" = ?`), u.val, u.id); err != nil {
			return err
		}
	}
	return nil
}

// handleCategories GET /api/admin/categories
func (a *App) handleCategories(w http.ResponseWriter, r *http.Request) {
	out := CategoryOptionsDto{Groups: []string{}, Platforms: []string{}, Tags: []string{}, LinkTypes: []string{}}
	load := func(table, orderBy string) []string {
		rows, err := a.db.sql.Query(`SELECT "Name" FROM "` + table + `" ORDER BY ` + orderBy)
		if err != nil {
			return []string{}
		}
		defer rows.Close()
		items := []string{}
		for rows.Next() {
			var n string
			if err := rows.Scan(&n); err == nil {
				items = append(items, n)
			}
		}
		return items
	}
	out.Groups = load("Groups", `"Name"`)
	out.Platforms = load("Platforms", `"Name"`)
	out.Tags = load("Tags", `"Name"`)
	out.LinkTypes = load("LinkTypes", `"Id"`)
	writeOK(w, out)
}

func normalizeCategoryName(v *string) (string, bool) {
	if v == nil {
		return "", false
	}
	name := strings.TrimSpace(*v)
	if name == "" || len([]rune(name)) > 200 {
		return "", false
	}
	return name, true
}

func (a *App) categoryExists(kind, name string) bool {
	k, ok := categoryKinds[strings.ToLower(kind)]
	if !ok {
		return false
	}
	var n int
	_ = a.db.sql.QueryRow(a.db.Rebind(`SELECT COUNT(*) FROM "`+k.table+`" WHERE "Name" = ?`), name).Scan(&n)
	return n > 0
}

// handleCreateCategory POST /api/admin/categories/{kind}
func (a *App) handleCreateCategory(w http.ResponseWriter, r *http.Request) {
	kind := strings.ToLower(r.PathValue("kind"))
	k, ok := categoryKinds[kind]
	if !ok {
		writeErr(w, http.StatusBadRequest, "类别必须是 group、platform、tag 或 type")
		return
	}
	var req CategoryNameRequest
	if err := decodeJSON(r, &req, 1<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	name, valid := normalizeCategoryName(req.Name)
	if !valid {
		writeErr(w, http.StatusBadRequest, "名称不能为空，且不能超过 200 个字符")
		return
	}
	if a.categoryExists(kind, name) {
		writeErr(w, http.StatusConflict, "该名称已存在")
		return
	}
	if _, err := a.db.sql.Exec(a.db.Rebind(`INSERT INTO "`+k.table+`" ("Name") VALUES (?)`), name); err != nil {
		writeInternalErr(w, err)
		return
	}
	writeOK(w, map[string]string{"name": name})
}

// handleRenameCategory PUT /api/admin/categories/{kind}
func (a *App) handleRenameCategory(w http.ResponseWriter, r *http.Request) {
	kind := strings.ToLower(r.PathValue("kind"))
	k, ok := categoryKinds[kind]
	if !ok {
		writeErr(w, http.StatusBadRequest, "类别必须是 group、platform、tag 或 type")
		return
	}
	var req CategoryRenameRequest
	if err := decodeJSON(r, &req, 1<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	oldName, ok1 := normalizeCategoryName(req.OldName)
	newName, ok2 := normalizeCategoryName(req.NewName)
	if !ok1 || !ok2 {
		writeErr(w, http.StatusBadRequest, "名称不能为空，且不能超过 200 个字符")
		return
	}
	if oldName == newName {
		writeOK(w, map[string]string{"name": newName})
		return
	}
	if a.categoryExists(kind, newName) {
		writeErr(w, http.StatusConflict, "目标名称已存在")
		return
	}

	tx, err := a.db.sql.Begin()
	if err != nil {
		writeInternalErr(w, err)
		return
	}
	defer tx.Rollback()

	res, err := tx.Exec(a.db.Rebind(`UPDATE "`+k.table+`" SET "Name" = ? WHERE "Name" = ?`), newName, oldName)
	if err != nil {
		writeInternalErr(w, err)
		return
	}
	if n, _ := res.RowsAffected(); n == 0 {
		writeErr(w, http.StatusNotFound, "项目不存在")
		return
	}
	if k.linkCol != "" {
		if isMultiValueCol(k.linkCol) {
			// 多值字段：拆分后替换原子，避免 "音乐,电影" 这样的组合值被漏改
			if err := a.renameMultiValue(tx, k.linkCol, oldName, newName); err != nil {
				writeInternalErr(w, err)
				return
			}
		} else {
			// 单值字段（LinkType）：精确替换
			if _, err := tx.Exec(a.db.Rebind(`UPDATE "Links" SET "`+k.linkCol+`" = ? WHERE "`+k.linkCol+`" = ?`), newName, oldName); err != nil {
				writeInternalErr(w, err)
				return
			}
		}
	}
	if err := tx.Commit(); err != nil {
		writeInternalErr(w, err)
		return
	}
	writeOK(w, map[string]string{"name": newName})
}

// handleDeleteCategory DELETE /api/admin/categories/{kind}?name=
func (a *App) handleDeleteCategory(w http.ResponseWriter, r *http.Request) {
	kind := strings.ToLower(r.PathValue("kind"))
	k, ok := categoryKinds[kind]
	if !ok {
		writeErr(w, http.StatusBadRequest, "类别必须是 group、platform、tag 或 type")
		return
	}
	name := strings.TrimSpace(r.URL.Query().Get("name"))
	if name == "" {
		writeErr(w, http.StatusBadRequest, "名称无效")
		return
	}
	if !a.categoryExists(kind, name) {
		writeErr(w, http.StatusNotFound, "项目不存在")
		return
	}
	if k.linkCol != "" {
		used := false
		if isMultiValueCol(k.linkCol) {
			used = a.multiValueInUse(a.db.sql, k.linkCol, name)
		} else {
			var n int
			_ = a.db.sql.QueryRow(a.db.Rebind(`SELECT COUNT(*) FROM "Links" WHERE "`+k.linkCol+`" = ?`), name).Scan(&n)
			used = n > 0
		}
		if used {
			writeErr(w, http.StatusConflict, "该"+k.label+"仍被链接使用，请先调整链接"+k.label)
			return
		}
	}
	if kind == "tag" {
		var tagID int
		if err := a.db.sql.QueryRow(a.db.Rebind(`SELECT "Id" FROM "Tags" WHERE "Name" = ?`), name).Scan(&tagID); err == nil {
			_, _ = a.db.sql.Exec(a.db.Rebind(`DELETE FROM "LinkTags" WHERE "TagId" = ?`), tagID)
		}
	}
	if _, err := a.db.sql.Exec(a.db.Rebind(`DELETE FROM "`+k.table+`" WHERE "Name" = ?`), name); err != nil {
		writeInternalErr(w, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// handleBatchDeleteCategories POST /api/admin/categories/{kind}/batch-delete
func (a *App) handleBatchDeleteCategories(w http.ResponseWriter, r *http.Request) {
	kind := strings.ToLower(r.PathValue("kind"))
	k, ok := categoryKinds[kind]
	if !ok {
		writeErr(w, http.StatusBadRequest, "类别必须是 group、platform、tag 或 type")
		return
	}
	var req CategoryBatchDeleteRequest
	if err := decodeJSON(r, &req, 1<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	seen := map[string]bool{}
	var names []string
	for _, raw := range req.Names {
		name := strings.TrimSpace(raw)
		if name == "" || len([]rune(name)) > 200 || seen[name] {
			continue
		}
		seen[name] = true
		names = append(names, name)
	}
	if len(names) == 0 {
		writeErr(w, http.StatusBadRequest, "未选择有效项目")
		return
	}
	if len(names) > maxBatchIDs {
		names = names[:maxBatchIDs]
	}

	result := CategoryBatchDeleteResult{Deleted: []string{}, Skipped: []string{}}
	if kind == "tag" {
		// 先解除关联，再删标签
		ph := strings.TrimSuffix(strings.Repeat("?,", len(names)), ",")
		args := strArgs(names)
		if rows, err := a.db.sql.Query(a.db.Rebind(`SELECT "Id","Name" FROM "Tags" WHERE "Name" IN (`+ph+`)`), args...); err == nil {
			type pair struct {
				id   int
				name string
			}
			var pairs []pair
			for rows.Next() {
				var p pair
				if err := rows.Scan(&p.id, &p.name); err == nil {
					pairs = append(pairs, p)
				}
			}
			rows.Close()
			for _, p := range pairs {
				_, _ = a.db.sql.Exec(a.db.Rebind(`DELETE FROM "LinkTags" WHERE "TagId" = ?`), p.id)
				_, _ = a.db.sql.Exec(a.db.Rebind(`DELETE FROM "Tags" WHERE "Id" = ?`), p.id)
				result.Deleted = append(result.Deleted, p.name)
			}
		}
		sort.Strings(result.Deleted)
		for _, n := range names {
			found := false
			for _, d := range result.Deleted {
				if d == n {
					found = true
					break
				}
			}
			if !found {
				result.Skipped = append(result.Skipped, n)
			}
		}
		writeOK(w, result)
		return
	}

	// group/platform/type：仍被链接引用的跳过
	used := map[string]bool{}
	if isMultiValueCol(k.linkCol) {
		// 多值字段：原子匹配，避免 "音乐,电影" 中的 "音乐" 被漏检
		for _, name := range names {
			if a.multiValueInUse(a.db.sql, k.linkCol, name) {
				used[name] = true
			}
		}
	} else {
		ph := strings.TrimSuffix(strings.Repeat("?,", len(names)), ",")
		if rows, err := a.db.sql.Query(a.db.Rebind(`SELECT DISTINCT "`+k.linkCol+`" FROM "Links" WHERE "`+k.linkCol+`" IN (`+ph+`)`), strArgs(names)...); err == nil {
			for rows.Next() {
				var v sql.NullString
				if err := rows.Scan(&v); err == nil && v.Valid {
					used[v.String] = true
				}
			}
			rows.Close()
		}
	}
	for _, name := range names {
		if used[name] {
			result.Skipped = append(result.Skipped, name)
			continue
		}
		res, err := a.db.sql.Exec(a.db.Rebind(`DELETE FROM "`+k.table+`" WHERE "Name" = ?`), name)
		if err != nil {
			result.Skipped = append(result.Skipped, name)
			continue
		}
		if n, _ := res.RowsAffected(); n > 0 {
			result.Deleted = append(result.Deleted, name)
		} else {
			result.Skipped = append(result.Skipped, name)
		}
	}
	writeOK(w, result)
}

func strArgs(items []string) []any {
	out := make([]any, len(items))
	for i, v := range items {
		out[i] = v
	}
	return out
}
