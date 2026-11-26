package main

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strconv"
	"strings"
	"time"
)

func nowTime() time.Time { return time.Now().UTC() }

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	if v == nil {
		return
	}
	enc := json.NewEncoder(w)
	enc.SetEscapeHTML(false)
	_ = enc.Encode(v)
}

func writeErr(w http.ResponseWriter, status int, msg string) {
	writeJSON(w, status, APIError{Message: msg})
}

func writeOK(w http.ResponseWriter, v any) { writeJSON(w, http.StatusOK, v) }

// decodeJSON 限制请求体大小，避免恶意超大 JSON。
func decodeJSON(r *http.Request, dst any, maxBytes int64) error {
	if maxBytes <= 0 {
		maxBytes = 8 << 20
	}
	body, err := io.ReadAll(io.LimitReader(r.Body, maxBytes+1))
	if err != nil {
		return err
	}
	if int64(len(body)) > maxBytes {
		return errors.New("请求体过大")
	}
	if len(strings.TrimSpace(string(body))) == 0 {
		return nil
	}
	return json.Unmarshal(body, dst)
}

func queryInt(r *http.Request, key string, def int) int {
	if v := r.URL.Query().Get(key); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			return n
		}
	}
	return def
}

func queryIntPtr(r *http.Request, key string) *int {
	if v := r.URL.Query().Get(key); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			return &n
		}
	}
	return nil
}

// queryStrings 支持 groups=a&groups=b 与 groups=a,b 两种写法。
func queryStrings(r *http.Request, key string) []string {
	raw := r.URL.Query()[key]
	var out []string
	for _, item := range raw {
		for _, part := range strings.Split(item, ",") {
			if s := strings.TrimSpace(part); s != "" {
				out = append(out, s)
			}
		}
	}
	return out
}

func parseLinkQuery(r *http.Request) LinkQuery {
	q := LinkQuery{
		Q:        strings.TrimSpace(r.URL.Query().Get("q")),
		Group:    strings.TrimSpace(r.URL.Query().Get("group")),
		Tag:      strings.TrimSpace(r.URL.Query().Get("tag")),
		Platform: strings.TrimSpace(r.URL.Query().Get("platform")),
		Channel:  strings.ToLower(strings.TrimSpace(r.URL.Query().Get("channel"))),
		Page:     queryInt(r, "page", 1),
		PageSize: queryInt(r, "pageSize", 20),
	}
	if v := r.URL.Query().Get("importSource"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			src := ImportSource(n)
			q.ImportSource = &src
		}
	}
	if v := r.URL.Query().Get("libraryId"); v != "" {
		if n, err := strconv.Atoi(v); err == nil && n > 0 {
			q.LibraryID = &n
		}
	}
	q.Groups = queryStrings(r, "groups")
	q.Tags = queryStrings(r, "tags")
	q.Platforms = queryStrings(r, "platforms")
	for _, v := range queryStrings(r, "importSources") {
		if n, err := strconv.Atoi(v); err == nil {
			q.ImportSources = append(q.ImportSources, ImportSource(n))
		}
	}
	return q
}

func clamp(v, lo, hi int) int {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

// pathID 取 {id} 路径参数。
func pathID(r *http.Request, name string) (int, bool) {
	v := r.PathValue(name)
	n, err := strconv.Atoi(v)
	if err != nil || n <= 0 {
		return 0, false
	}
	return n, true
}

func strPtr(s string) *string { return &s }

func nz(s string, alt string) string {
	if strings.TrimSpace(s) == "" {
		return alt
	}
	return s
}

func nptr(v *string) any {
	if v == nil {
		return nil
	}
	return *v
}
