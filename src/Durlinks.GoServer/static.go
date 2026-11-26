package main

import (
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
)

var contentTypes = map[string]string{
	".html":  "text/html; charset=utf-8",
	".htm":   "text/html; charset=utf-8",
	".js":    "application/javascript",
	".mjs":   "application/javascript",
	".css":   "text/css",
	".wasm":  "application/wasm",
	".json":  "application/json",
	".dat":   "application/octet-stream",
	".png":   "image/png",
	".jpg":   "image/jpeg",
	".jpeg":  "image/jpeg",
	".gif":   "image/gif",
	".svg":   "image/svg+xml",
	".ico":   "image/x-icon",
	".woff":  "font/woff",
	".woff2": "font/woff2",
	".ttf":   "font/ttf",
	".map":   "application/json",
	".txt":   "text/plain; charset=utf-8",
}

func contentTypeFor(name string) string {
	if ct, ok := contentTypes[strings.ToLower(filepath.Ext(name))]; ok {
		return ct
	}
	return "application/octet-stream"
}

// serveStatic 复刻原 Program.cs 的静态托管：按端口选站点、console 前缀、预压缩、缓存策略、SPA 回退。
func (a *App) serveStatic(w http.ResponseWriter, r *http.Request, root string, isPublic bool) bool {
	path := r.URL.Path
	if path == "" {
		path = "/"
	}

	// 管理端根路径与登录页：直接给纯静态登录页
	if !isPublic && (path == "/" || path == "/login.html") {
		login := filepath.Join(root, "login.html")
		if fileExists(login) {
			w.Header().Set("Content-Type", "text/html; charset=utf-8")
			w.Header().Set("Cache-Control", "no-cache")
			serveFileMaybeCompressed(w, r, login)
			return true
		}
	}

	rel := strings.TrimPrefix(path, "/")
	if !isPublic {
		if strings.HasPrefix(strings.ToLower(rel), "console/") {
			rel = rel[len("console/"):]
		} else if strings.EqualFold(rel, "console") {
			rel = ""
		}
	}
	if rel != "" && !strings.HasSuffix(rel, "/") {
		candidate := filepath.Join(root, filepath.FromSlash(rel))
		// 防目录穿越：用 Rel 比较，避免 "/a/b" 前缀误匹配 "/a/bxxx"
		if absRoot, err1 := filepath.Abs(root); err1 == nil {
			if absCand, err2 := filepath.Abs(candidate); err2 == nil {
				relPath, err3 := filepath.Rel(absRoot, absCand)
				if err3 != nil || filepath.IsAbs(relPath) || relPath == ".." ||
					strings.HasPrefix(relPath, ".."+string(filepath.Separator)) {
					return false
				}
			}
		}
		// 防符号链接逃逸：解析真实路径后再次确认仍在 root 内
		if absRoot, err1 := filepath.Abs(root); err1 == nil {
			if realRoot, err := filepath.EvalSymlinks(absRoot); err == nil {
				if realCand, err := filepath.EvalSymlinks(candidate); err == nil {
					relPath, err := filepath.Rel(realRoot, realCand)
					if err != nil || filepath.IsAbs(relPath) || relPath == ".." ||
						strings.HasPrefix(relPath, ".."+string(filepath.Separator)) {
						return false
					}
				} else if !os.IsNotExist(err) {
					// 符号链接损坏或指向不可达路径，一律拒绝
					return false
				}
			}
		}
		if fileExists(candidate) {
			w.Header().Set("Content-Type", contentTypeFor(candidate))
			w.Header().Set("Vary", "Accept-Encoding")
			if strings.HasPrefix(strings.ToLower(rel), "_framework/") {
				w.Header().Set("Cache-Control", "public, max-age=31536000, immutable")
			} else if strings.EqualFold(filepath.Ext(candidate), ".html") || strings.EqualFold(filepath.Ext(candidate), ".css") {
				w.Header().Set("Cache-Control", "no-cache")
			} else {
				w.Header().Set("Cache-Control", "public, max-age=86400")
			}
			serveFileMaybeCompressed(w, r, candidate)
			return true
		}
	}

	index := filepath.Join(root, "index.html")
	if fileExists(index) {
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		w.Header().Set("Cache-Control", "no-cache")
		serveFileMaybeCompressed(w, r, index)
		return true
	}
	return false
}

func serveFileMaybeCompressed(w http.ResponseWriter, r *http.Request, name string) {
	accept := r.Header.Get("Accept-Encoding")
	target := name
	if strings.Contains(accept, "br") && fileExists(name+".br") {
		target = name + ".br"
		w.Header().Set("Content-Encoding", "br")
	} else if strings.Contains(accept, "gzip") && fileExists(name+".gz") {
		target = name + ".gz"
		w.Header().Set("Content-Encoding", "gzip")
	}
	if target != name {
		w.Header().Set("Vary", "Accept-Encoding")
		f, err := os.Open(target)
		if err != nil {
			http.Error(w, "file error", http.StatusInternalServerError)
			return
		}
		defer f.Close()
		if st, err := f.Stat(); err == nil {
			w.Header().Set("Content-Length", itoa64(st.Size()))
		}
		_, _ = io.Copy(w, f)
		return
	}
	f, err := os.Open(name)
	if err != nil {
		http.Error(w, "file not found", http.StatusNotFound)
		return
	}
	defer f.Close()
	st, err := f.Stat()
	if err != nil {
		http.Error(w, "file error", http.StatusInternalServerError)
		return
	}
	http.ServeContent(w, r, name, st.ModTime(), f)
}

func fileExists(p string) bool {
	st, err := os.Stat(p)
	return err == nil && !st.IsDir()
}

func itoa64(n int64) string {
	if n == 0 {
		return "0"
	}
	neg := n < 0
	if neg {
		n = -n
	}
	var buf [24]byte
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
