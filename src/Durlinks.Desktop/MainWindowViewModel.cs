using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Durlinks.Shared.DTOs;

namespace Durlinks.Desktop;

/// <summary>布局尺寸：桌面 / 平板 / 移动。</summary>
public enum LayoutSize { Desktop, Tablet, Mobile }

/// <summary>主题模式：跟随系统 / 浅色 / 深色。</summary>
public enum ThemeMode { System, Light, Dark }

/// <summary>筛选呈现档位：四维全展 / 二维+更多 / 单按钮。</summary>
public enum FilterTier { Full, Compact, Button }

/// <summary>提示类型。</summary>
public enum ToastKind { Success, Info, Warning, Error }

/// <summary>单条提示（用四个布尔控制配色，避免 VM 直接解析主题画刷）。</summary>
public class ToastItem : INotifyPropertyChanged
{
    private string _message = "";
    public string Message { get => _message; set { if (_message == value) return; _message = value; OnPropertyChanged(); } }
    public bool IsSuccess { get; set; }
    public bool IsInfo { get; set; }
    public bool IsWarning { get; set; }
    public bool IsError { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>筛选维度 chip（来源为单选，其余多选）。</summary>
public class FilterChipVm
{
    public string Dimension { get; set; } = "";
    public string Value { get; set; } = "";
    public string Display { get; set; } = "";
    public bool IsSelected { get; set; }
    /// <summary>仅复合平台值挂 ToolTip；其余维度为 null 不显示气泡。</summary>
    public string? ToolTipText { get; set; }
}

/// <summary>搜索/刷新的触发来源：切库不铺骨架屏，其余保持替换式骨架。</summary>
public enum SearchReason { FirstLoad, PageNav, LibrarySwitch, Filter, Keyword, Other }

/// <summary>发布库切换条目（左侧 rail 与移动抽屉共用）。</summary>
public class LibraryChipVm : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _showCount;
    private int _linkCount;

    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>该库的链接条数（条数由客户端补齐，晚于条目创建，必须会通知）。</summary>
    public int LinkCount
    {
        get => _linkCount;
        set
        {
            if (_linkCount == value) return;
            _linkCount = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Id==0 的合成项「全部发布库」。</summary>
    public bool IsAll => Id == 0;

    /// <summary>条数是否展示：≥216 宽度档 + 该库有条目 + 非「全部」。</summary>
    public bool ShowCount
    {
        get => _showCount;
        set
        {
            if (_showCount == value) return;
            _showCount = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowCount));
            OnPropertyChanged(nameof(AccessibleName));
        }
    }

    /// <summary>读屏用名称：选中项追加「已选中」。</summary>
    public string AccessibleName => IsSelected ? $"{Name}，已选中" : Name;

    /// <summary>圆形字标中的首字（「全部发布库」→「全」）。</summary>
    public string Initial
    {
        get
        {
            var name = Name?.Trim();
            return string.IsNullOrEmpty(name) ? "·" : name[..1];
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}


/// <summary>每页条数选项（选中态由 VM 驱动，供 Flyout / 面板复用同一模板）。</summary>
public class PageSizeOptionVm : INotifyPropertyChanged
{
    private bool _isSelected;
    public int Value { get; init; }
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>附属链接的展示项：标题缺失时退回网址，避免出现空按钮。</summary>
public class SubLinkVm
{
    public string Display { get; init; } = "";
    public string Url { get; init; } = "";
}

/// <summary>表格中的一行（原卡片 VM）：承载行内展开状态与列可见性，DTO 保持只读。</summary>
public class LinkCardViewModel : INotifyPropertyChanged
{
    public LinkDto Link { get; }
    /// <summary>跨页连续序号（从 1 计），与 Link.Id 无关。</summary>
    public int Index { get; }

    public LinkCardViewModel(LinkDto link, int index = 0)
    {
        Link = link;
        Index = index;
        // 多 URL 时把主链接提到首位，避免「点 A 开 B」：TryOpenLink 取首个 http(s)。
        EnsurePrimaryUrlFirst();
        BuildMetaText();
        SubLinks = Link.SubLinks
            .Where(s => !string.IsNullOrWhiteSpace(s.Url) || !string.IsNullOrWhiteSpace(s.Title))
            .Select(s =>
            {
                var title = s.Title?.Trim();
                var url = s.Url?.Trim() ?? "";
                return new SubLinkVm
                {
                    Display = string.IsNullOrEmpty(title) ? ClampText(url) : title,
                    Url = url
                };
            })
            .ToList();
    }

    /// <summary>附属链接没有标题时用网址（或后端塞进 url 字段的描述文字）顶替，过长截断。</summary>
    private static string ClampText(string text, int max = 48) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>附属链接（标题缺失时以网址代替）。</summary>
    public IReadOnlyList<SubLinkVm> SubLinks { get; }

    private void EnsurePrimaryUrlFirst()
    {
        var primary = Link.Url;
        if (string.IsNullOrWhiteSpace(primary)) return;
        var pos = Link.Urls.FindIndex(u => string.Equals(u, primary, StringComparison.OrdinalIgnoreCase));
        if (pos == 0) return;
        if (pos > 0) Link.Urls.RemoveAt(pos);
        Link.Urls.Insert(0, primary);
    }

    /// <summary>平台展示统一走归一化，原始值仅用于后端 IN 查询。</summary>
    public string PlatformDisplay => MainWindowViewModel.NormalizePlatformKey(Link.Platform);

    public bool IsAi => Link.ImportSourceLabel == "AI接入";
    public bool IsManual => !IsAi;
    public bool HasSummary => !string.IsNullOrWhiteSpace(Link.Summary);
    public bool HasExtractCode => !string.IsNullOrWhiteSpace(Link.ExtractCode);
    public bool HasPlatform => !string.IsNullOrWhiteSpace(Link.Platform);
    public bool HasGroup => !string.IsNullOrWhiteSpace(Link.GroupName);
    /// <summary>按过滤后的展示项判断，避免「有附属链接」却渲染出空行。</summary>
    public bool HasSubLinks => SubLinks.Count > 0;
    public bool HasUrl => !string.IsNullOrWhiteSpace(Link.Url);
    public bool HasExpireAt => !string.IsNullOrWhiteSpace(Link.ExpireAt);

    /// <summary>「分组 · 标签」列：类型 → 分组 → #标签，来源与平台有独立列，不重复。</summary>
    public string MetaText { get; private set; } = "";
    public bool HasMeta => MetaText.Length > 0;

    public string ExtractText => HasExtractCode ? $"提取码 {Link.ExtractCode}" : "";
    public string CreatedText => Link.CreatedAt == default ? "" : Link.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");

    /// <summary>标题 Tooltip：摘要通常比标题更有信息量，两者一起给。</summary>
    public string RowTooltip => HasSummary ? $"{Link.Title}\n{Link.Summary}" : Link.Title;

    /// <summary>行内详情是否可展开（摘要 / 提取码 / 到期 / 附属链接）。</summary>
    public bool HasDetail => HasSummary || HasExtractCode || HasSubLinks || HasExpireAt;

    private bool _isDetailExpanded;
    public bool IsDetailExpanded
    {
        get => _isDetailExpanded;
        set
        {
            if (_isDetailExpanded == value) return;
            _isDetailExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailToggleName));
        }
    }
    public string DetailToggleName => IsDetailExpanded ? "收起详情" : "展开详情";
    public void ToggleDetail() => IsDetailExpanded = !IsDetailExpanded;

    // ---- 列可见性：由窗口按当前净宽统一下发，表头与行各持一份 ----
    private bool _showIndexColumn = true, _showMetaColumn = true, _showPlatformColumn = true,
                 _showSourceColumn = true, _showDateColumn = true, _showCopyAction = true, _showOpenAction = true;

    public bool ShowIndexColumn => _showIndexColumn;
    public bool ShowMetaColumn => _showMetaColumn;
    public bool ShowPlatformColumn => _showPlatformColumn;
    public bool ShowSourceColumn => _showSourceColumn;
    public bool ShowDateColumn => _showDateColumn;
    public bool ShowCopyAction => _showCopyAction;
    public bool ShowOpenAction => _showOpenAction;

    public void ApplyTableMetrics(bool index, bool meta, bool platform, bool source, bool date, bool copy, bool open)
    {
        if (_showIndexColumn == index && _showMetaColumn == meta && _showPlatformColumn == platform
            && _showSourceColumn == source && _showDateColumn == date
            && _showCopyAction == copy && _showOpenAction == open) return;

        _showIndexColumn = index;
        _showMetaColumn = meta;
        _showPlatformColumn = platform;
        _showSourceColumn = source;
        _showDateColumn = date;
        _showCopyAction = copy;
        _showOpenAction = open;
        OnPropertyChanged(nameof(ShowIndexColumn));
        OnPropertyChanged(nameof(ShowMetaColumn));
        OnPropertyChanged(nameof(ShowPlatformColumn));
        OnPropertyChanged(nameof(ShowSourceColumn));
        OnPropertyChanged(nameof(ShowDateColumn));
        OnPropertyChanged(nameof(ShowCopyAction));
        OnPropertyChanged(nameof(ShowOpenAction));
    }

    public void BuildMetaText()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Link.LinkType)) parts.Add(Link.LinkType.Trim());
        if (HasGroup) parts.Add(Link.GroupName.Trim());
        foreach (var tag in Link.Tags.Where(t => !string.IsNullOrWhiteSpace(t)))
            parts.Add($"#{tag.Trim()}");
        MetaText = string.Join("  ·  ", parts);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>桌面客户端主视图模型：搜索、筛选、发布库、设置、主题、提示。</summary>
public class MainWindowViewModel : INotifyPropertyChanged
{
    private const string DefaultBackend = "https://demo.durlists.de5.net:8080";
    // 便携模式：配置固定落在 exe 同目录，不受快捷方式/命令行带来的工作目录影响
    private static readonly string SettingsFile =
        Path.Combine(AppContext.BaseDirectory, "durlinks.settings.txt");
    private static readonly int[] PageSizeChoices = [20, 50, 100, 200, 500];
    private const int StickyChipLimit = 10; // 吸顶每维最多展示 10 个，其余走「更多筛选」
    // 摘要 clamp 估算，由 code-behind 按布局写入（chip 不设上限）
    // 表格列可见性（由 ApplyLayout 按净宽下发，表头与行各持一份）
    private bool _tblIndex = true, _tblMeta = true, _tblPlatform = true, _tblSource = true,
                 _tblDate = true, _tblCopy = true, _tblOpen = true;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private long _requestId;                 // 递增请求号，只渲染最新响应
    private CancellationTokenSource? _loadingCts;
    private CancellationTokenSource? _searchCts; // 取消已过期的搜索请求，避免快速输入时堆积网络任务
    private CancellationTokenSource? _countCts; // rail 条数批量请求
    private bool _suppressSearch;

    private string _keyword = "", _status = "共 0 条结果", _backend = DefaultBackend;
    private string _connectionText = "未连接", _updatedText = "更新于 --:--";
    private int _pageSize = 20, _page = 1, _total, _totalPages;
    private string _settingsUrl = DefaultBackend, _testMessage = "";
    private string _emptyTitle = "", _emptyHint = "", _shortcutHint;

    private ObservableCollection<LinkDto> _links = [];
    private ObservableCollection<LinkCardViewModel> _cards = [];
    private ObservableCollection<LibraryDto> _libraries = [];
    private ObservableCollection<LibraryChipVm> _libraryChips = [];
    private ObservableCollection<FilterChipVm> _groupChips = [];
    private ObservableCollection<FilterChipVm> _tagChips = [];
    private ObservableCollection<FilterChipVm> _platformChips = [];
    private ObservableCollection<FilterChipVm> _sourceChips = [];
    private ObservableCollection<PageSizeOptionVm> _pageSizeOptions = [];
    // rail：吸顶「全部发布库」单条 + 可滚动列表（不含 Id=0）
    private LibraryChipVm _allLibraryChip = new() { Id = 0, Name = "全部发布库", IsSelected = true };
    private ObservableCollection<LibraryChipVm> _railLibraries = [];
    private bool _showRailCounts;
    private bool _librariesLoadFailed;
    private int _savedLibraryId;             // settings 第 5 行：上次选中的库 Id
    private ObservableCollection<ToastItem> _toasts = [];
    private ObservableCollection<int> _skeletonItems = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    private LibraryDto? _selectedLibrary;
    private LayoutSize _layoutSize = LayoutSize.Desktop;
    private ThemeMode _themeMode = ThemeMode.System;
    private double _brandBarHeight = 56, _searchBoxHeight = 40;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindowViewModel()
    {
        _shortcutHint = OperatingSystem.IsMacOS() ? "⌘ K" : "Ctrl K";
        LoadSettings();
        BuildSourceChips();
        BuildPageSizeOptions();
    }

    // ---------------- 基础数据 ----------------

    public string BackendUrl
    {
        get => _backend;
        set => Set(ref _backend, value); // 不再 set 即写文件
    }

    public string Keyword
    {
        get => _keyword;
        set { if (Set(ref _keyword, value)) OnPropertyChanged(nameof(ShowClearSearchHint)); }
    }

    public string Status { get => _status; set => Set(ref _status, value); }
    public string ConnectionText { get => _connectionText; set => Set(ref _connectionText, value); }
    public string UpdatedText { get => _updatedText; set => Set(ref _updatedText, value); }
    public string ShortcutHint => _shortcutHint;
    public Thickness BrandLeftPadding { get; } =
        new(OperatingSystem.IsMacOS() ? 72 : 20, 0, 0, 0);

    public ObservableCollection<LinkDto> Links
    {
        get => _links;
        set { if (Set(ref _links, value)) OnPropertyChanged(nameof(Links)); }
    }

    public ObservableCollection<LinkCardViewModel> Cards
    {
        get => _cards;
        set { if (Set(ref _cards, value)) { OnPropertyChanged(nameof(Cards)); OnPropertyChanged(nameof(HasCards)); OnPropertyChanged(nameof(ShowEmpty)); } }
    }

    public ObservableCollection<LibraryDto> Libraries
    {
        get => _libraries;
        set => Set(ref _libraries, value);
    }

    public ObservableCollection<LibraryChipVm> LibraryChips
    {
        get => _libraryChips;
        set => Set(ref _libraryChips, value);
    }

    public ObservableCollection<FilterChipVm> GroupChips
    {
        get => _groupChips;
        set => Set(ref _groupChips, value);
    }

    public ObservableCollection<FilterChipVm> TagChips
    {
        get => _tagChips;
        set => Set(ref _tagChips, value);
    }

    public ObservableCollection<FilterChipVm> PlatformChips
    {
        get => _platformChips;
        set => Set(ref _platformChips, value);
    }

    public ObservableCollection<FilterChipVm> SourceChips
    {
        get => _sourceChips;
        set => Set(ref _sourceChips, value);
    }

    public ObservableCollection<PageSizeOptionVm> PageSizeOptions
    {
        get => _pageSizeOptions;
        set => Set(ref _pageSizeOptions, value);
    }

    /// <summary>rail 顶部吸顶的「全部发布库」（Id=0 合成项，不在滚动列表里）。</summary>
    public LibraryChipVm AllLibraryChip
    {
        get => _allLibraryChip;
        set => Set(ref _allLibraryChip, value);
    }

    /// <summary>rail 可滚动区的发布库（不含 Id=0 的「全部」）。</summary>
    public ObservableCollection<LibraryChipVm> RailLibraries
    {
        get => _railLibraries;
        set => Set(ref _railLibraries, value);
    }

    /// <summary>发布库加载失败：rail 底部展示「重试」。</summary>
    public bool LibrariesLoadFailed
    {
        get => _librariesLoadFailed;
        set => Set(ref _librariesLoadFailed, value);
    }

    /// <summary>
    /// 由 code-behind 按 rail 宽度档调用：仅 ≥216 档展示条数。
    /// 写入偏好位并立刻刷新现有条目（含构建前已存在的实例）。
    /// </summary>
    public void ApplyRailMetrics(bool showCount)
    {
        _showRailCounts = showCount;
        foreach (var chip in LibraryChips) chip.ShowCount = ShouldShowCount(chip);
    }

    private bool ShouldShowCount(LibraryChipVm chip) =>
        _showRailCounts && chip.LinkCount > 0 && !chip.IsAll;

    // 吸顶筛选行只展示每维前 10 项（已选项始终保留），避免大库标签把结果区挤没
    public ObservableCollection<FilterChipVm> StickyGroupChips => BuildStickyChips(GroupChips);
    public ObservableCollection<FilterChipVm> StickyTagChips => BuildStickyChips(TagChips);
    public ObservableCollection<FilterChipVm> StickyPlatformChips => BuildStickyChips(PlatformChips);
    public ObservableCollection<FilterChipVm> StickySourceChips => BuildStickyChips(SourceChips);
    public bool HasMoreFilters => GroupChips.Count > StickyChipLimit
                               || TagChips.Count > StickyChipLimit
                               || PlatformChips.Count > StickyChipLimit
                               || SourceChips.Count > StickyChipLimit;

    private static ObservableCollection<FilterChipVm> BuildStickyChips(ObservableCollection<FilterChipVm> source)
    {
        var list = source.Take(StickyChipLimit).ToList();
        // 隐藏区里被选中的项也要出现在吸顶行，方便用户看到当前筛选状态
        foreach (var chip in source.Skip(StickyChipLimit).Where(c => c.IsSelected))
            if (!list.Contains(chip)) list.Add(chip);
        return new ObservableCollection<FilterChipVm>(list);
    }

    public ObservableCollection<ToastItem> Toasts
    {
        get => _toasts;
        set => Set(ref _toasts, value);
    }

    public ObservableCollection<int> SkeletonItems
    {
        get => _skeletonItems;
        set => Set(ref _skeletonItems, value);
    }

    // ---------------- 表格列可见性 ----------------

    public bool ShowIndexColumn => _tblIndex;
    public bool ShowMetaColumn => _tblMeta;
    public bool ShowPlatformColumn => _tblPlatform;
    public bool ShowSourceColumn => _tblSource;
    public bool ShowDateColumn => _tblDate;

    /// <summary>布局切换后统一改写列可见性：表头（本 VM）与每一行（行 VM）各刷一遍，不触发搜索。</summary>
    public void ApplyTableMetrics(bool index, bool meta, bool platform, bool source, bool date, bool copy, bool open)
    {
        if (_tblIndex == index && _tblMeta == meta && _tblPlatform == platform && _tblSource == source
            && _tblDate == date && _tblCopy == copy && _tblOpen == open) return;

        _tblIndex = index;
        _tblMeta = meta;
        _tblPlatform = platform;
        _tblSource = source;
        _tblDate = date;
        _tblCopy = copy;
        _tblOpen = open;
        OnPropertyChanged(nameof(ShowIndexColumn));
        OnPropertyChanged(nameof(ShowMetaColumn));
        OnPropertyChanged(nameof(ShowPlatformColumn));
        OnPropertyChanged(nameof(ShowSourceColumn));
        OnPropertyChanged(nameof(ShowDateColumn));
        foreach (var card in Cards) ApplyTableMetricsTo(card, index, meta, platform, source, date, copy, open);
    }

    private static void ApplyTableMetricsTo(LinkCardViewModel card,
        bool index, bool meta, bool platform, bool source, bool date, bool copy, bool open)
        => card.ApplyTableMetrics(index, meta, platform, source, date, copy, open);

    public LibraryDto? SelectedLibrary
    {
        get => _selectedLibrary;
        set
        {
            if (!Set(ref _selectedLibrary, value)) return;
            RefreshLibraryChipSelection();   // 原地改 IsSelected：rail 滚动位置不回到顶部
            OnPropertyChanged(nameof(CurrentLibraryName));
            SaveSettings();                  // 第 5 行持久化上次选中的库
            if (!_suppressSearch) _ = SearchAsync(SearchReason.LibrarySwitch);
        }
    }

    /// <summary>移动库按钮上显示的当前库名。</summary>
    public string CurrentLibraryName => SelectedLibrary?.Name ?? "选择发布库";

    // ---------------- 状态标志 ----------------

    private bool _isBusy, _isLoadingVisible, _showSkeleton = true, _isError;
    private bool _isSettingsOpen, _isFilterPanelOpen, _isLibraryDrawerOpen, _isTestingConnection, _testSuccess;
    // 筛选区默认折叠：首屏把纵向空间全部让给结果列表，需要时点 header 展开
    private bool _isFiltersCollapsed = true;
    private FilterTier _filterTier = FilterTier.Full;
    private string _errorReason = "";

    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }
    public bool IsLoadingVisible { get => _isLoadingVisible; set => Set(ref _isLoadingVisible, value); }
    public bool ShowSkeleton { get => _showSkeleton; set { if (Set(ref _showSkeleton, value)) { OnPropertyChanged(nameof(ShowEmpty)); OnPropertyChanged(nameof(HasCards)); } } }
    public bool IsError { get => _isError; set { if (Set(ref _isError, value)) OnPropertyChanged(nameof(ShowEmpty)); } }
    public string ErrorReason { get => _errorReason; set => Set(ref _errorReason, value); }

    public bool IsSettingsOpen { get => _isSettingsOpen; set => Set(ref _isSettingsOpen, value); }
    public bool IsFilterPanelOpen { get => _isFilterPanelOpen; set => Set(ref _isFilterPanelOpen, value); }
    /// <summary>移动库抽屉开合。</summary>
    public bool IsLibraryDrawerOpen { get => _isLibraryDrawerOpen; set => Set(ref _isLibraryDrawerOpen, value); }
    /// <summary>筛选档位，由窗口按布局与高度计算后写入。</summary>
    public FilterTier FilterTier { get => _filterTier; set => Set(ref _filterTier, value); }
    public bool IsTestingConnection { get => _isTestingConnection; set { if (Set(ref _isTestingConnection, value)) OnPropertyChanged(nameof(CanTestConnection)); } }
    public bool CanTestConnection => !IsTestingConnection;
    public bool TestSuccess { get => _testSuccess; set { if (Set(ref _testSuccess, value)) OnPropertyChanged(nameof(TestFailed)); } }
    public bool TestFailed => !string.IsNullOrWhiteSpace(TestMessage) && !TestSuccess;
    public string TestMessage { get => _testMessage; set { if (Set(ref _testMessage, value)) OnPropertyChanged(nameof(TestFailed)); } }
    public string SettingsUrl { get => _settingsUrl; set => Set(ref _settingsUrl, value); }

    public bool IsConnected => ConnectionText == "已连接";
    public bool IsConnecting => ConnectionText == "连接中…";
    public bool IsDisconnected => !IsConnected && !IsConnecting;
    public bool HasCards => Cards.Count > 0;
    public bool ShowEmpty => !ShowSkeleton && !IsError && Cards.Count == 0;
    public bool ShowClearSearchHint => !string.IsNullOrWhiteSpace(Keyword);

    // ---------------- 分页 ----------------

    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (!Set(ref _pageSize, value)) return;
            UpdatePageSizeSelection();
            SaveSettings();
            if (!_suppressSearch) _ = SearchAsync(); // 改每页条数：Page 归 1 重搜
        }
    }
    public int Page { get => _page; set => Set(ref _page, value); }
    public int Total { get => _total; set => Set(ref _total, value); }
    public int TotalPages { get => _totalPages; set => Set(ref _totalPages, value); }
    public bool CanGoPrevious => Page > 1 && !IsBusy;
    public bool CanGoNext => TotalPages > 0 && Page < TotalPages && !IsBusy;
    public bool CanLoadMore => CanGoNext;
    public string MobileStatus => TotalPages > 0 ? $"共 {Total} 条 · {Page}/{TotalPages} 页" : $"共 {Total} 条";
    public string PageIndicator => $"第 {Page} / {TotalPages} 页";
    public string MobileLoadMoreText => $"加载更多（第 {Page}/{TotalPages} 页）";

    // 底部进度条：按已翻到第几页 / 共几页折算；只有多页时才有意义，单页不画
    public double ProgressPercent => TotalPages > 1
        ? Math.Min(100d, Math.Round(Page * 100d / TotalPages, 1))
        : 0d;
    public string ProgressText => TotalPages > 1
        ? (ProgressPercent >= 1 ? $"{ProgressPercent:0}%" : "‹1%")
        : "";
    public bool HasProgress => TotalPages > 1;

    /// <summary>每页条数切换入口：非法值忽略，同值不重搜。</summary>
    public void SetPageSize(int size)
    {
        if (!PageSizeChoices.Contains(size) || size == PageSize) return;
        PageSize = size;
    }

    private void BuildPageSizeOptions()
    {
        PageSizeOptions = new ObservableCollection<PageSizeOptionVm>(
            PageSizeChoices.Select(v => new PageSizeOptionVm { Value = v, IsSelected = v == PageSize }));
    }

    private void UpdatePageSizeSelection()
    {
        foreach (var option in PageSizeOptions) option.IsSelected = option.Value == PageSize;
        OnPropertyChanged(nameof(PageSize));
    }

    /// <summary>筛选折叠状态：仅影响可访问性/展示，不改变筛选条件。</summary>
    public bool IsFiltersCollapsed
    {
        get => _isFiltersCollapsed;
        set
        {
            if (!Set(ref _isFiltersCollapsed, value)) return;
            OnPropertyChanged(nameof(FilterToggleName));
            OnPropertyChanged(nameof(FilterStatusText));
            SaveSettings();
        }
    }
    public string FilterToggleName => IsFiltersCollapsed ? "展开筛选" : "收起筛选";
    /// <summary>
    /// 筛选行右侧文案。默认收起后这里同时承担「发现入口」职责：
    /// 无筛选时点明可筛维度，有筛选时提示生效数量，两者都带「点此展开」暗示整行可点。
    /// 展开态四维已在下方常显，不再占文案。
    /// </summary>
    public string FilterStatusText => IsFiltersCollapsed
        ? (HasActiveFilters ? $"{ActiveFilterCount}项筛选生效中 · 点此展开" : "分组 / 标签 / 平台 / 来源 · 点此展开")
        : "";

    public int ActiveFilterCount => GroupChips.Count(c => c.IsSelected)
                                  + TagChips.Count(c => c.IsSelected)
                                  + PlatformChips.Count(c => c.IsSelected)
                                  + SourceChips.Count(c => c.IsSelected);
    public bool HasActiveFilters => ActiveFilterCount > 0;

    // 侧栏「筛选」段的逐维计数徽章
    public int GroupSelectedCount => GroupChips.Count(c => c.IsSelected);
    public int TagSelectedCount => TagChips.Count(c => c.IsSelected);
    public int PlatformSelectedCount => PlatformChips.Count(c => c.IsSelected);
    public int SourceSelectedCount => SourceChips.Count(c => c.IsSelected);
    public bool HasGroupFilter => GroupSelectedCount > 0;
    public bool HasTagFilter => TagSelectedCount > 0;
    public bool HasPlatformFilter => PlatformSelectedCount > 0;
    public bool HasSourceFilter => SourceSelectedCount > 0;

    public string EmptyTitle { get => _emptyTitle; set => Set(ref _emptyTitle, value); }
    public string EmptyHint { get => _emptyHint; set => Set(ref _emptyHint, value); }

    public LayoutSize LayoutSize
    {
        get => _layoutSize;
        set { if (Set(ref _layoutSize, value)) { OnPropertyChanged(nameof(IsDesktop)); OnPropertyChanged(nameof(IsTablet)); OnPropertyChanged(nameof(IsMobile)); } }
    }
    public bool IsDesktop => LayoutSize == LayoutSize.Desktop;
    public bool IsTablet => LayoutSize == LayoutSize.Tablet;
    public bool IsMobile => LayoutSize == LayoutSize.Mobile;

    public double BrandBarHeight { get => _brandBarHeight; set => Set(ref _brandBarHeight, value); }
    public double SearchBoxHeight { get => _searchBoxHeight; set => Set(ref _searchBoxHeight, value); }

    // ---------------- 主题 ----------------

    public ThemeMode ThemeMode
    {
        get => _themeMode;
        set
        {
            if (!Set(ref _themeMode, value)) return;
            OnPropertyChanged(nameof(IsSystemTheme));
            OnPropertyChanged(nameof(IsLightTheme));
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(ThemeTooltip));
        }
    }
    public bool IsSystemTheme => ThemeMode == ThemeMode.System;
    public bool IsLightTheme => ThemeMode == ThemeMode.Light;
    public bool IsDarkTheme => ThemeMode == ThemeMode.Dark;
    public string ThemeTooltip => ThemeMode switch
    {
        ThemeMode.Light => "主题：浅色（点击切换为深色）",
        ThemeMode.Dark => "主题：深色（点击切换为跟随系统）",
        _ => "主题：跟随系统（点击切换为浅色）"
    };

    /// <summary>三态循环：跟随系统 → 浅色 → 深色。</summary>
    public void ToggleTheme()
    {
        ThemeMode = ThemeMode switch
        {
            ThemeMode.System => ThemeMode.Light,
            ThemeMode.Light => ThemeMode.Dark,
            _ => ThemeMode.System
        };
        ApplyTheme();
        SaveSettings();
    }

    private void ApplyTheme()
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = ThemeMode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    // ---------------- 设置读写 ----------------

    public void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) { ApplyTheme(); return; }
            var lines = File.ReadAllLines(SettingsFile);
            if (lines.Length > 0)
            {
                var url = NormalizeUrl(lines[0]);
                if (url is not null) _backend = url;
            }
            if (lines.Length > 1)
                _themeMode = lines[1].Trim().ToLowerInvariant() switch
                {
                    "light" => ThemeMode.Light,
                    "dark" => ThemeMode.Dark,
                    _ => ThemeMode.System
                };
            // 兼容旧 2~4 行文件；第 3/4/5 行分别为每页条数、筛选折叠态、上次选中的库 Id
            if (lines.Length > 2 && int.TryParse(lines[2].Trim(), out var size) && PageSizeChoices.Contains(size))
                _pageSize = size;
            if (lines.Length > 3)
                _isFiltersCollapsed = lines[3].Trim().Equals("collapsed", StringComparison.OrdinalIgnoreCase);
            if (lines.Length > 4 && int.TryParse(lines[4].Trim(), out var libraryId) && libraryId >= 0)
                _savedLibraryId = libraryId;
        }
        catch { /* 读取失败用默认值 */ }
        UpdatePageSizeSelection();
        _settingsUrl = _backend;
        ApplyTheme();
    }

    private void SaveSettings()
    {
        try
        {
            var token = ThemeMode switch
            {
                ThemeMode.Light => "light",
                ThemeMode.Dark => "dark",
                _ => "system"
            };
            // 第 5 行 = 上次选中的库 Id（0 = 全部发布库）；旧 4 行文件读回时按 0 处理
            File.WriteAllText(SettingsFile,
                $"{_backend}\n{token}\n{_pageSize}\n{(_isFiltersCollapsed ? "collapsed" : "expanded")}\n{_selectedLibrary?.Id ?? 0}");
        }
        catch { /* 写文件失败不影响使用 */ }
    }

    /// <summary>失焦时提交地址：合法才保存，否则回退。</summary>
    public void CommitSettingsUrl()
    {
        var url = NormalizeUrl(SettingsUrl);
        if (url is null)
        {
            SettingsUrl = BackendUrl;
            return;
        }
        if (SettingsUrl != url) SettingsUrl = url;
        if (url != BackendUrl)
        {
            BackendUrl = url;
            SaveSettings();
        }
    }

    public static string? NormalizeUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().TrimEnd('/');
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        return Uri.TryCreate(s, UriKind.Absolute, out _) ? s : null;
    }

    /// <summary>恢复默认地址（仅改草稿，测试成功或失焦才落盘）。</summary>
    public void ResetBackend()
    {
        SettingsUrl = DefaultBackend;
        TestMessage = "";
        TestSuccess = false;
        OnPropertyChanged(nameof(SettingsUrl));
    }

    public void OpenSettings()
    {
        SettingsUrl = BackendUrl;
        TestMessage = "";
        TestSuccess = false;
        IsSettingsOpen = true;
    }

    public void CloseSettings() => IsSettingsOpen = false;

    /// <summary>测试连接：8s 超时探针，成功保存地址并返回 true。</summary>
    public async Task<bool> TestConnectionAsync()
    {
        var url = NormalizeUrl(SettingsUrl);
        if (url is null)
        {
            TestSuccess = false;
            TestMessage = "请输入以 http:// 或 https:// 开头的地址";
            return false;
        }

        IsTestingConnection = true;
        TestMessage = "正在连接…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var libs = await _http.GetFromJsonAsync<List<LibraryDto>>(
                $"{url}/api/libraries?channel=client", cts.Token) ?? [];
            TestSuccess = true;
            TestMessage = $"连接成功 · 发现 {libs.Count} 个库";
            BackendUrl = url;
            SaveSettings();
            return true;
        }
        catch (Exception ex)
        {
            TestSuccess = false;
            TestMessage = NormalizeError(ex);
            return false;
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    // ---------------- 发布库 ----------------

    /// <summary>加载客户端可见的发布库（首项恒为合成的「全部发布库」Id=0）。</summary>
    public async Task LoadLibrariesAsync()
    {
        // 「全部」在 try 之外合成：后端不可达时 rail 仍有一个可选项，搜索走错误态
        var all = new List<LibraryDto> { new() { Id = 0, Name = "全部发布库" } };
        LibrariesLoadFailed = false;
        try
        {
            var baseUrl = NormalizeUrl(BackendUrl)
                ?? throw new InvalidOperationException("后端地址无效");
            var libs = await _http.GetFromJsonAsync<List<LibraryDto>>($"{baseUrl}/api/libraries?channel=client") ?? [];
            all.AddRange(libs);
        }
        catch
        {
            LibrariesLoadFailed = true; // rail 底部展示「发布库加载失败 · 重试」
        }

        var keepId = _selectedLibrary?.Id ?? _savedLibraryId;
        _suppressSearch = true;
        Libraries = new ObservableCollection<LibraryDto>(all);
        // 默认「全部发布库」；上次选中的库已不存在则回退 Id=0 并提示
        SelectedLibrary = all.FirstOrDefault(x => x.Id == keepId) ?? all[0];
        _suppressSearch = false;
        if (keepId > 0 && all.All(x => x.Id != keepId))
            ShowToast("原发布库已不存在，已切换到全部发布库", ToastKind.Warning);
        RefreshLibraryChips();
        _ = LoadLibraryCountsAsync(RailLibraries); // 条数异步补齐，不阻塞首屏
    }

    /// <summary>
    /// 公开接口 /api/libraries 不下发 LinkCount（仅管理端带），服务端契约不变，
    /// 这里用 pageSize=1 的轻量请求读 total 补齐 rail 条数；并发上限 6，单库失败静默跳过。
    /// </summary>
    private async Task LoadLibraryCountsAsync(IEnumerable<LibraryChipVm> chips)
    {
        var baseUrl = NormalizeUrl(BackendUrl);
        var targets = chips.Where(c => !c.IsAll).ToList();
        if (baseUrl is null || targets.Count == 0) return;

        _countCts?.Cancel();
        _countCts = new CancellationTokenSource();
        var token = _countCts.Token;
        using var gate = new SemaphoreSlim(6);

        var tasks = targets.Select(async chip =>
        {
            try
            {
                await gate.WaitAsync(token);
                try
                {
                    var page = await _http.GetFromJsonAsync<PagedResult<LinkDto>>(
                        $"{baseUrl}/api/links?channel=client&page=1&pageSize=1&libraryId={chip.Id}", token);
                    if (token.IsCancellationRequested) return;
                    chip.LinkCount = page?.Total ?? 0;
                    chip.ShowCount = ShouldShowCount(chip);
                }
                finally { gate.Release(); }
            }
            catch { /* 单库条数失败不影响其它库 */ }
        });
        try { await Task.WhenAll(tasks); }
        catch { /* 忽略取消 */ }
    }

    /// <summary>重建 chip 集合：LibraryChips 为全量（含 Id=0，供抽屉/滚动定位），rail 用后两者。</summary>
    private void RefreshLibraryChips()
    {
        var chips = Libraries.Select(l => new LibraryChipVm
        {
            Id = l.Id,
            Name = l.Name,
            LinkCount = l.LinkCount,
            IsSelected = l.Id == SelectedLibrary?.Id
        }).ToList();
        foreach (var chip in chips) chip.ShowCount = ShouldShowCount(chip);

        LibraryChips = new ObservableCollection<LibraryChipVm>(chips);
        AllLibraryChip = chips.FirstOrDefault(x => x.IsAll) ?? new LibraryChipVm
        {
            Id = 0,
            Name = "全部发布库",
            IsSelected = SelectedLibrary is null || SelectedLibrary.Id == 0
        };
        RailLibraries = new ObservableCollection<LibraryChipVm>(chips.Where(x => !x.IsAll));
    }

    /// <summary>切换选中只改现有实例的状态，绝不重建集合（否则 rail 滚动位置每次回顶）。</summary>
    private void RefreshLibraryChipSelection()
    {
        var selectedId = SelectedLibrary?.Id;
        foreach (var chip in LibraryChips) chip.IsSelected = chip.Id == selectedId;
    }

    /// <summary>chip 点击切库：保留关键词与筛选。</summary>
    public void SelectLibrary(int id)
    {
        var lib = Libraries.FirstOrDefault(x => x.Id == id);
        if (lib is not null) SelectedLibrary = lib;
    }

    // ---------------- 筛选 ----------------

    private void BuildSourceChips()
    {
        SourceChips = new ObservableCollection<FilterChipVm>
        {
            new() { Dimension = "source", Value = "AI接入", Display = "AI接入" },
            new() { Dimension = "source", Value = "人工导入", Display = "人工导入" }
        };
    }

    /// <summary>加载筛选选项；接口不存在或失败则静默降级为空选项。</summary>
    public async Task LoadFiltersAsync()
    {
        try
        {
            var baseUrl = NormalizeUrl(BackendUrl);
            if (baseUrl is null) return;
            var opt = await _http.GetFromJsonAsync<FilterOptionsDto>($"{baseUrl}/api/filters?channel=client");
            if (opt is null) return;
            GroupChips = RebuildChips(GroupChips, "group", opt.Groups.Where(x => !string.IsNullOrWhiteSpace(x)));
            TagChips = RebuildChips(TagChips, "tag", opt.Tags.Where(x => !string.IsNullOrWhiteSpace(x)));
            PlatformChips = RebuildPlatformChips(PlatformChips, opt.Platforms.Where(x => !string.IsNullOrWhiteSpace(x)));
            RaiseStickyFilterChanged();
            RaiseFilterChanged();
        }
        catch { /* 静默降级：筛选区为空 */ }
    }

    private static ObservableCollection<FilterChipVm> RebuildChips(
        ObservableCollection<FilterChipVm> old, string dimension, IEnumerable<string> values)
    {
        var selected = old.Where(c => c.IsSelected).Select(c => c.Value).ToHashSet();
        var list = values.Distinct().Take(60).Select(v => new FilterChipVm
        {
            Dimension = dimension,
            Value = v,
            Display = v,
            IsSelected = selected.Contains(v)
        });
        return new ObservableCollection<FilterChipVm>(list);
    }

    /// <summary>平台 chip：按归一化 key 去重、保留选中并按 Windows→MacOS→Linux→复合→其它固定排序。</summary>
    private static ObservableCollection<FilterChipVm> RebuildPlatformChips(
        ObservableCollection<FilterChipVm> old, IEnumerable<string> values)
    {
        // 旧 chip 也按 key 归一化，避免 win32 → Windows 后选中丢失
        var selected = old.Where(c => c.IsSelected).Select(c => NormalizePlatformKey(c.Value)).ToHashSet();
        var normalized = values.Select(NormalizePlatformKey)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()
            .OrderBy(PlatformRank).ThenBy(x => x, StringComparer.Ordinal)
            .Take(60)
            .Select(v => new FilterChipVm
            {
                Dimension = "platform", Value = v, Display = v, IsSelected = selected.Contains(v),
                ToolTipText = v.Contains('/') ? "同时支持 Windows / macOS / Linux 多平台的资源" : null
            });
        return new ObservableCollection<FilterChipVm>(normalized);
    }

    /// <summary>trim + 转小写 + 压缩空格；复合值（含 /）保留原样独立成项。</summary>
    public static string NormalizePlatformKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = string.Join(" ", raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (s.Contains('/')) return s; // Windows/Mac/Linux 等复合项不拆分、不归并
        return s.ToLowerInvariant() switch
        {
            "windows" or "win" or "win32" or "win64" or "win10" or "win11" or "winxp" => "Windows",
            "macos" or "mac os" or "mac" or "osx" or "os x" => "MacOS",
            "linux" => "Linux",
            _ => s
        };
    }

    /// <summary>把归一化 key 展开为可能的原始值集合，供后端 IN 查询；不改数据库。</summary>
    public static string[] ExpandPlatformKey(string key) => key switch
    {
        "Windows" => ["Windows", "windows", "Win", "win", "Win32", "win32", "Win64", "win64",
                      "Win10", "win10", "Win11", "win11", "WinXP", "winxp"],
        "MacOS" => ["MacOS", "macos", "Mac", "mac", "Mac OS", "mac os", "OSX", "osx", "OS X", "os x"],
        "Linux" => ["Linux", "linux"],
        _ => [key]
    };

    private static int PlatformRank(string key) => key switch
    {
        "Windows" => 0,
        "MacOS" => 1,
        "Linux" => 2,
        _ => key.Contains('/') ? 3 : 4
    };

    /// <summary>来源单选，其余维度多选。</summary>
    public void OnFilterChipToggled(FilterChipVm chip)
    {
        if (chip.Dimension == "source" && chip.IsSelected)
            foreach (var other in SourceChips.Where(x => !ReferenceEquals(x, chip))) other.IsSelected = false;
        RaiseFilterChanged();
    }

    public void ClearFilters()
    {
        foreach (var chip in AllFilterChips()) chip.IsSelected = false;
        RaiseFilterChanged(false);
    }

    private IEnumerable<FilterChipVm> AllFilterChips() =>
        GroupChips.Concat(TagChips).Concat(PlatformChips).Concat(SourceChips);

    private void RaiseStickyFilterChanged()
    {
        OnPropertyChanged(nameof(StickyGroupChips));
        OnPropertyChanged(nameof(StickyTagChips));
        OnPropertyChanged(nameof(StickyPlatformChips));
        OnPropertyChanged(nameof(StickySourceChips));
        OnPropertyChanged(nameof(HasMoreFilters));
    }

    private void RaiseFilterChanged(bool search = true)
    {
        OnPropertyChanged(nameof(ActiveFilterCount));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(FilterStatusText));
        OnPropertyChanged(nameof(GroupSelectedCount));
        OnPropertyChanged(nameof(TagSelectedCount));
        OnPropertyChanged(nameof(PlatformSelectedCount));
        OnPropertyChanged(nameof(SourceSelectedCount));
        OnPropertyChanged(nameof(HasGroupFilter));
        OnPropertyChanged(nameof(HasTagFilter));
        OnPropertyChanged(nameof(HasPlatformFilter));
        OnPropertyChanged(nameof(HasSourceFilter));
        RaiseStickyFilterChanged();
        if (search) _ = SearchAsync(SearchReason.Filter);
    }

    /// <summary>
    /// 兼容旧调用点的 noop：多维筛选已全部下推后端（方案 A），客户端不再本地过滤，
    /// 否则会出现每页不满 / Total 失真 / 序号断裂 / 翻页漏数据。
    /// </summary>
    private static List<LinkDto> ApplyFilters(IEnumerable<LinkDto> source) => source.ToList();

    // ---------------- 搜索 ----------------

    /// <summary>默认行为：回到第 1 页重搜（关键词/切库/筛选/每页条数变化都走这里）。</summary>
    public Task SearchAsync(SearchReason reason = SearchReason.Other) => SearchAsyncCore(1, false, reason);

    /// <summary>翻页：替换当前页数据，序号按 (Page-1)×PageSize 重排。</summary>
    public Task GoToPageAsync(int page) => SearchAsyncCore(page, false, SearchReason.PageNav);

    /// <summary>移动端「加载更多」：追加下一页，不清空已加载卡片。</summary>
    public Task LoadMoreAsync() => SearchAsyncCore(Page + 1, true, SearchReason.PageNav);

    private async Task SearchAsyncCore(int page, bool append, SearchReason reason = SearchReason.Other)
    {
        var requestId = ++_requestId;
        var targetPage = Math.Max(1, page);

        // 新搜索立即中止旧请求；requestId 仍负责防止已完成/不可取消的旧响应覆盖新结果。
        var searchCts = new CancellationTokenSource();
        var previousSearchCts = Interlocked.Exchange(ref _searchCts, searchCts);
        var searchToken = searchCts.Token;
        if (previousSearchCts is not null)
        {
            try { previousSearchCts.Cancel(); }
            catch (ObjectDisposedException) { } // 旧请求可能刚好结束
        }
        IsBusy = true;
        IsError = false;
        ConnectionText = "连接中…";
        // 切库不铺骨架：先保留旧列表 + 脉冲点，1200ms 仍未返回才升级为骨架屏
        ShowSkeleton = !append && reason != SearchReason.LibrarySwitch;
        RaiseConnectionChanged();
        RaisePagingChanged();

        // 请求超过 150ms 才显示内联 spinner，避免闪现
        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var loadingToken = _loadingCts.Token;
        _ = Task.Delay(150, loadingToken).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                // IsBusy 兜底：请求若已在 150ms 内结束，finally 已复位过一次，
                // 这里再补亮 spinner 就再也没人关它（会永久亮着）
                if (requestId == _requestId && IsBusy) IsLoadingVisible = true;
            });
        });

        // 切库的慢请求兜底：超过 1200ms 才把保留的旧列表换成骨架屏
        if (reason == SearchReason.LibrarySwitch)
        {
            _ = Task.Delay(1200, loadingToken).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (requestId == _requestId && IsBusy) ShowSkeleton = true;
                });
            });
        }

        try
        {
            var baseUrl = NormalizeUrl(BackendUrl);
            if (baseUrl is null) throw new InvalidOperationException("后端地址无效，请在设置中修改为 http(s) 地址");

            var qs = new List<string>
            {
                "channel=client",
                $"page={targetPage}",
                $"pageSize={PageSize}"
            };
            if (!string.IsNullOrWhiteSpace(Keyword)) qs.Add($"q={Uri.EscapeDataString(Keyword.Trim())}");
            if (SelectedLibrary is { Id: > 0 }) qs.Add($"libraryId={SelectedLibrary.Id}");

            // 四维多选全部下推后端；平台先归一化再展开为原始值集合，后端用 IN 语义过滤
            AddMultiValues(qs, "groups", GroupChips.Where(c => c.IsSelected).Select(c => c.Value));
            AddMultiValues(qs, "tags", TagChips.Where(c => c.IsSelected).Select(c => c.Value));
            AddMultiValues(qs, "platforms", PlatformChips.Where(c => c.IsSelected)
                .SelectMany(c => ExpandPlatformKey(c.Value)));
            AddMultiValues(qs, "importSources", SourceChips.Where(c => c.IsSelected)
                .Select(c => c.Value == "AI接入" ? "1" : "0"));

            var resp = await _http.GetFromJsonAsync<PagedResult<LinkDto>>(
                $"{baseUrl}/api/links?{string.Join("&", qs)}", searchToken);
            if (requestId != _requestId) return; // 竞态：丢弃过期响应

            var items = ApplyFilters(resp?.Items ?? []);
            var indexBase = (targetPage - 1) * PageSize;
            var newCards = items.Select((x, i) => new LinkCardViewModel(x, indexBase + i + 1)).ToList();
            // 新建行必须继承当前列的可见性，否则缩放窗口后新页的行会多出隐藏列
            foreach (var card in newCards)
                ApplyTableMetricsTo(card, _tblIndex, _tblMeta, _tblPlatform, _tblSource, _tblDate, _tblCopy, _tblOpen);

            if (append)
            {
                foreach (var c in newCards) Cards.Add(c);
                foreach (var l in items) Links.Add(l);
                OnPropertyChanged(nameof(HasCards));
                OnPropertyChanged(nameof(ShowEmpty));
            }
            else
            {
                Links = new ObservableCollection<LinkDto>(items);
                Cards = new ObservableCollection<LinkCardViewModel>(newCards); // 触发结果滚回顶
            }

            Page = targetPage;
            Total = resp?.Total ?? 0;
            TotalPages = resp is { TotalPages: > 0 } ? resp.TotalPages : (Total + PageSize - 1) / PageSize;
            IsError = false;
            ConnectionText = "已连接";
            Status = BuildStatusText();
            UpdatedText = $"更新于 {DateTime.Now:HH:mm}";
            UpdateEmptyState();
            RaisePagingChanged();
        }
        catch (Exception ex)
        {
            if (requestId != _requestId) return;
            if (!append) { Links = []; Cards = []; }
            IsError = true;
            ErrorReason = NormalizeError(ex);
            ConnectionText = "未连接";
            Status = "连接失败";
            UpdatedText = $"更新于 {DateTime.Now:HH:mm}";
            RaisePagingChanged();
        }
        finally
        {
            if (requestId == _requestId)
            {
                _loadingCts.Cancel();
                IsLoadingVisible = false;
                IsBusy = false;
                ShowSkeleton = false;
                RaiseConnectionChanged();
                RaisePagingChanged();
            }

            // 较新请求负责取消并释放旧 CTS；本次只有在仍是当前请求时才清理字段。
            if (ReferenceEquals(Interlocked.CompareExchange(ref _searchCts, null, searchCts), searchCts))
                searchCts.Dispose();
        }
    }

    private static void AddMultiValues(List<string> qs, string key, IEnumerable<string> values)
    {
        foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
            qs.Add($"{key}={Uri.EscapeDataString(value.Trim())}");
    }

    /// <summary>状态栏：「全部」只报总数；具体库带库名（超过 12 字截断）。</summary>
    private string BuildStatusText()
    {
        var lib = SelectedLibrary;
        if (lib is { Id: > 0 })
        {
            var name = lib.Name.Trim();
            if (name.Length > 12) name = name[..12] + "…";
            return $"「{name}」共 {Total} 条结果";
        }
        return $"共 {Total} 条结果";
    }

    private void RaisePagingChanged()
    {
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(MobileStatus));
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(MobileLoadMoreText));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(HasProgress));
    }

    private void UpdateEmptyState()
    {
        if (Cards.Count > 0) { ShowClearFiltersHint = false; return; }
        if (!string.IsNullOrWhiteSpace(Keyword))
        {
            EmptyTitle = $"没有找到“{Keyword.Trim()}”相关的链接";
            EmptyHint = "换个关键词试试，或清除搜索查看全部";
            ShowClearFiltersHint = false;
        }
        else if (HasActiveFilters)
        {
            EmptyTitle = "没有符合当前筛选条件的结果";
            EmptyHint = "清除筛选后重试";
            ShowClearFiltersHint = true;
        }
        else if (SelectedLibrary is { Id: > 0 } lib)
        {
            EmptyTitle = $"「{lib.Name}」还没有链接";
            EmptyHint = "切换发布库或稍后再试";
            ShowClearFiltersHint = false;
        }
        else
        {
            EmptyTitle = "还没有任何链接";
            EmptyHint = "在后台添加链接后这里会显示";
            ShowClearFiltersHint = false;
        }
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
    }

    private bool _showClearFiltersHint;
    public bool ShowClearFiltersHint
    {
        get => _showClearFiltersHint;
        set => Set(ref _showClearFiltersHint, value);
    }

    public void ClearSearch()
    {
        Keyword = "";
        _ = SearchAsync(SearchReason.Keyword);
    }

    private void RaiseConnectionChanged()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsDisconnected));
    }

    private static string NormalizeError(Exception ex) => ex switch
    {
        OperationCanceledException or TimeoutException => "连接超时，请检查网络或后端是否在线",
        HttpRequestException h when h.InnerException is System.Net.Sockets.SocketException => "连接被拒绝，请检查地址和端口",
        HttpRequestException h when h.InnerException is System.Security.Authentication.AuthenticationException => "SSL 证书校验失败",
        HttpRequestException => "后端服务返回错误，请检查地址是否正确",
        JsonException => "后端返回的数据格式不正确",
        _ => ex.Message
    };

    // ---------------- 提示 ----------------

    public void ShowToast(string message, ToastKind kind = ToastKind.Info)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 同文案合并：移除旧的重新计时
            var existing = Toasts.FirstOrDefault(t => t.Message == message);
            if (existing is not null) Toasts.Remove(existing);
            var toast = new ToastItem
            {
                Message = message,
                IsSuccess = kind == ToastKind.Success,
                IsInfo = kind == ToastKind.Info,
                IsWarning = kind == ToastKind.Warning,
                IsError = kind == ToastKind.Error
            };
            Toasts.Add(toast);
            while (Toasts.Count > 3) Toasts.RemoveAt(0);

            var delay = kind switch
            {
                ToastKind.Success or ToastKind.Info => 2000,
                ToastKind.Warning => 3000,
                _ => 4000
            };
            _ = Task.Delay(delay).ContinueWith(_ => Dispatcher.UIThread.Post(() => Toasts.Remove(toast)));
        });
    }

    // ---------------- 链接操作 ----------------

    /// <summary>非 http(s) 拒绝打开，多 URL 取第一个并提示。</summary>
    public bool TryOpenLink(LinkCardViewModel card)
    {
        var candidates = card.Link.Urls.Count > 0 ? card.Link.Urls : [card.Link.Url];
        var url = candidates.FirstOrDefault(IsHttpUrl);
        if (url is null)
        {
            ShowToast("链接地址无效，仅支持 http/https", ToastKind.Warning);
            return false;
        }
        var opened = TryOpenUrl(url);
        if (opened && candidates.Count > 1)
            ShowToast($"已打开首个链接（共 {candidates.Count} 个）", ToastKind.Info);
        return opened;
    }

    /// <summary>用系统默认浏览器打开 http(s) 链接。</summary>
    public bool TryOpenUrl(string? url)
    {
        if (!IsHttpUrl(url))
        {
            ShowToast("链接地址无效，仅支持 http/https", ToastKind.Warning);
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            ShowToast($"无法打开链接：{ex.Message}", ToastKind.Error);
            return false;
        }
    }

    public static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // ---------------- INotifyPropertyChanged ----------------

    private bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
