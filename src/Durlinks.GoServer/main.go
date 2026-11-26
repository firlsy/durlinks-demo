package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"
	"time"
)

type ctxKey int

const claimsKey ctxKey = 1

type App struct {
	db         *DB
	auth       *Auth
	captcha    *CaptchaStore
	loginGuard *loginLimiter
	cfg        *Config
	baseDir    string
	webMode    string
	userSite   string
	adminSite  string
	publicPort int
	adminPort  int
}

func (a *App) logf(format string, args ...any) { log.Printf(format, args...) }

func claimsFrom(r *http.Request) *JWTClaims {
	if v, ok := r.Context().Value(claimsKey).(*JWTClaims); ok {
		return v
	}
	return nil
}

func main() {
	var (
		configPath = flag.String("config", "", "配置文件路径（默认先找 appsettings.json，再找 config.json）")
		baseDir    = flag.String("base", "", "基准目录（静态站与数据库相对路径的根，默认可执行文件目录）")
	)
	flag.Parse()

	log.SetFlags(log.LstdFlags)
	logf := func(format string, args ...any) { log.Printf(format, args...) }

	base := *baseDir
	if base == "" {
		exe, err := os.Executable()
		if err == nil {
			base = filepath.Dir(exe)
		} else {
			base, _ = os.Getwd()
		}
	}
	absBase, err := filepath.Abs(base)
	if err == nil {
		base = absBase
	}

	cfgPath := *configPath
	if cfgPath == "" {
		for _, candidate := range []string{"appsettings.json", "config.json"} {
			p := ResolvePath(base, candidate)
			if fileExists(p) {
				cfgPath = p
				break
			}
		}
	}
	cfg, err := LoadConfig(cfgPath)
	if err != nil {
		log.Fatalf("配置加载失败: %v", err)
	}

	// JWT 密钥校验：生产必须显式配置 >=32 位、不含占位符；开发环境自动生成
	secret := strings.TrimSpace(cfg.Jwt.Secret)
	isDev := strings.EqualFold(os.Getenv("DURLINKS_ENV"), "development") ||
		strings.EqualFold(os.Getenv("ASPNETCORE_ENVIRONMENT"), "Development")
	if len(secret) < 32 || strings.Contains(strings.ToUpper(secret), "CHANGE_ME") || strings.HasPrefix(strings.ToLower(secret), "dev-") {
		if !isDev {
			log.Fatalf("生产环境必须通过 Jwt.Secret/DURLINKS_JWT_SECRET 配置至少 32 位的随机密钥（不得含 CHANGE_ME/dev- 等占位符）；本地调试可设 DURLINKS_ENV=development")
		}
		secret = randomSecret(48)
		logf("开发环境：已自动生成临时 JWT 密钥")
	}

	db, err := OpenDB(cfg, base)
	if err != nil {
		log.Fatalf("数据库打开失败: %v", err)
	}
	defer db.Close()
	if err := db.Init(logf); err != nil {
		log.Fatalf("数据库初始化失败: %v", err)
	}
	provider := cfg.Database.Provider
	target := cfg.Database.Path
	if provider == "postgres" {
		target = "PostgreSQL"
	}
	logf("数据库就绪：%s %s", provider, target)

	app := &App{
		db:         db,
		auth:       NewAuth(secret),
		captcha:    NewCaptchaStore(),
		loginGuard: newLoginLimiter(),
		cfg:        cfg,
		webMode:    cfg.Web.Mode,
		baseDir:    base,
		userSite:   ResolvePath(base, cfg.StaticFiles.UserDir),
		adminSite:  ResolvePath(base, cfg.StaticFiles.AdminDir),
		publicPort: cfg.Ports.Public,
		adminPort:  cfg.Ports.Admin,
	}

	listenHost := cfg.Kestrel.Host
	if strings.TrimSpace(listenHost) == "" {
		listenHost = "localhost"
	}
	bindHost := "127.0.0.1"
	if listenHost == "0.0.0.0" {
		bindHost = "0.0.0.0"
	}

	publicSrv := &http.Server{
		Addr:              fmt.Sprintf("%s:%d", bindHost, app.publicPort),
		Handler:           app.publicHandler(),
		ReadHeaderTimeout: 15 * time.Second,
		ReadTimeout:       60 * time.Second,  // 防 Slowloris：卡住读请求会被掐断
		WriteTimeout:      300 * time.Second, // 留足大文件导出/导入的时间
		IdleTimeout:       120 * time.Second, // 空闲连接回收
		MaxHeaderBytes:    1 << 20,
	}
	adminSrv := &http.Server{
		Addr:              fmt.Sprintf("%s:%d", bindHost, app.adminPort),
		Handler:           app.adminHandler(),
		ReadHeaderTimeout: 15 * time.Second,
		ReadTimeout:       120 * time.Second, // 管理端要传大 Excel
		WriteTimeout:      300 * time.Second,
		IdleTimeout:       120 * time.Second,
		MaxHeaderBytes:    1 << 20,
	}

	errCh := make(chan error, 2)
	go func() {
		logf("用户端 http://localhost:%d（只读）", app.publicPort)
		if err := publicSrv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			errCh <- fmt.Errorf("用户端启动失败(%s): %w", publicSrv.Addr, err)
		}
	}()
	go func() {
		logf("管理端 http://localhost:%d（全权限）", app.adminPort)
		if err := adminSrv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			errCh <- fmt.Errorf("管理端启动失败(%s): %w", adminSrv.Addr, err)
		}
	}()

	// 端口是否真的监听成功（快速失败，避免"启动即退出但无提示"）
	for _, p := range []int{app.publicPort, app.adminPort} {
		addr := net.JoinHostPort(bindHost, itoa(p))
		conn, err := net.DialTimeout("tcp", addr, 2*time.Second)
		if err != nil {
			logf("警告：端口 %s 未就绪：%v", addr, err)
		} else {
			conn.Close()
		}
	}

	stop := make(chan os.Signal, 1)
	signal.Notify(stop, os.Interrupt, syscall.SIGTERM)
	select {
	case err := <-errCh:
		log.Fatalf("服务异常: %v", err)
	case <-stop:
		logf("收到退出信号，正在关闭…")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 8*time.Second)
	defer cancel()
	_ = publicSrv.Shutdown(ctx)
	_ = adminSrv.Shutdown(ctx)
}

// ---------- 中间件 ----------

// portGuard 用户端端口只读：仅 GET，且 API 仅放行公开搜索接口。
func (a *App) portGuard(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		path := r.URL.Path
		isAPI := strings.HasPrefix(strings.ToLower(path), "/api")
		if isAPI {
			isPublicAPI := strings.HasPrefix(path, "/api/links") ||
				strings.HasPrefix(path, "/api/filters") ||
				strings.HasPrefix(path, "/api/libraries")
			if r.Method != http.MethodGet || !isPublicAPI {
				w.Header().Set("Content-Type", "text/plain; charset=utf-8")
				w.WriteHeader(http.StatusForbidden)
				_, _ = w.Write([]byte("只读端口：仅允许公开搜索的 GET 请求"))
				return
			}
		}
		next.ServeHTTP(w, r)
	})
}

// authMiddleware 管理端 JWT 校验（登录/验证码/静态资源放行）。
func (a *App) authMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		path := strings.ToLower(r.URL.Path)
		if !strings.HasPrefix(path, "/api") ||
			path == "/api/auth/login" ||
			path == "/api/auth/captcha" ||
			path == "/api/auth/captcha/image" ||
			path == "/api/auth/mfa/check" {
			next.ServeHTTP(w, r)
			return
		}
		header := r.Header.Get("Authorization")
		token := ""
		if strings.HasPrefix(strings.ToLower(header), "bearer ") {
			token = strings.TrimSpace(header[7:])
		}
		if token == "" {
			writeErr(w, http.StatusUnauthorized, "未登录")
			return
		}
		claims, err := a.auth.ParseToken(token)
		if err != nil {
			writeErr(w, http.StatusUnauthorized, "登录已过期，请重新登录")
			return
		}
		// 实时校验：用户仍存在、令牌版本未过期（改密码会 +1），并用库里的角色覆盖 claim，
		// 避免「账号被删/降权/改密后旧令牌继续可用」。
		u, err := a.findUserByUsername(claims.UniqueName)
		if err != nil {
			writeErr(w, http.StatusInternalServerError, err.Error())
			return
		}
		if u == nil || u.TokenVersion != claims.TokenVersion {
			writeErr(w, http.StatusUnauthorized, "登录状态已失效，请重新登录")
			return
		}
		claims.Role = u.Role.String()
		claims.NameID = itoa(u.ID)
		ctx := context.WithValue(r.Context(), claimsKey, claims)
		next.ServeHTTP(w, r.WithContext(ctx))
	})
}

// superAdminOnly 仅超管可用（用户管理）。
func superAdminOnly(h http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		claims := claimsFrom(r)
		if claims == nil || claims.Role != "SuperAdmin" {
			writeErr(w, http.StatusForbidden, "需要超级管理员权限")
			return
		}
		h(w, r)
	}
}

func (a *App) recoverMW(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		defer func() {
			if rec := recover(); rec != nil {
				log.Printf("panic: %v %s %s", rec, r.Method, r.URL.Path)
				writeErr(w, http.StatusInternalServerError, "服务器内部错误")
			}
		}()
		next.ServeHTTP(w, r)
	})
}

func (a *App) logMW(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		next.ServeHTTP(w, r)
		if d := time.Since(start); d > 1500*time.Millisecond {
			log.Printf("慢请求 %s %s %v", r.Method, r.URL.Path, d)
		}
	})
}

// ---------- 路由 ----------

func (a *App) apiRoutes(admin bool) *http.ServeMux {
	mux := http.NewServeMux()

	// 公共
	mux.HandleFunc("GET /api/links", a.handlePublicLinks)
	mux.HandleFunc("GET /api/filters", a.handleFilters)
	mux.HandleFunc("GET /api/libraries", a.handlePublicLibraries)
	mux.HandleFunc("GET /api/auth/captcha", a.handleCaptcha)
	mux.HandleFunc("GET /api/auth/captcha/image", a.handleCaptchaImage)
	mux.HandleFunc("GET /api/auth/mfa/check", a.handleMfaCheck)
	mux.HandleFunc("POST /api/auth/login", a.handleLogin)

	if !admin {
		return mux
	}

	// 账号
	mux.HandleFunc("GET /api/auth/me", a.handleMe)
	mux.HandleFunc("POST /api/auth/mfa/disable", a.handleMfaDisable)
	mux.HandleFunc("POST /api/auth/password", a.handleChangePassword)
	mux.HandleFunc("POST /api/auth/mfa/setup", a.handleMfaSetup)
	mux.HandleFunc("POST /api/auth/mfa/verify", a.handleMfaVerify)
	mux.HandleFunc("GET /api/auth/users", superAdminOnly(a.handleListUsers))
	mux.HandleFunc("POST /api/auth/users", superAdminOnly(a.handleCreateUser))

	// 看板
	mux.HandleFunc("GET /api/admin/stats", a.handleStats)

	// 链接
	mux.HandleFunc("GET /api/admin/links", a.handleAdminLinks)
	mux.HandleFunc("POST /api/admin/links", a.handleCreateLink)
	mux.HandleFunc("PUT /api/admin/links/{id}", a.handleUpdateLink)
	mux.HandleFunc("DELETE /api/admin/links/{id}", a.handleSoftDelete)
	mux.HandleFunc("PATCH /api/admin/links/{id}/hidden", a.handleSetHidden)
	mux.HandleFunc("PATCH /api/admin/links/batch/hidden", a.handleBatchHidden)
	mux.HandleFunc("DELETE /api/admin/links/batch", a.handleBatchDelete)
	mux.HandleFunc("POST /api/admin/links/move", a.handleMoveLinks)
	mux.HandleFunc("POST /api/admin/links/copy", a.handleCopyLinks)
	mux.HandleFunc("POST /api/admin/links/dedupe", a.handleDedupe)
	mux.HandleFunc("POST /api/admin/import/links", a.handleImportLinks)

	// 发布库
	mux.HandleFunc("GET /api/admin/libraries", a.handleAdminLibraries)
	mux.HandleFunc("POST /api/admin/libraries", a.handleCreateLibrary)
	mux.HandleFunc("PUT /api/admin/libraries/{id}", a.handleUpdateLibrary)
	mux.HandleFunc("POST /api/admin/libraries/{id}/delete", a.handleDeleteLibrary)
	mux.HandleFunc("DELETE /api/admin/libraries/{id}", a.handleDeleteLibraryLegacy)

	// 分类字典
	mux.HandleFunc("GET /api/admin/categories", a.handleCategories)
	mux.HandleFunc("POST /api/admin/categories/{kind}", a.handleCreateCategory)
	mux.HandleFunc("PUT /api/admin/categories/{kind}", a.handleRenameCategory)
	mux.HandleFunc("DELETE /api/admin/categories/{kind}", a.handleDeleteCategory)
	mux.HandleFunc("POST /api/admin/categories/{kind}/batch-delete", a.handleBatchDeleteCategories)

	// LLM 配置
	mux.HandleFunc("GET /api/admin/llm/configs", a.handleListLlmConfigs)
	mux.HandleFunc("POST /api/admin/llm/configs", a.handleSaveLlmConfig)
	mux.HandleFunc("DELETE /api/admin/llm/configs/{id}", a.handleDeleteLlmConfig)
	mux.HandleFunc("POST /api/admin/llm/configs/{id}/default", a.handleSetDefaultLlmConfig)
	mux.HandleFunc("POST /api/admin/llm/configs/{id}/model", a.handleSetLlmModel)
	mux.HandleFunc("GET /api/admin/llm/config", a.handleGetLlmConfig)
	mux.HandleFunc("PUT /api/admin/llm/config", a.handleLegacySetLlmConfig)
	mux.HandleFunc("POST /api/admin/llm/test", a.handleTestLlm)
	mux.HandleFunc("POST /api/admin/llm/models", a.handleListLlmModels)

	// 导入 / 导出（Excel）
	mux.HandleFunc("GET /api/admin/import/template", a.handleImportTemplate)
	mux.HandleFunc("POST /api/admin/import/excel", a.handleImportExcel)
	mux.HandleFunc("POST /api/admin/import/excel/jobs", a.handleStartExcelImport)
	mux.HandleFunc("GET /api/admin/import/excel/jobs/status", a.handleExcelImportStatus)
	mux.HandleFunc("POST /api/admin/import/excel/jobs/cancel", a.handleCancelExcelImport)
	mux.HandleFunc("GET /api/admin/ai/enrich/export", a.handleExportEnriched)

	// AI 补料
	mux.HandleFunc("POST /api/admin/ai/enrich/{id}", a.handleEnrich)
	mux.HandleFunc("POST /api/admin/ai/enrich/batch", a.handleStartBatchEnrich)
	mux.HandleFunc("GET /api/admin/ai/enrich/batch/status", a.handleBatchEnrichStatus)
	mux.HandleFunc("POST /api/admin/ai/enrich/batch/cancel", a.handleCancelBatchEnrich)

	// 导出数据包（zip）
	mux.HandleFunc("POST /api/admin/export/package", a.handleExportPackage)

	// 其余未实现接口统一返回 501 + 明确 JSON，避免前端拿到 HTML
	mux.HandleFunc("/api/admin/", a.notImplemented)

	return mux
}

func (a *App) notImplemented(w http.ResponseWriter, r *http.Request) {
	writeErr(w, http.StatusNotImplemented, "该接口在 Go 版服务端尚未实现："+r.Method+" "+r.URL.Path)
}

// staticHandler 非 /api 请求走静态站。
func (a *App) staticHandler(root string, isPublic bool) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if strings.HasPrefix(strings.ToLower(r.URL.Path), "/api") {
			http.NotFound(w, r)
			return
		}
		if !a.serveStatic(w, r, root, isPublic) {
			http.NotFound(w, r)
		}
	})
}

func (a *App) publicHandler() http.Handler {
	mux := a.apiRoutes(false)
	handler := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		path := r.URL.Path
		if strings.HasPrefix(strings.ToLower(path), "/api") {
			mux.ServeHTTP(w, r)
			return
		}
		// go 模式：用户端由 Go 直接渲染，不依赖任何前端产物
		if a.webMode == "go" {
			switch {
			case path == "/" || path == "/index.html":
				a.handleWebIndex(w, r)
				return
			case strings.HasPrefix(path, "/assets/"):
				a.handleWebAssets(w, r)
				return
			}
		}
		a.staticHandler(a.userSite, true).ServeHTTP(w, r)
	})
	return a.recoverMW(a.logMW(a.portGuard(handler)))
}

func (a *App) adminHandler() http.Handler {
	mux := a.apiRoutes(true)
	handler := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if strings.HasPrefix(strings.ToLower(r.URL.Path), "/api") {
			mux.ServeHTTP(w, r)
			return
		}
		a.staticHandler(a.adminSite, false).ServeHTTP(w, r)
	})
	return a.recoverMW(a.logMW(a.authMiddleware(handler)))
}

func randomSecret(n int) string {
	return randomToken() + randomToken()[:max(0, n-32)]
}
