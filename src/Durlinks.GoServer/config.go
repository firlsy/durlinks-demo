package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

// Config 对齐原 appsettings.json：键名大小写不敏感（Go 的 json 解码本就忽略大小写）。
type Config struct {
	Database struct {
		Provider string `json:"provider"`
		Path     string `json:"path"`
		SeedXlsx string `json:"seedXlsx"`
	} `json:"database"`
	ConnectionStrings struct {
		Postgres string `json:"postgres"`
	} `json:"connectionStrings"`
	Kestrel struct {
		Host string `json:"host"`
	} `json:"kestrel"`
	Ports struct {
		Public int `json:"public"`
		Admin  int `json:"admin"`
	} `json:"ports"`
	StaticFiles struct {
		UserDir  string `json:"userDir"`
		AdminDir string `json:"adminDir"`
	} `json:"staticFiles"`
	Jwt struct {
		Secret string `json:"secret"`
	} `json:"jwt"`
	// Web.Mode: go=Go 服务端直接渲染用户端（默认，不依赖 .NET 产物）；static=托管 wwwroot-user 静态站
	Web struct {
		Mode string `json:"mode"`
	} `json:"web"`
	BatchEnrich struct {
		MaxItems      int `json:"maxItems"`
		MinIntervalMs int `json:"minIntervalMs"`
	} `json:"batchEnrich"`
}

// LoadConfig 读取 config.json（可选），环境变量优先覆盖，缺省值与 appsettings.json 一致。
func LoadConfig(path string) (*Config, error) {
	c := &Config{}
	// 默认值
	c.Database.Provider = "sqlite"
	c.Database.Path = "data/durlinks.db"
	c.Kestrel.Host = "localhost"
	c.Ports.Public = 8789
	c.Ports.Admin = 8788
	c.StaticFiles.UserDir = "wwwroot-user"
	c.StaticFiles.AdminDir = "wwwroot-admin"
	c.Web.Mode = "go"
	c.BatchEnrich.MaxItems = 200000
	c.BatchEnrich.MinIntervalMs = 100

	if path != "" {
		if raw, err := os.ReadFile(path); err == nil {
			if err := json.Unmarshal(raw, c); err != nil {
				return nil, fmt.Errorf("解析配置文件 %s 失败: %w", path, err)
			}
		} else if !os.IsNotExist(err) {
			return nil, err
		}
	}

	// 环境变量覆盖（DURLINKS_* 与 ASP.NET 风格的 __ 分隔都支持）
	env := func(keys ...string) (string, bool) {
		for _, k := range keys {
			if v, ok := os.LookupEnv(k); ok && v != "" {
				return v, true
			}
		}
		return "", false
	}
	if v, ok := env("DURLINKS_DB_PROVIDER", "Database__Provider"); ok {
		c.Database.Provider = v
	}
	if v, ok := env("DURLINKS_DB_PATH", "Database__Path"); ok {
		c.Database.Path = v
	}
	if v, ok := env("DURLINKS_SEED_XLSX", "Database__SeedXlsx"); ok {
		c.Database.SeedXlsx = v
	}
	if v, ok := env("DURLINKS_POSTGRES", "ConnectionStrings__Postgres"); ok {
		c.ConnectionStrings.Postgres = v
	}
	if v, ok := env("DURLINKS_HOST", "Kestrel__Host"); ok {
		c.Kestrel.Host = v
	}
	if v, ok := env("DURLINKS_PORT_PUBLIC", "Ports__Public"); ok {
		if n, err := strconv.Atoi(v); err == nil {
			c.Ports.Public = n
		}
	}
	if v, ok := env("DURLINKS_PORT_ADMIN", "Ports__Admin"); ok {
		if n, err := strconv.Atoi(v); err == nil {
			c.Ports.Admin = n
		}
	}
	if v, ok := env("DURLINKS_JWT_SECRET", "Jwt__Secret"); ok {
		c.Jwt.Secret = v
	}
	if v, ok := env("DURLINKS_STATIC_USER", "StaticFiles__UserDir"); ok {
		c.StaticFiles.UserDir = v
	}
	if v, ok := env("DURLINKS_STATIC_ADMIN", "StaticFiles__AdminDir"); ok {
		c.StaticFiles.AdminDir = v
	}

	c.Database.Provider = strings.ToLower(strings.TrimSpace(c.Database.Provider))
	if c.Database.Provider == "" {
		c.Database.Provider = "sqlite"
	}

	// 用户端渲染模式：默认 go（服务端直出）；显式写 static 才回退到托管 wwwroot-user
	c.Web.Mode = strings.ToLower(strings.TrimSpace(c.Web.Mode))
	if c.Web.Mode != "static" {
		c.Web.Mode = "go"
	}
	return c, nil
}

// ResolvePath 相对路径按基准目录解析（原实现以 ContentRoot 为基准）。
func ResolvePath(base, p string) string {
	if p == "" {
		return p
	}
	if filepath.IsAbs(p) {
		return p
	}
	return filepath.Join(base, p)
}
