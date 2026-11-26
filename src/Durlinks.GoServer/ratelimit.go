package main

import (
	"sort"
	"sync"
	"time"
)

const (
	loginWindowSize = 10 * time.Minute // 失败计数窗口
	loginMaxFails   = 10               // 窗口内允许的失败次数
	loginLockFor    = 5 * time.Minute  // 超限后锁定时间
	// 条目上限：攻击者用海量不同用户名刷失败登录时，map 不能无限涨
	loginMaxEntries = 10000
)

type loginAttempt struct {
	count    int
	firstAt  time.Time
	lockedTo time.Time
}

// loginLimiter 按 "IP|用户名" 限制登录失败次数，防止无脑爆破。
// 原来的实现完全没有限流，而 PBKDF2 每次要吃 100ms CPU，等于给了个廉价的 DoS 面。
type loginLimiter struct {
	mu     sync.Mutex
	data   map[string]*loginAttempt
	lastGC time.Time
}

func newLoginLimiter() *loginLimiter {
	return &loginLimiter{data: map[string]*loginAttempt{}}
}

func (l *loginLimiter) blocked(key string) (bool, time.Duration) {
	l.mu.Lock()
	defer l.mu.Unlock()
	a, ok := l.data[key]
	if !ok {
		return false, 0
	}
	now := time.Now()
	if a.lockedTo.After(now) {
		return true, time.Until(a.lockedTo)
	}
	if now.Sub(a.firstAt) > loginWindowSize {
		delete(l.data, key)
	}
	return false, 0
}

func (l *loginLimiter) fail(key string) {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := time.Now()
	l.gcLocked(now)
	a, ok := l.data[key]
	if !ok || now.Sub(a.firstAt) > loginWindowSize {
		l.data[key] = &loginAttempt{count: 1, firstAt: now}
		return
	}
	a.count++
	if a.count >= loginMaxFails {
		a.lockedTo = now.Add(loginLockFor)
	}
}

func (l *loginLimiter) success(key string) {
	l.mu.Lock()
	delete(l.data, key)
	l.mu.Unlock()
}

func (l *loginLimiter) gcLocked(now time.Time) {
	if now.Sub(l.lastGC) < time.Minute && len(l.data) < loginMaxEntries {
		return
	}
	l.lastGC = now
	for k, a := range l.data {
		if now.Sub(a.firstAt) > loginWindowSize && !a.lockedTo.After(now) {
			delete(l.data, k)
		}
	}
	if len(l.data) <= loginMaxEntries {
		return
	}
	// 仍超限：丢掉最老的一半。宁可放宽限流，也不能让内存被刷爆。
	type item struct {
		key     string
		firstAt time.Time
	}
	items := make([]item, 0, len(l.data))
	for k, a := range l.data {
		items = append(items, item{k, a.firstAt})
	}
	sort.Slice(items, func(i, j int) bool { return items[i].firstAt.Before(items[j].firstAt) })
	for i := 0; i < len(items)/2; i++ {
		delete(l.data, items[i].key)
	}
}
