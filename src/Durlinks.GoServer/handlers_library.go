package main

import (
	"database/sql"
	"net/http"
	"strings"
)

// handlePublicLibraries GET /api/libraries?channel=web|client
func (a *App) handlePublicLibraries(w http.ResponseWriter, r *http.Request) {
	channel := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("channel")))
	cond := `"IsDeleted" = ` + a.db.False()
	if channel == "client" {
		cond += ` AND "VisibleOnClient" = ` + a.db.True()
	} else {
		cond += ` AND "VisibleOnWeb" = ` + a.db.True()
	}
	rows, err := a.db.sql.Query(`SELECT "Id","Name","Description","VisibleOnWeb","VisibleOnClient","SortOrder","CreatedAt" FROM "PublishLibraries" WHERE ` + cond + ` ORDER BY "SortOrder","Id"`)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	defer rows.Close()
	out := []LibraryDto{}
	for rows.Next() {
		var (
			id, sortOrder   int
			name            string
			desc            sql.NullString
			onWeb, onClient any
			createdAt       any
		)
		if err := rows.Scan(&id, &name, &desc, &onWeb, &onClient, &sortOrder, &createdAt); err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
		out = append(out, LibraryDto{
			ID: id, Name: name, Description: nullableString(desc),
			VisibleOnWeb: scanBool(onWeb), VisibleOnClient: scanBool(onClient),
			SortOrder: sortOrder, CreatedAt: isoTime(scanTime(createdAt)),
		})
	}
	writeOK(w, out)
}

// handleAdminLibraries GET /api/admin/libraries（含各库链接数）
func (a *App) handleAdminLibraries(w http.ResponseWriter, r *http.Request) {
	counts := map[int]int{}
	if rows, err := a.db.sql.Query(`SELECT "LibraryId", COUNT(*) FROM "Links" WHERE "IsDeleted" = ` + a.db.False() + ` AND "LibraryId" IS NOT NULL GROUP BY "LibraryId"`); err == nil {
		for rows.Next() {
			var id, n int
			if err := rows.Scan(&id, &n); err == nil {
				counts[id] = n
			}
		}
		rows.Close()
	}
	rows, err := a.db.sql.Query(`SELECT "Id","Name","Description","VisibleOnWeb","VisibleOnClient","SortOrder","CreatedAt" FROM "PublishLibraries" WHERE "IsDeleted" = ` + a.db.False() + ` ORDER BY "SortOrder","Id"`)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	defer rows.Close()
	out := []LibraryDto{}
	for rows.Next() {
		var (
			id, sortOrder   int
			name            string
			desc            sql.NullString
			onWeb, onClient any
			createdAt       any
		)
		if err := rows.Scan(&id, &name, &desc, &onWeb, &onClient, &sortOrder, &createdAt); err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
		out = append(out, LibraryDto{
			ID: id, Name: name, Description: nullableString(desc),
			VisibleOnWeb: scanBool(onWeb), VisibleOnClient: scanBool(onClient),
			SortOrder: sortOrder, LinkCount: counts[id], CreatedAt: isoTime(scanTime(createdAt)),
		})
	}
	writeOK(w, out)
}

// handleCreateLibrary POST /api/admin/libraries
func (a *App) handleCreateLibrary(w http.ResponseWriter, r *http.Request) {
	var dto LibrarySaveRequest
	if err := decodeJSON(r, &dto, 1<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	name := strings.TrimSpace(dto.Name)
	if name == "" {
		writeErr(w, http.StatusBadRequest, "发布库名称不能为空")
		return
	}
	if len([]rune(name)) > 100 {
		writeErr(w, http.StatusBadRequest, "名称不能超过 100 字")
		return
	}
	var exists int
	_ = a.db.sql.QueryRow(a.db.Rebind(`SELECT COUNT(*) FROM "PublishLibraries" WHERE "IsDeleted" = `+a.db.False()+` AND "Name" = ?`), name).Scan(&exists)
	if exists > 0 {
		writeErr(w, http.StatusBadRequest, "已存在同名发布库")
		return
	}
	var maxSort sql.NullInt64
	_ = a.db.sql.QueryRow(`SELECT MAX("SortOrder") FROM "PublishLibraries" WHERE "IsDeleted" = ` + a.db.False()).Scan(&maxSort)
	sortOrder := dto.SortOrder
	if sortOrder == 0 {
		sortOrder = int(maxSort.Int64) + 1
	}
	desc := dto.Description
	if desc != nil {
		trimmed := strings.TrimSpace(*desc)
		desc = &trimmed
	}
	created := a.db.now()
	var id int
	if a.db.pg {
		err := a.db.sql.QueryRow(`INSERT INTO "PublishLibraries" ("Name","Description","VisibleOnWeb","VisibleOnClient","SortOrder","IsDeleted","CreatedAt")
			VALUES (?,?,?,?,?,?,?) RETURNING "Id"`, name, nptr(desc), dto.VisibleOnWeb, dto.VisibleOnClient, sortOrder, false, created).Scan(&id)
		if err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
	} else {
		res, err := a.db.sql.Exec(a.db.Rebind(`INSERT INTO "PublishLibraries" ("Name","Description","VisibleOnWeb","VisibleOnClient","SortOrder","IsDeleted","CreatedAt")
			VALUES (?,?,?,?,?,?,?)`), name, nptr(desc), a.db.BoolValue(dto.VisibleOnWeb), a.db.BoolValue(dto.VisibleOnClient), sortOrder, a.db.BoolValue(false), created)
		if err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
		last, _ := res.LastInsertId()
		id = int(last)
	}
	writeOK(w, LibraryDto{
		ID: id, Name: name, Description: desc, VisibleOnWeb: dto.VisibleOnWeb,
		VisibleOnClient: dto.VisibleOnClient, SortOrder: sortOrder, LinkCount: 0, CreatedAt: isoTime(nowTime()),
	})
}

// handleUpdateLibrary PUT /api/admin/libraries/{id}
func (a *App) handleUpdateLibrary(w http.ResponseWriter, r *http.Request) {
	id, ok := pathID(r, "id")
	if !ok {
		writeErr(w, http.StatusNotFound, "发布库不存在")
		return
	}
	var dto LibrarySaveRequest
	if err := decodeJSON(r, &dto, 1<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	name := strings.TrimSpace(dto.Name)
	if name == "" {
		writeErr(w, http.StatusBadRequest, "发布库名称不能为空")
		return
	}
	var target int
	err := a.db.sql.QueryRow(a.db.Rebind(`SELECT COUNT(*) FROM "PublishLibraries" WHERE "Id" = ? AND "IsDeleted" = `+a.db.False()), id).Scan(&target)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if target == 0 {
		writeErr(w, http.StatusNotFound, "发布库不存在")
		return
	}
	var dup int
	_ = a.db.sql.QueryRow(a.db.Rebind(`SELECT COUNT(*) FROM "PublishLibraries" WHERE "IsDeleted" = `+a.db.False()+` AND "Id" <> ? AND "Name" = ?`), id, name).Scan(&dup)
	if dup > 0 {
		writeErr(w, http.StatusBadRequest, "已存在同名发布库")
		return
	}
	desc := dto.Description
	if desc != nil {
		trimmed := strings.TrimSpace(*desc)
		desc = &trimmed
	}
	sets := `"Name" = ?, "Description" = ?, "VisibleOnWeb" = ?, "VisibleOnClient" = ?`
	args := []any{name, nptr(desc), a.db.BoolValue(dto.VisibleOnWeb), a.db.BoolValue(dto.VisibleOnClient)}
	if dto.SortOrder != 0 {
		sets += `, "SortOrder" = ?`
		args = append(args, dto.SortOrder)
	}
	args = append(args, id)
	if _, err := a.db.sql.Exec(a.db.Rebind(`UPDATE "PublishLibraries" SET `+sets+` WHERE "Id" = ?`), args...); err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeOK(w, map[string]string{"message": "已保存"})
}

// handleDeleteLibrary POST /api/admin/libraries/{id}/delete（密码二次确认 + 连带软删除链接）
func (a *App) handleDeleteLibrary(w http.ResponseWriter, r *http.Request) {
	id, ok := pathID(r, "id")
	if !ok {
		writeErr(w, http.StatusNotFound, "发布库不存在或已删除")
		return
	}
	claims := claimsFrom(r)
	if claims == nil {
		writeErr(w, http.StatusUnauthorized, "登录状态已失效，请重新登录")
		return
	}
	var req DeleteLibraryRequest
	_ = decodeJSON(r, &req, 1<<20)
	user, err := a.findUserByUsername(claims.UniqueName)
	if err != nil || user == nil {
		writeErr(w, http.StatusUnauthorized, "登录状态已失效，请重新登录")
		return
	}
	if strings.TrimSpace(req.Password) == "" || !VerifyPassword(req.Password, user.PasswordHash) {
		a.logf("删除发布库被拒绝：%s 密码校验未通过，库 Id=%d", claims.UniqueName, id)
		writeErr(w, http.StatusBadRequest, "密码错误，未执行删除")
		return
	}

	tx, err := a.db.sql.Begin()
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	defer tx.Rollback()

	var name string
	err = tx.QueryRow(a.db.Rebind(`SELECT "Name" FROM "PublishLibraries" WHERE "Id" = ? AND "IsDeleted" = `+a.db.False()), id).Scan(&name)
	if err == sql.ErrNoRows {
		writeErr(w, http.StatusNotFound, "发布库不存在或已删除")
		return
	}
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	var remaining int
	_ = tx.QueryRow(a.db.Rebind(`SELECT COUNT(*) FROM "PublishLibraries" WHERE "IsDeleted" = `+a.db.False()+` AND "Id" <> ?`), id).Scan(&remaining)
	if remaining == 0 {
		writeErr(w, http.StatusBadRequest, "至少保留一个发布库")
		return
	}

	res, err := tx.Exec(a.db.Rebind(`UPDATE "Links" SET "IsDeleted" = ? WHERE "LibraryId" = ? AND "IsDeleted" = `+a.db.False()), a.db.BoolValue(true), id)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	deletedLinks, _ := res.RowsAffected()
	if _, err := tx.Exec(a.db.Rebind(`UPDATE "PublishLibraries" SET "IsDeleted" = ? WHERE "Id" = ?`), a.db.BoolValue(true), id); err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	if err := tx.Commit(); err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	a.logf("发布库已删除：%s(Id=%d)，操作人 %s，连带软删除链接 %d 条", name, id, claims.UniqueName, deletedLinks)
	writeOK(w, map[string]any{"message": "已删除", "deletedLinks": deletedLinks})
}

// handleDeleteLibraryLegacy DELETE /api/admin/libraries/{id}（保留提示，不执行删除）
func (a *App) handleDeleteLibraryLegacy(w http.ResponseWriter, r *http.Request) {
	writeErr(w, http.StatusBadRequest, "删除发布库需密码验证，请刷新页面后重试")
}
