package main

import (
	"fmt"
	"net"
	"net/http"
	"net/url"
	"strings"
)

// validateEndpointURL 校验 LLM 接口地址，阻止 SSRF 访问云元数据/链路本地/特殊地址。
// 保留 loopback（Ollama/LM Studio 本地模型）与私网（内网部署的 LLM 服务）以维持功能。
func validateEndpointURL(raw string) (string, error) {
	e := strings.TrimSpace(raw)
	if e == "" {
		return "", fmt.Errorf("接口地址为空")
	}
	u, err := url.Parse(e)
	if err != nil {
		return "", fmt.Errorf("接口地址无效: %v", err)
	}
	scheme := strings.ToLower(u.Scheme)
	if scheme != "http" && scheme != "https" {
		return "", fmt.Errorf("接口地址仅支持 http/https")
	}
	host := u.Hostname()
	if host == "" {
		return "", fmt.Errorf("接口地址缺少主机名")
	}
	// 解析 host（域名解析后 IP 也要检查，防止 example.com 指向元数据地址）
	ips, err := net.LookupIP(host)
	if err != nil {
		return "", fmt.Errorf("接口地址域名解析失败: %v", err)
	}
	if len(ips) == 0 {
		return "", fmt.Errorf("接口地址域名无解析结果")
	}
	for _, ip := range ips {
		if isForbiddenIP(ip) {
			return "", fmt.Errorf("接口地址指向受限网络地址：%s", ip.String())
		}
	}
	return e, nil
}

// isForbiddenIP 判定高风险 SSRF 目标：链路本地（含云元数据 169.254.169.254）、
// 未指定地址、组播、广播。loopback 与 RFC1918 私网放行（本地/内网模型需要）。
func isForbiddenIP(ip net.IP) bool {
	if ip.IsLinkLocalUnicast() || ip.IsLinkLocalMulticast() {
		return true
	}
	if ip.IsUnspecified() || ip.IsMulticast() || ip.IsInterfaceLocalMulticast() {
		return true
	}
	v4 := ip.To4()
	if v4 != nil {
		// 169.254.0.0/16 已由 IsLinkLocalUnicast 覆盖；额外拦截 0.x 与 240/4 保留段
		if v4[0] == 0 || v4[0] >= 240 {
			return true
		}
	}
	return false
}

// safeLLMRedirect 复用作 SSRF 防护的重定向策略，禁止跳转到受限地址。
func safeLLMRedirect(req *http.Request, via []*http.Request) error {
	if len(via) >= 5 {
		return fmt.Errorf("重定向次数过多")
	}
	host := req.URL.Hostname()
	if host == "" {
		return fmt.Errorf("重定向地址缺少主机名")
	}
	ips, err := net.LookupIP(host)
	if err != nil {
		return err
	}
	for _, ip := range ips {
		if isForbiddenIP(ip) {
			return fmt.Errorf("重定向目标被拒绝：%s", ip.String())
		}
	}
	return nil
}
