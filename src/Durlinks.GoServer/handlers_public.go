package main

import (
	"net/http"
	"sort"
	"strings"
)

// handlePublicLinks GET /api/links
func (a *App) handlePublicLinks(w http.ResponseWriter, r *http.Request) {
	q := parseLinkQuery(r)
	items, total, err := a.db.queryLinks(q, true)
	if err != nil {
		writeInternalErr(w, err)
		return
	}
	page := max(1, q.Page)
	// 与 queryLinks 的 publicMaxPageSize 保持一致，否则上报的每页条数与实际返回不符。
	size := clamp(q.PageSize, 1, publicMaxPageSize)
	totalPages := 0
	if size > 0 {
		totalPages = (total + size - 1) / size
	}
	writeOK(w, PagedResult{Items: items, Total: total, Page: page, PageSize: size, TotalPages: totalPages})
}

// publicFilterOptions 汇总可见链接的分组/标签/平台（API 与服务端渲染共用）。
func (a *App) publicFilterOptions(channel string) (FilterOptionsDto, error) {
	visCond := `l."IsHidden" = ` + a.db.False() + ` AND l."IsDeleted" = ` + a.db.False() +
		` AND lib."Id" IS NOT NULL AND lib."IsDeleted" = ` + a.db.False()
	if strings.EqualFold(channel, "client") {
		visCond += ` AND lib."VisibleOnClient" = ` + a.db.True()
	} else {
		visCond += ` AND lib."VisibleOnWeb" = ` + a.db.True()
	}

	groupSet := map[string]bool{}
	platformSet := map[string]bool{}
	rows, err := a.db.sql.Query(`SELECT l."GroupName", COALESCE(l."Platform",'') FROM "Links" l
		JOIN "PublishLibraries" lib ON lib."Id" = l."LibraryId" WHERE ` + visCond)
	if err != nil {
		return FilterOptionsDto{}, err
	}
	for rows.Next() {
		var g, p string
		if err := rows.Scan(&g, &p); err != nil {
			continue
		}
		for _, part := range strings.Split(g, ",") {
			if s := strings.TrimSpace(part); s != "" {
				groupSet[s] = true
			}
		}
		for _, part := range strings.Split(p, ",") {
			if s := strings.TrimSpace(part); s != "" {
				platformSet[s] = true
			}
		}
	}
	rows.Close()

	tagSet := map[string]bool{}
	tagRows, err := a.db.sql.Query(`SELECT DISTINCT tg."Name" FROM "LinkTags" lt
		JOIN "Tags" tg ON tg."Id" = lt."TagId"
		JOIN "Links" l ON l."Id" = lt."LinkId"
		JOIN "PublishLibraries" lib ON lib."Id" = l."LibraryId" WHERE ` + visCond)
	if err == nil {
		for tagRows.Next() {
			var n string
			if err := tagRows.Scan(&n); err == nil && strings.TrimSpace(n) != "" {
				tagSet[n] = true
			}
		}
		tagRows.Close()
	}

	return FilterOptionsDto{
		Groups:    sortedKeys(groupSet),
		Tags:      sortedKeys(tagSet),
		Platforms: sortedKeys(platformSet),
	}, nil
}

// handleFilters GET /api/filters?channel=web|client
func (a *App) handleFilters(w http.ResponseWriter, r *http.Request) {
	channel := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("channel")))
	out, err := a.publicFilterOptions(channel)
	if err != nil {
		writeInternalErr(w, err)
		return
	}
	writeOK(w, out)
}

func sortedKeys(m map[string]bool) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}
