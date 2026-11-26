package main

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha1"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base32"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"math/big"
	"strconv"
	"strings"
	"sync"
	"time"
)

// ======================= 密码：PBKDF2-SHA256（与 C# PasswordHelper 完全兼容） =======================

const pbkdf2Iterations = 100_000
const pbkdf2KeyLen = 32

func pbkdf2SHA256(password, salt []byte, iter, keyLen int) []byte {
	prf := hmac.New(sha256.New, password)
	hashLen := prf.Size()
	numBlocks := (keyLen + hashLen - 1) / hashLen
	var buf [4]byte
	dk := make([]byte, 0, numBlocks*hashLen)
	u := make([]byte, hashLen)
	for block := 1; block <= numBlocks; block++ {
		prf.Reset()
		prf.Write(salt)
		buf[0] = byte(block >> 24)
		buf[1] = byte(block >> 16)
		buf[2] = byte(block >> 8)
		buf[3] = byte(block)
		prf.Write(buf[:4])
		dk = prf.Sum(dk)
		t := dk[len(dk)-hashLen:]
		copy(u, t)
		for n := 2; n <= iter; n++ {
			prf.Reset()
			prf.Write(u)
			u = prf.Sum(u[:0])
			for x := range u {
				t[x] ^= u[x]
			}
		}
	}
	return dk[:keyLen]
}

// HashPassword 输出 "base64(salt):base64(hash)"，与 C# 一致。
func HashPassword(password string) string {
	salt := make([]byte, 16)
	_, _ = rand.Read(salt)
	hash := pbkdf2SHA256([]byte(password), salt, pbkdf2Iterations, pbkdf2KeyLen)
	return base64.StdEncoding.EncodeToString(salt) + ":" + base64.StdEncoding.EncodeToString(hash)
}

func VerifyPassword(password, stored string) bool {
	parts := strings.Split(stored, ":")
	if len(parts) != 2 {
		return false
	}
	salt, err1 := base64.StdEncoding.DecodeString(parts[0])
	expected, err2 := base64.StdEncoding.DecodeString(parts[1])
	if err1 != nil || err2 != nil {
		return false
	}
	actual := pbkdf2SHA256([]byte(password), salt, pbkdf2Iterations, pbkdf2KeyLen)
	return subtle.ConstantTimeCompare(expected, actual) == 1
}

// ======================= JWT (HS256) =======================

type JWTClaims struct {
	Issuer       string `json:"iss"`
	Audience     string `json:"aud"`
	ExpiresAt    int64  `json:"exp"`
	IssuedAt     int64  `json:"iat"`
	NameID       string `json:"nameid"`
	UniqueName   string `json:"unique_name"`
	Role         string `json:"role"`
	TokenVersion int    `json:"tv"`
}

type Auth struct {
	secret []byte
}

func NewAuth(secret string) *Auth { return &Auth{secret: []byte(secret)} }

func b64url(b []byte) string { return base64.RawURLEncoding.EncodeToString(b) }

func (a *Auth) IssueToken(u *User) string {
	now := time.Now().UTC()
	claims := JWTClaims{
		Issuer:       "durlinks",
		Audience:     "durlinks-admin",
		ExpiresAt:    now.Add(12 * time.Hour).Unix(),
		IssuedAt:     now.Unix(),
		NameID:       strconv.Itoa(u.ID),
		UniqueName:   u.Username,
		Role:         u.Role.String(),
		TokenVersion: u.TokenVersion,
	}
	header := b64url([]byte(`{"alg":"HS256","typ":"JWT"}`))
	payload, _ := json.Marshal(claims)
	body := header + "." + b64url(payload)
	mac := hmac.New(sha256.New, a.secret)
	mac.Write([]byte(body))
	return body + "." + b64url(mac.Sum(nil))
}

var errInvalidToken = errors.New("令牌无效或已过期")

func (a *Auth) ParseToken(token string) (*JWTClaims, error) {
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		return nil, errInvalidToken
	}
	mac := hmac.New(sha256.New, a.secret)
	mac.Write([]byte(parts[0] + "." + parts[1]))
	expected := mac.Sum(nil)
	got, err := base64.RawURLEncoding.DecodeString(parts[2])
	if err != nil || subtle.ConstantTimeCompare(expected, got) != 1 {
		return nil, errInvalidToken
	}
	payload, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		return nil, errInvalidToken
	}
	var claims JWTClaims
	if err := json.Unmarshal(payload, &claims); err != nil {
		return nil, errInvalidToken
	}
	if claims.Issuer != "durlinks" || claims.Audience != "durlinks-admin" {
		return nil, errInvalidToken
	}
	if claims.ExpiresAt == 0 || time.Now().Unix() >= claims.ExpiresAt {
		return nil, errInvalidToken
	}
	return &claims, nil
}

// ======================= TOTP (RFC 6238, SHA1/6 位/30 秒) =======================

func base32Encode(data []byte) string {
	return strings.TrimRight(base32.StdEncoding.WithPadding(base32.NoPadding).EncodeToString(data), "=")
}

func base32Decode(s string) ([]byte, error) {
	s = strings.ToUpper(strings.TrimRight(strings.TrimSpace(s), "="))
	return base32.StdEncoding.WithPadding(base32.NoPadding).DecodeString(s)
}

func GenerateMfaSecret() string {
	b := make([]byte, 20)
	_, _ = rand.Read(b)
	return base32Encode(b)
}

func BuildOtpAuthURI(username, secret string) string {
	label := urlEscape("Durlinks:" + username)
	return fmt.Sprintf("otpauth://totp/%s?secret=%s&issuer=Durlinks&algorithm=SHA1&digits=6&period=30", label, secret)
}

func urlEscape(s string) string {
	// 与 Uri.EscapeDataString 对齐：保留 -_.!~*'() 之外的字符都转义
	var b strings.Builder
	for _, c := range []byte(s) {
		if (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
			c == '-' || c == '_' || c == '.' || c == '!' || c == '~' || c == '*' || c == '\'' || c == '(' || c == ')' {
			b.WriteByte(c)
		} else {
			fmt.Fprintf(&b, "%%%02X", c)
		}
	}
	return b.String()
}

func computeTOTP(secret string, counter int64) string {
	key, err := base32Decode(secret)
	if err != nil || len(key) == 0 {
		return ""
	}
	var buf [8]byte
	for i := 7; i >= 0; i-- {
		buf[i] = byte(counter & 0xFF)
		counter >>= 8
	}
	mac := hmac.New(sha1.New, key)
	mac.Write(buf[:])
	sum := mac.Sum(nil)
	offset := sum[len(sum)-1] & 0x0F
	bin := (int(sum[offset])&0x7F)<<24 | int(sum[offset+1])<<16 | int(sum[offset+2])<<8 | int(sum[offset+3])
	return fmt.Sprintf("%06d", bin%1000000)
}

// VerifyTotp 允许前后 30 秒窗口。
func VerifyTotp(secret, code string) bool {
	secret = strings.TrimSpace(secret)
	code = strings.TrimSpace(code)
	if secret == "" || code == "" {
		return false
	}
	counter := time.Now().Unix() / 30
	for delta := int64(-1); delta <= 1; delta++ {
		expected := computeTOTP(secret, counter+delta)
		if expected != "" && subtle.ConstantTimeCompare([]byte(expected), []byte(code)) == 1 {
			return true
		}
	}
	return false
}

// ======================= 图形验证码（SVG，内存存储，一次性消费） =======================

type captchaEntry struct {
	code      string
	createdAt int64
}

type CaptchaStore struct {
	mu      sync.Mutex
	store   map[string]captchaEntry
	lastGC  int64
	chars   []byte
	ttl     int64
	maxSize int
}

func NewCaptchaStore() *CaptchaStore {
	return &CaptchaStore{
		store:   map[string]captchaEntry{},
		chars:   []byte("ABCDEFGHJKLMNPQRSTUVWXYZ23456789"),
		ttl:     300,
		maxSize: 5000,
	}
}

func randomInt(max int) int {
	if max <= 0 {
		return 0
	}
	n, err := rand.Int(rand.Reader, big.NewInt(int64(max)))
	if err != nil {
		return 0
	}
	return int(n.Int64())
}

func randomToken() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return fmt.Sprintf("%x", b)
}

// Generate 生成验证码，ticket 为空时自动生成。返回 (id, svg)。
func (c *CaptchaStore) Generate(ticket string) (string, string) {
	c.cleanupIfNeeded()
	id := ticket
	if strings.TrimSpace(id) == "" {
		id = randomToken()
	}
	code := make([]byte, 4)
	for i := range code {
		code[i] = c.chars[randomInt(len(c.chars))]
	}
	c.mu.Lock()
	c.store[id] = captchaEntry{code: string(code), createdAt: time.Now().Unix()}
	c.mu.Unlock()

	var sb strings.Builder
	sb.WriteString("<svg xmlns='http://www.w3.org/2000/svg' width='120' height='38' viewBox='0 0 120 38'>\n")
	sb.WriteString("<rect width='120' height='38' fill='#eef2ff'/>\n")
	for i := 0; i < 4; i++ {
		fmt.Fprintf(&sb, "<line x1='%d' y1='%d' x2='%d' y2='%d' stroke='#c7d2fe' stroke-width='1'/>\n",
			randomInt(120), randomInt(38), randomInt(120), randomInt(38))
	}
	colors := []string{"#1e3a8a", "#3730a3", "#0f766e", "#9a3412"}
	// 每个可见字符后插入随机零宽字符：浏览器渲染不可见，但脚本用 textContent
	// 直接提取会得到带 U+200B/U+200C 的串，提交后校验失败，挡住简单正则抓取。
	zeroWidth := []string{"&#8203;", "&#8204;", "&#8205;"}
	for i, ch := range code {
		x := 12 + i*27
		y := 26 + randomInt(4)
		color := colors[randomInt(4)]
		rotate := randomInt(30) - 15
		fmt.Fprintf(&sb, "<text x='%d' y='%d' font-family='Arial,sans-serif' font-size='22' font-weight='bold' fill='%s' transform='rotate(%d %d %d)'>%c%s</text>\n",
			x, y, color, rotate, x, y, ch, zeroWidth[randomInt(len(zeroWidth))])
	}
	sb.WriteString("</svg>\n")
	return id, sb.String()
}

// Verify 校验并消费（一次性）。
func (c *CaptchaStore) Verify(id, code string) bool {
	if strings.TrimSpace(id) == "" || strings.TrimSpace(code) == "" {
		return false
	}
	c.mu.Lock()
	entry, ok := c.store[id]
	if ok {
		delete(c.store, id)
	}
	c.mu.Unlock()
	if !ok {
		return false
	}
	if time.Now().Unix()-entry.createdAt > c.ttl {
		return false
	}
	return strings.EqualFold(strings.TrimSpace(code), entry.code)
}

func (c *CaptchaStore) cleanupIfNeeded() {
	now := time.Now().Unix()
	c.mu.Lock()
	defer c.mu.Unlock()
	if now-c.lastGC < 60 {
		return
	}
	c.lastGC = now
	for k, v := range c.store {
		if now-v.createdAt > c.ttl {
			delete(c.store, k)
		}
	}
	if len(c.store) > c.maxSize {
		// 简单粗暴：超限时清空一半（与原实现一致）
		i := 0
		for k := range c.store {
			delete(c.store, k)
			i++
			if i >= c.maxSize/2 {
				break
			}
		}
	}
}
