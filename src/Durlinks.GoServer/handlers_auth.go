package main

import (
	"database/sql"
	"encoding/base64"
	"fmt"
	"net"
	"net/http"
	"strings"
	"time"
)

// clientIP 取连接对端地址；用 RemoteAddr 而非 X-Forwarded-For，
// 否则伪造请求头就能绕过登录限流。
func clientIP(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}

const captchaCookie = "durlinks_captcha"

func (a *App) handleCaptcha(w http.ResponseWriter, r *http.Request) {
	id, svg := a.captcha.Generate("")
	w.Header().Set("Cache-Control", "no-store, no-cache, must-revalidate")
	w.Header().Set("Pragma", "no-cache")
	writeOK(w, CaptchaDto{
		ID:       id,
		Svg:      svg,
		ImageURL: "data:image/svg+xml;base64," + base64.StdEncoding.EncodeToString([]byte(svg)),
	})
}

func (a *App) handleCaptchaImage(w http.ResponseWriter, r *http.Request) {
	ticket := ""
	if ck, err := r.Cookie(captchaCookie); err == nil {
		ticket = ck.Value
	}
	if strings.TrimSpace(ticket) == "" {
		ticket = randomToken()
	}
	id, svg := a.captcha.Generate(ticket)
	http.SetCookie(w, &http.Cookie{
		Name:     captchaCookie,
		Value:    id,
		Path:     "/",
		HttpOnly: true,
		Secure:   a.cookieSecure(r),
		SameSite: http.SameSiteStrictMode,
		MaxAge:   300,
	})
	w.Header().Set("Cache-Control", "no-store, no-cache, must-revalidate")
	w.Header().Set("Content-Type", "image/svg+xml; charset=utf-8")
	_, _ = w.Write([]byte(svg))
}

func (a *App) handleMfaCheck(w http.ResponseWriter, r *http.Request) {
	// 匿名可访问，按 IP 限频，防止遍历用户名枚举 MFA 开启状态
	if !a.mfaGuard.allow(clientIP(r)) {
		writeErr(w, http.StatusTooManyRequests, "查询过于频繁，请稍后再试")
		return
	}
	username := strings.TrimSpace(r.URL.Query().Get("username"))
	var n int
	err := a.db.sql.QueryRow(a.db.Rebind(`SELECT COUNT(*) FROM "Users" WHERE "Username" = ? AND "MfaEnabled" = `+a.db.True()), username).Scan(&n)
	if err != nil {
		writeInternalErr(w, err)
		return
	}
	writeOK(w, MfaCheckDto{MfaEnabled: n > 0})
}

func (a *App) handleLogin(w http.ResponseWriter, r *http.Request) {
	var req LoginRequest
	if err := decodeJSON(r, &req, 1<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}

	// 失败计数键：反代部署下 RemoteAddr 是同一个上游地址，等价于按账号限流
	loginKey := clientIP(r) + "|" + strings.ToLower(strings.TrimSpace(req.Username))
	// 原子占用一次尝试配额，避免并发请求同时通过检查后再计数（TOCTOU 绕过）
	if blocked, left := a.loginGuard.reserve(loginKey); blocked {
		minutes := int(left.Minutes()) + 1
		writeJSON(w, http.StatusTooManyRequests, APIError{Message: fmt.Sprintf("登录失败次数过多，请 %d 分钟后再试", minutes)})
		return
	}

	captchaID := ""
	if req.CaptchaID != nil && strings.TrimSpace(*req.CaptchaID) != "" {
		captchaID = *req.CaptchaID
	} else if ck, err := r.Cookie(captchaCookie); err == nil {
		captchaID = ck.Value
	}
	code := ""
	if req.CaptchaCode != nil {
		code = *req.CaptchaCode
	}
	if !a.captcha.Verify(captchaID, code) {
		writeErr(w, http.StatusBadRequest, "验证码错误或已过期")
		return
	}

	user, err := a.findUserByUsername(strings.TrimSpace(req.Username))
	if err != nil {
		writeInternalErr(w, err)
		return
	}
	if user == nil || !VerifyPassword(req.Password, user.PasswordHash) {
		// 计数已在 reserve() 原子完成，这里不再重复 fail
		writeErr(w, http.StatusUnauthorized, "用户名或密码错误")
		return
	}

	if user.MfaEnabled {
		secret := ""
		if user.MfaSecret != nil {
			secret = *user.MfaSecret
		}
		totp := ""
		if req.TotpCode != nil {
			totp = *req.TotpCode
		}
		if totp == "" || !VerifyTotp(secret, totp) {
			writeJSON(w, http.StatusUnauthorized, map[string]any{"message": "MFA 动态码错误", "mfaRequired": true})
			return
		}
	}

	a.loginGuard.success(loginKey)
	tokenStr := a.auth.IssueToken(user)
	// 页面导航（/console）带不了 Authorization 头，这里额外下发 HttpOnly cookie，
	// API 请求仍走 Bearer token，两套凭证共享同一个 TokenVersion。
	http.SetCookie(w, &http.Cookie{
		Name: authCookieName, Value: tokenStr, Path: "/", HttpOnly: true,
		SameSite: http.SameSiteStrictMode, MaxAge: 12 * 3600, Secure: a.cookieSecure(r),
	})
	writeOK(w, LoginResponse{
		Token:       tokenStr,
		Username:    user.Username,
		Role:        user.Role.String(),
		MfaEnabled:  user.MfaEnabled,
		MfaRequired: user.MfaEnabled,
	})
}

func (a *App) handleMe(w http.ResponseWriter, r *http.Request) {
	claims := claimsFrom(r)
	if claims == nil {
		writeErr(w, http.StatusUnauthorized, "未登录")
		return
	}
	user, err := a.findUserByUsername(claims.UniqueName)
	if err != nil {
		writeInternalErr(w, err)
		return
	}
	mfa := false
	if user != nil {
		mfa = user.MfaEnabled
	}
	writeOK(w, map[string]any{
		"username":   claims.UniqueName,
		"role":       claims.Role,
		"mfaEnabled": mfa,
	})
}

func (a *App) handleMfaDisable(w http.ResponseWriter, r *http.Request) {
	claims := claimsFrom(r)
	if claims == nil {
		writeErr(w, http.StatusUnauthorized, "未登录")
		return
	}
	var dto MfaDisableRequest
	if err := decodeJSON(r, &dto, 1<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	user, err := a.findUserByUsername(claims.UniqueName)
	if err != nil || user == nil {
		writeErr(w, http.StatusUnauthorized, "未登录")
		return
	}
	if !user.MfaEnabled {
		writeErr(w, http.StatusBadRequest, "MFA 未开启")
		return
	}
	codeOK := dto.Code != nil && user.MfaSecret != nil && VerifyTotp(*user.MfaSecret, *dto.Code)
	pwdOK := dto.Password != nil && VerifyPassword(*dto.Password, user.PasswordHash)
	if !codeOK && !pwdOK {
		writeErr(w, http.StatusBadRequest, "动态码或密码错误")
		return
	}
	// MFA 状态变更后 TokenVersion+1：开启前签发的旧 token 立即失效，避免 MFA 形同虚设
	_, err = a.db.sql.Exec(a.db.Rebind(`UPDATE "Users" SET "MfaEnabled" = ?, "MfaSecret" = NULL, "TokenVersion" = "TokenVersion" + 1 WHERE "Id" = ?`), a.db.BoolValue(false), user.ID)
	if err != nil {
		writeInternalErr(w, err)
		return
	}
	writeOK(w, map[string]string{"message": "MFA 已关闭"})
}

func (a *App) handleChangePassword(w http.ResponseWriter, r *http.Request) {
	claims := claimsFrom(r)
	if claims == nil {
		writeErr(w, http.StatusUnauthorized, "未登录")
		return
	}
	var dto ChangePasswordRequest
	if err := decodeJSON(r, &dto, 1<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	if len(strings.TrimSpace(dto.NewPassword)) < 8 {
		writeErr(w, http.StatusBadRequest, "新密码至少 8 位")
		return
	}
	user, err := a.findUserByUsername(claims.UniqueName)
	if err != nil || user == nil {
		writeErr(w, http.StatusUnauthorized, "未登录")
		return
	}
	if !VerifyPassword(dto.CurrentPassword, user.PasswordHash) {
		writeErr(w, http.StatusBadRequest, "当前密码错误")
		return
	}
	// 密码变更后 TokenVersion+1：所有已签发的旧 JWT 立即失效
	res, err := a.db.sql.Exec(a.db.Rebind(`UPDATE "Users" SET "PasswordHash" = ?, "TokenVersion" = "TokenVersion" + 1 WHERE "Id" = ?`), HashPassword(dto.NewPassword), user.ID)
	if err != nil {
		writeInternalErr(w, err)
		return
	}
	if n, _ := res.RowsAffected(); n == 0 {
		writeErr(w, http.StatusInternalServerError, "密码更新失败")
		return
	}
	writeOK(w, map[string]string{"message": "密码已修改，请重新登录"})
}

func (a *App) handleMfaSetup(w http.ResponseWriter, r *http.Request) {
	claims := claimsFrom(r)
	if claims == nil {
		writeErr(w, http.StatusUnauthorized, "未登录")
		return
	}
	user, err := a.findUserByUsername(claims.UniqueName)
	if err != nil || user == nil {
		writeErr(w, http.StatusUnauthorized, "未登录")
		return
	}
	if user.MfaEnabled {
		writeErr(w, http.StatusBadRequest, "MFA 已开启，如需重新绑定请先关闭 MFA")
		return
	}
	secret := GenerateMfaSecret()
	if _, err := a.db.sql.Exec(a.db.Rebind(`UPDATE "Users" SET "MfaSecret" = ? WHERE "Id" = ?`), secret, user.ID); err != nil {
		writeInternalErr(w, err)
		return
	}
	writeOK(w, MfaSetupDto{Secret: secret, QrURI: BuildOtpAuthURI(user.Username, secret)})
}

func (a *App) handleMfaVerify(w http.ResponseWriter, r *http.Request) {
	claims := claimsFrom(r)
	if claims == nil {
		writeErr(w, http.StatusUnauthorized, "未登录")
		return
	}
	var dto struct {
		Code string `json:"code"`
	}
	if err := decodeJSON(r, &dto, 1<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	user, err := a.findUserByUsername(claims.UniqueName)
	if err != nil || user == nil || user.MfaSecret == nil {
		writeErr(w, http.StatusBadRequest, "请先获取密钥")
		return
	}
	if !VerifyTotp(*user.MfaSecret, dto.Code) {
		writeErr(w, http.StatusBadRequest, "动态码错误")
		return
	}
	// MFA 开启后 TokenVersion+1：开启前签发的旧 token 立即失效
	if _, err := a.db.sql.Exec(a.db.Rebind(`UPDATE "Users" SET "MfaEnabled" = ?, "TokenVersion" = "TokenVersion" + 1 WHERE "Id" = ?`), a.db.BoolValue(true), user.ID); err != nil {
		writeInternalErr(w, err)
		return
	}
	writeOK(w, map[string]string{"message": "MFA 已开启"})
}

func (a *App) handleListUsers(w http.ResponseWriter, r *http.Request) {
	rows, err := a.db.sql.Query(`SELECT "Id","Username","Role","MfaEnabled","CreatedAt" FROM "Users" ORDER BY "Id"`)
	if err != nil {
		writeInternalErr(w, err)
		return
	}
	defer rows.Close()
	out := []UserDto{}
	for rows.Next() {
		var (
			id, role  int
			username  string
			mfa       any
			createdAt any
		)
		if err := rows.Scan(&id, &username, &role, &mfa, &createdAt); err != nil {
			writeInternalErr(w, err)
			return
		}
		out = append(out, UserDto{
			ID: id, Username: username, Role: UserRole(role).String(),
			MfaEnabled: scanBool(mfa), CreatedAt: isoTime(scanTime(createdAt)),
		})
	}
	writeOK(w, out)
}

func (a *App) handleCreateUser(w http.ResponseWriter, r *http.Request) {
	var req CreateUserRequest
	if err := decodeJSON(r, &req, 1<<20); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	if strings.TrimSpace(req.Username) == "" || strings.TrimSpace(req.Password) == "" {
		writeErr(w, http.StatusBadRequest, "用户名与密码不能为空")
		return
	}
	var exists int
	_ = a.db.sql.QueryRow(a.db.Rebind(`SELECT COUNT(*) FROM "Users" WHERE "Username" = ?`), req.Username).Scan(&exists)
	if exists > 0 {
		writeErr(w, http.StatusConflict, "用户名已存在")
		return
	}
	now := a.db.now()
	var id int
	if a.db.pg {
		err := a.db.sql.QueryRow(a.db.Rebind(`INSERT INTO "Users" ("Username","PasswordHash","Role","MfaSecret","MfaEnabled","CreatedAt") VALUES (?,?,?,?,?,?) RETURNING "Id"`),
			strings.TrimSpace(req.Username), HashPassword(req.Password), int(RoleAdmin), nil, a.db.BoolValue(false), now).Scan(&id)
		if err != nil {
			writeInternalErr(w, err)
			return
		}
	} else {
		res, err := a.db.sql.Exec(a.db.Rebind(`INSERT INTO "Users" ("Username","PasswordHash","Role","MfaSecret","MfaEnabled","CreatedAt") VALUES (?,?,?,?,?,?)`),
			strings.TrimSpace(req.Username), HashPassword(req.Password), int(RoleAdmin), nil, a.db.BoolValue(false), now)
		if err != nil {
			writeInternalErr(w, err)
			return
		}
		last, _ := res.LastInsertId()
		id = int(last)
	}
	writeOK(w, UserDto{ID: id, Username: req.Username, Role: RoleAdmin.String(), MfaEnabled: false, CreatedAt: isoTime(time.Now())})
}

func (a *App) findUserByUsername(username string) (*User, error) {
	row := a.db.sql.QueryRow(a.db.Rebind(`SELECT "Id","Username","PasswordHash","Role","MfaSecret","MfaEnabled","TokenVersion","CreatedAt" FROM "Users" WHERE "Username" = ?`), username)
	var (
		u         User
		role      int
		mfaSecret sql.NullString
		mfa       any
		createdAt any
	)
	if err := row.Scan(&u.ID, &u.Username, &u.PasswordHash, &role, &mfaSecret, &mfa, &u.TokenVersion, &createdAt); err != nil {
		if err == sql.ErrNoRows {
			return nil, nil
		}
		return nil, err
	}
	u.Role = UserRole(role)
	u.MfaSecret = nullableString(mfaSecret)
	u.MfaEnabled = scanBool(mfa)
	u.CreatedAt = scanTime(createdAt)
	return &u, nil
}
