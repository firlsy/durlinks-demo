package main

import "time"

// ImportSource 与枚举一致：0=人工导入，1=AI 接入
type ImportSource int

const (
	ImportManual ImportSource = 0
	ImportAI     ImportSource = 1
)

// UserRole 0=超级管理员（内置 admin），1=管理员
type UserRole int

const (
	RoleSuperAdmin UserRole = 0
	RoleAdmin      UserRole = 1
)

// RoleName 对齐 C# enum.ToString()，JWT 与前端都用这个名字
func (r UserRole) String() string {
	if r == RoleSuperAdmin {
		return "SuperAdmin"
	}
	return "Admin"
}

// ---------- 实体（数据库行） ----------

type User struct {
	ID           int
	Username     string
	PasswordHash string
	Role         UserRole
	MfaSecret    *string
	MfaEnabled   bool
	TokenVersion int
	CreatedAt    time.Time
}

type Link struct {
	ID           int
	SortOrder    int
	LinkType     string
	GroupName    string
	Title        string
	URL          string
	ExtractCode  *string
	ExpireAt     *string
	Platform     *string
	Version      *string
	Remark       *string
	Summary      *string
	CreatedAt    time.Time
	ImportSource ImportSource
	IsHidden     bool
	IsDeleted    bool
	LibraryID    *int
	LibraryName  *string
}

type Tag struct {
	ID   int
	Name string
}

type PublishLibrary struct {
	ID              int
	Name            string
	Description     *string
	VisibleOnWeb    bool
	VisibleOnClient bool
	SortOrder       int
	IsDeleted       bool
	CreatedAt       time.Time
	LinkCount       int
}

type LlmConfig struct {
	ID           int
	Name         string
	Protocol     string
	RetryPolicy  string
	MaxRetries   int
	RetryOn      string
	ApiEndpoint  string
	ApiKey       string
	Model        string
	ModelsJson   string
	IsConfigured bool
	IsDefault    bool
	UpdatedAt    time.Time
}

// ---------- DTO（JSON 契约，全部 camelCase，对齐 ASP.NET 默认序列化） ----------

type SubLinkDto struct {
	URL   string  `json:"url"`
	Title *string `json:"title"`
}

type LinkDto struct {
	ID                int          `json:"id"`
	SortOrder         int          `json:"sortOrder"`
	LinkType          string       `json:"linkType"`
	GroupName         string       `json:"groupName"`
	Title             string       `json:"title"`
	URL               string       `json:"url"`
	Urls              []string     `json:"urls"`
	ExtractCode       *string      `json:"extractCode"`
	ExpireAt          *string      `json:"expireAt"`
	Platform          *string      `json:"platform"`
	Version           *string      `json:"version"`
	Remark            *string      `json:"remark"`
	Summary           *string      `json:"summary"`
	CreatedAt         string       `json:"createdAt"`
	ImportSource      ImportSource `json:"importSource"`
	ImportSourceLabel string       `json:"importSourceLabel"`
	SubLinks          []SubLinkDto `json:"subLinks"`
	Tags              []string     `json:"tags"`
	IsHidden          bool         `json:"isHidden"`
	IsDeleted         bool         `json:"isDeleted"`
	LibraryID         *int         `json:"libraryId"`
	LibraryName       *string      `json:"libraryName"`
}

type PagedResult struct {
	Items      []LinkDto `json:"items"`
	Total      int       `json:"total"`
	Page       int       `json:"page"`
	PageSize   int       `json:"pageSize"`
	TotalPages int       `json:"totalPages"`
}

type LibraryDto struct {
	ID              int     `json:"id"`
	Name            string  `json:"name"`
	Description     *string `json:"description"`
	VisibleOnWeb    bool    `json:"visibleOnWeb"`
	VisibleOnClient bool    `json:"visibleOnClient"`
	SortOrder       int     `json:"sortOrder"`
	LinkCount       int     `json:"linkCount"`
	CreatedAt       string  `json:"createdAt"`
}

type LibrarySaveRequest struct {
	Name            string  `json:"name"`
	Description     *string `json:"description"`
	VisibleOnWeb    bool    `json:"visibleOnWeb"`
	VisibleOnClient bool    `json:"visibleOnClient"`
	SortOrder       int     `json:"sortOrder"`
}

type DeleteLibraryRequest struct {
	Password string `json:"password"`
}

type LoginRequest struct {
	Username    string  `json:"username"`
	Password    string  `json:"password"`
	TotpCode    *string `json:"totpCode"`
	CaptchaID   *string `json:"captchaId"`
	CaptchaCode *string `json:"captchaCode"`
}

type LoginResponse struct {
	Token       string `json:"token"`
	Username    string `json:"username"`
	Role        string `json:"role"`
	MfaEnabled  bool   `json:"mfaEnabled"`
	MfaRequired bool   `json:"mfaRequired"`
}

type CaptchaDto struct {
	ID       string `json:"id"`
	Svg      string `json:"svg"`
	ImageURL string `json:"imageUrl"`
}

type MfaCheckDto struct {
	MfaEnabled bool `json:"mfaEnabled"`
}

type MfaSetupDto struct {
	Secret string `json:"secret"`
	QrURI  string `json:"qrUri"`
}

type MfaStatusDto struct {
	MfaEnabled bool `json:"mfaEnabled"`
}

type MfaDisableRequest struct {
	Code     *string `json:"code"`
	Password *string `json:"password"`
}

type ChangePasswordRequest struct {
	CurrentPassword string `json:"currentPassword"`
	NewPassword     string `json:"newPassword"`
}

type UserDto struct {
	ID         int    `json:"id"`
	Username   string `json:"username"`
	Role       string `json:"role"`
	MfaEnabled bool   `json:"mfaEnabled"`
	CreatedAt  string `json:"createdAt"`
}

type CreateUserRequest struct {
	Username string `json:"username"`
	Password string `json:"password"`
}

type LinkCreateRequest struct {
	LibraryID   *int     `json:"libraryId"`
	Title       string   `json:"title"`
	URL         string   `json:"url"`
	LinkType    *string  `json:"linkType"`
	GroupName   *string  `json:"groupName"`
	Platform    *string  `json:"platform"`
	Version     *string  `json:"version"`
	ExtractCode *string  `json:"extractCode"`
	ExpireAt    *string  `json:"expireAt"`
	Remark      *string  `json:"remark"`
	Summary     *string  `json:"summary"`
	Tags        []string `json:"tags"`
}

type LinkUpdateRequest struct {
	LibraryID   *int     `json:"libraryId"`
	Title       *string  `json:"title"`
	URL         *string  `json:"url"`
	LinkType    *string  `json:"linkType"`
	GroupName   *string  `json:"groupName"`
	Platform    *string  `json:"platform"`
	Version     *string  `json:"version"`
	ExtractCode *string  `json:"extractCode"`
	ExpireAt    *string  `json:"expireAt"`
	Remark      *string  `json:"remark"`
	Summary     *string  `json:"summary"`
	Tags        []string `json:"tags"`
	IsHidden    *bool    `json:"isHidden"`
}

type HiddenDto struct {
	Hidden bool `json:"hidden"`
}

type BatchIdsDto struct {
	IDs    []int `json:"ids"`
	Hidden bool  `json:"hidden"`
}

type MoveCopyLinksRequest struct {
	IDs             []int `json:"ids"`
	TargetLibraryID int   `json:"targetLibraryId"`
}

type PasteDto struct {
	Text string `json:"text"`
}

type LinkInfoDto struct {
	Title      string   `json:"title"`
	Recognized bool     `json:"recognized"`
	URL        string   `json:"url"`
	RawText    string   `json:"rawText"`
	LinkType   string   `json:"linkType"`
	GroupName  string   `json:"groupName"`
	Platform   string   `json:"platform"`
	Summary    string   `json:"summary"`
	Tags       []string `json:"tags"`
}

type CategoryOptionsDto struct {
	Groups    []string `json:"groups"`
	Platforms []string `json:"platforms"`
	Tags      []string `json:"tags"`
	LinkTypes []string `json:"linkTypes"`
}

type CategoryNameRequest struct {
	Name *string `json:"name"`
}

type CategoryRenameRequest struct {
	OldName *string `json:"oldName"`
	NewName *string `json:"newName"`
}

type CategoryBatchDeleteRequest struct {
	Names []string `json:"names"`
}

type CategoryBatchDeleteResult struct {
	Deleted []string `json:"deleted"`
	Skipped []string `json:"skipped"`
}

type FilterOptionsDto struct {
	Groups    []string `json:"groups"`
	Tags      []string `json:"tags"`
	Platforms []string `json:"platforms"`
}

// ---------- LLM ----------

type LlmConfigDto struct {
	ID           int      `json:"id"`
	Name         string   `json:"name"`
	Protocol     string   `json:"protocol"`
	RetryPolicy  string   `json:"retryPolicy"`
	MaxRetries   int      `json:"maxRetries"`
	RetryOn      []string `json:"retryOn"`
	ApiEndpoint  string   `json:"apiEndpoint"`
	ApiKey       string   `json:"apiKey"`
	Model        string   `json:"model"`
	Models       []string `json:"models"`
	IsConfigured bool     `json:"isConfigured"`
	IsDefault    bool     `json:"isDefault"`
	HasApiKey    bool     `json:"hasApiKey"`
}

type SetModelRequest struct {
	Model string `json:"model"`
}

type LlmModelDto struct {
	ID          string  `json:"id"`
	DisplayName *string `json:"displayName"`
}

type LlmModelsResult struct {
	Ok        bool          `json:"ok"`
	Message   string        `json:"message"`
	Models    []LlmModelDto `json:"models"`
	LatencyMs int64         `json:"latencyMs"`
}

// ---------- Excel 导入 ----------

type ExcelImportStatus struct {
	Running         bool    `json:"running"`
	FileName        string  `json:"fileName"`
	Processed       int     `json:"processed"`
	Imported        int     `json:"imported"`
	Skipped         int     `json:"skipped"`
	Failed          int     `json:"failed"`
	Message         string  `json:"message"`
	CancelRequested bool    `json:"cancelRequested"`
	StartedAt       *string `json:"startedAt"`
	FinishedAt      *string `json:"finishedAt"`
}

// ---------- AI 补料 ----------

type BatchEnrichRequest struct {
	Mode        string `json:"mode"`        // missing=仅未补料（默认）/ all=全部
	Limit       int    `json:"limit"`       // 本次最多处理条数，<=0 按 100；传了 linkIds 时忽略
	Concurrency int    `json:"concurrency"` // 并发数 1-8，默认 3
	LibraryID   int    `json:"libraryId"`   // 0=全部
	LinkIDs     []int  `json:"linkIds"`     // 非空时只处理这些 ID（表格勾选）
}

type BatchEnrichStatus struct {
	Running         bool    `json:"running"`
	Mode            string  `json:"mode"`
	Selected        int     `json:"selected"` // 通过表格勾选传入的条数，0 表示按范围/条数
	Total           int     `json:"total"`
	Processed       int     `json:"processed"`
	Succeeded       int     `json:"succeeded"`
	Failed          int     `json:"failed"`
	Skipped         int     `json:"skipped"`
	Message         string  `json:"message"`
	StartedAt       *string `json:"startedAt"`
	FinishedAt      *string `json:"finishedAt"`
	CancelRequested bool    `json:"cancelRequested"`
}

type ExportPackageRequest struct {
	Password string `json:"password"`
}

type DedupeRequest struct {
	Scope     string `json:"scope"`
	LibraryID *int   `json:"libraryId"`
	DryRun    bool   `json:"dryRun"`
}

type DedupeResult struct {
	Scanned    int               `json:"scanned"`
	Groups     int               `json:"groups"`
	Duplicates int               `json:"duplicates"`
	Removed    int               `json:"removed"`
	Kept       int               `json:"kept"`
	Samples    []DedupeSampleDto `json:"samples"`
	Message    string            `json:"message"`
}

type DedupeSampleDto struct {
	URL    string   `json:"url"`
	Count  int      `json:"count"`
	Titles []string `json:"titles"`
}

type LinkQuery struct {
	ID            int
	Q             string
	Group         string
	Tag           string
	Platform      string
	ImportSource  *ImportSource
	Groups        []string
	Tags          []string
	Platforms     []string
	ImportSources []ImportSource
	LibraryID     *int
	Channel       string
	Page          int
	PageSize      int
}

// APIError 统一错误体：{ "message": "..." }
type APIError struct {
	Message string `json:"message"`
}
