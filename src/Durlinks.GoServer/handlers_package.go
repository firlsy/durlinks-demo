package main

import (
	"archive/zip"
	"database/sql"
	"encoding/json"
	"fmt"
	"net/http"
	"strings"
	"time"
)

// handleExportPackage POST /api/admin/export/package
// 需当前账号密码二次确认；导出 zip：链接 xlsx + 发布库 + 统计 + 说明。
// 说明里刻意不包含数据库口令、JWT 密钥、LLM ApiKey、用户密码。
func (a *App) handleExportPackage(w http.ResponseWriter, r *http.Request) {
	var req ExportPackageRequest
	if err := decodeJSON(r, &req, 1<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	claims := claimsFrom(r)
	if claims == nil {
		writeErr(w, http.StatusUnauthorized, "登录状态已失效，请重新登录")
		return
	}
	user, err := a.findUserByUsername(claims.UniqueName)
	if err != nil || user == nil {
		writeErr(w, http.StatusUnauthorized, "登录状态已失效，请重新登录")
		return
	}
	if strings.TrimSpace(req.Password) == "" || !VerifyPassword(req.Password, user.PasswordHash) {
		a.logf("导出数据包被拒绝：%s 密码校验未通过", claims.UniqueName)
		writeErr(w, http.StatusBadRequest, "密码错误，未执行导出")
		return
	}

	items, err := a.collectAllLinks(200000, nil)
	if err != nil {
		writeInternalErr(w, err)
		return
	}
	xlsxBlob, err := a.buildLinksXLSX(items)
	if err != nil {
		writeInternalErr(w, err)
		return
	}

	libs, err := a.queryLibrariesForExport()
	if err != nil {
		writeInternalErr(w, err)
		return
	}
	stats, err := a.queryStatsMap()
	if err != nil {
		writeInternalErr(w, err)
		return
	}

	libJSON, _ := json.MarshalIndent(libs, "", "  ")
	statJSON, _ := json.MarshalIndent(stats, "", "  ")

	now := time.Now()
	readme := fmt.Sprintf(`durlinks-demo 数据导出包
=====================================
导出时间：%s
导出账号：%s
链接条数：%d
发布库数：%d

文件说明
  links.xlsx      全部未删除链接，14 列结构与本系统导入模板一致，可原样再导入
  libraries.json  发布库清单（名称/描述/渠道可见性/排序/链接数）
  stats.json      看板统计快照
  README.txt      本说明

安全说明
  本包不包含数据库口令、JWT 密钥、LLM API Key 或任何用户的密码哈希。
  如需完整数据库备份，请在服务器上直接备份 SQLite 文件（data/durlinks.db）或 PostgreSQL 库。
`, now.Format("2006-01-02 15:04:05"), claims.UniqueName, len(items), len(libs))

	w.Header().Set("Content-Type", "application/zip")
	w.Header().Set("Content-Disposition", `attachment; filename="durlinks-demo-export.zip"`)
	w.Header().Set("Cache-Control", "no-store")

	zw := zip.NewWriter(w)
	add := func(name string, content []byte) error {
		f, err := zw.Create(name)
		if err != nil {
			return err
		}
		_, err = f.Write(content)
		return err
	}
	for _, pair := range []struct {
		name string
		data []byte
	}{
		{"links.xlsx", xlsxBlob},
		{"libraries.json", libJSON},
		{"stats.json", statJSON},
		{"README.txt", []byte(readme)},
	} {
		if err := add(pair.name, pair.data); err != nil {
			a.logf("导出数据包写入失败：%v", err)
			break
		}
	}
	if err := zw.Close(); err != nil {
		a.logf("导出数据包收尾失败：%v", err)
	}
	a.logf("已导出数据包：%s，链接 %d 条", claims.UniqueName, len(items))
}

// queryLibrariesForExport 导出用的库清单（含链接数）。
func (a *App) queryLibrariesForExport() ([]LibraryDto, error) {
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
		return nil, err
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
			return nil, err
		}
		out = append(out, LibraryDto{
			ID: id, Name: name, Description: nullableString(desc),
			VisibleOnWeb: scanBool(onWeb), VisibleOnClient: scanBool(onClient),
			SortOrder: sortOrder, LinkCount: counts[id], CreatedAt: isoTime(scanTime(createdAt)),
		})
	}
	return out, nil
}

// queryStatsMap 与 /api/admin/stats 相同的统计口径。
func (a *App) queryStatsMap() (map[string]int, error) {
	out := map[string]int{}
	queries := map[string]string{
		"total":    `SELECT COUNT(*) FROM "Links" WHERE "IsDeleted" = ` + a.db.False(),
		"hidden":   `SELECT COUNT(*) FROM "Links" WHERE "IsDeleted" = ` + a.db.False() + ` AND "IsHidden" = ` + a.db.True(),
		"aiCount":  `SELECT COUNT(*) FROM "Links" WHERE "IsDeleted" = ` + a.db.False() + ` AND "ImportSource" = 1`,
		"groups":   `SELECT COUNT(DISTINCT "GroupName") FROM "Links" WHERE "IsDeleted" = ` + a.db.False(),
		"tags":     `SELECT COUNT(*) FROM "Tags"`,
		"netdisk":  `SELECT COUNT(*) FROM "Links" WHERE "IsDeleted" = ` + a.db.False() + ` AND "LinkType" = '网盘'`,
		"download": `SELECT COUNT(*) FROM "Links" WHERE "IsDeleted" = ` + a.db.False() + ` AND "LinkType" = '下载直链'`,
	}
	for k, q := range queries {
		var n int
		if err := a.db.sql.QueryRow(q).Scan(&n); err != nil {
			return nil, err
		}
		out[k] = n
	}
	return out, nil
}
