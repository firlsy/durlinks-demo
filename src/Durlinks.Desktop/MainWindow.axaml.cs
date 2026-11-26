using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Durlinks.Desktop;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm = new();

    // 搜索 debounce 400ms；布局切换 250ms 节流 + 16px 迟滞
    private readonly DispatcherTimer _debounceTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private DispatcherTimer? _layoutTimer;
    private double _lastWidth = 1100;
    private double _lastHeight = 760;
    // rail 宽度的迟滞状态（初始值对应 1100×760 首帧）
    private double _railWidth = 216;
    private LayoutSize _currentLayout = LayoutSize.Desktop;
    private FilterTier _currentFilterTier = FilterTier.Full;
    private TextBox? _settingsUrlBox;
    private bool _lastShowSkeleton;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // 窗口图标：与网页版 favicon 一致（紫底 @ 图标）
        try
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://Durlinks.Desktop/Assets/icon-192.png"));
            Icon = new WindowIcon(iconStream);
        }
        catch { /* 图标加载失败不影响窗口显示 */ }

        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _ = _vm.SearchAsync(SearchReason.Keyword);
        };

        SearchBox.GotFocus += (_, _) => SetSearchFocused(true);
        SearchBox.LostFocus += (_, _) => SetSearchFocused(false);
        SizeChanged += OnWindowSizeChanged;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainWindowViewModel.IsSettingsOpen)
                or nameof(MainWindowViewModel.IsFilterPanelOpen)
                or nameof(MainWindowViewModel.IsLibraryDrawerOpen))
                ApplyOverlayVisibility();
            if (e.PropertyName is nameof(MainWindowViewModel.IsFiltersCollapsed))
                ApplyFilterVisibility();
            // 搜索/切库/筛选/翻页成功后结果回到顶部（Cards 集合每次替换都会触发；加载更多是原地 Append）
            if (e.PropertyName == nameof(MainWindowViewModel.Cards))
                Dispatcher.UIThread.Post(ResetResultsScroll);
            // 骨架屏出现（false→true）意味着一次新的替换式加载，结果区先回顶
            if (e.PropertyName == nameof(MainWindowViewModel.ShowSkeleton))
            {
                if (_vm.ShowSkeleton && !_lastShowSkeleton) Dispatcher.UIThread.Post(ResetResultsScroll);
                _lastShowSkeleton = _vm.ShowSkeleton;
            }
        };

        // 首帧即按已加载的折叠偏好定型，避免先展开再收起的一帧闪动
        ApplyFilterVisibility();

        Opened += async (_, _) =>
        {
            ApplyLayout(ComputeLayout(Bounds.Width > 0 ? Bounds.Width : _lastWidth));
            await _vm.LoadLibrariesAsync();
            await _vm.LoadFiltersAsync();     // 失败静默降级
            await _vm.SearchAsync(SearchReason.FirstLoad);
        };
    }

    // ---------------- 响应式布局 ----------------

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var height = e.NewSize.Height;
        if (Math.Abs(width - _lastWidth) < 1 && Math.Abs(height - _lastHeight) < 1) return;
        _lastWidth = width;
        _lastHeight = height;

        _layoutTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _layoutTimer.Tick -= OnLayoutTimerTick;
        _layoutTimer.Tick += OnLayoutTimerTick;
        _layoutTimer.Stop();
        _layoutTimer.Start();
    }

    private void OnLayoutTimerTick(object? sender, EventArgs e)
    {
        _layoutTimer?.Stop();
        ApplyLayout(ComputeLayout(_lastWidth > 0 ? _lastWidth : 1100));
    }

    /// <summary>1024 / 768 断点，±16px 迟滞防止抖动。</summary>
    private LayoutSize ComputeLayout(double width)
    {
        var target = width >= 1024 ? LayoutSize.Desktop
                   : width >= 768 ? LayoutSize.Tablet
                   : LayoutSize.Mobile;
        if (target == _currentLayout) return target;

        if (_currentLayout == LayoutSize.Desktop && target == LayoutSize.Tablet && width >= 1008)
            return _currentLayout;
        if (_currentLayout == LayoutSize.Tablet && target == LayoutSize.Desktop && width < 1040)
            return _currentLayout;
        if (_currentLayout == LayoutSize.Tablet && target == LayoutSize.Mobile && width >= 752)
            return _currentLayout;
        if (_currentLayout == LayoutSize.Mobile && target == LayoutSize.Tablet && width < 784)
            return _currentLayout;
        return target;
    }

    /// <summary>宽度档与高度档取更保守的一档：全展 / 二维+更多 / 按钮。</summary>
    private static FilterTier ComputeFilterTier(LayoutSize size, double height)
    {
        var byWidth = size switch
        {
            LayoutSize.Desktop => FilterTier.Full,
            LayoutSize.Tablet => FilterTier.Compact,
            _ => FilterTier.Button
        };
        var byHeight = height >= 720 ? FilterTier.Full
                     : height >= 560 ? FilterTier.Compact
                     : FilterTier.Button;
        return (FilterTier)Math.Min((int)byWidth, (int)byHeight);
    }

    /// <summary>集中切换可见性、工具栏高度、rail 宽度与表格列可见性。</summary>
    private void ApplyLayout(LayoutSize size)
    {
        _currentLayout = size;
        _currentFilterTier = ComputeFilterTier(size, _lastHeight > 0 ? _lastHeight : 760);
        _vm.LayoutSize = size;
        _vm.FilterTier = _currentFilterTier;
        _vm.BrandBarHeight = size == LayoutSize.Desktop ? 52 : 48;
        _vm.SearchBoxHeight = size == LayoutSize.Desktop ? 36 : size == LayoutSize.Tablet ? 36 : 38;

        // 移动端库选择仍走顶部按钮 + 抽屉；桌面/平板走左侧 rail
        Classes.Set("mobile", size == LayoutSize.Mobile);

        var windowWidth = _lastWidth > 0 ? _lastWidth : 1100;

        // 工具栏搜索框：靠右固定宽度，移动档吃掉剩余空间（动作按钮此时只留图标）
        FilterRowHost.IsVisible = size != LayoutSize.Mobile;
        SearchHost.Width = size switch
        {
            LayoutSize.Desktop => 320,
            LayoutSize.Tablet => 220,
            _ => Math.Max(140, windowWidth - 208)
        };

        MobileLibraryBar.IsVisible = size == LayoutSize.Mobile;
        MobileLibraryPicker.IsVisible = size == LayoutSize.Mobile;
        ApplyFilterVisibility();

        // 发布库 rail：Mobile 隐藏；平板及以上常驻 184 / 216 / 232（带 ±16 迟滞）。
        // 列宽显式写入：Auto 列会缓存上一次的宽度，导致 rail 在列内居中、结果区被挤压。
        var railWidth = size == LayoutSize.Mobile ? 0 : RailWidthFor(windowWidth);
        LibraryRail.IsVisible = size != LayoutSize.Mobile;
        WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(railWidth);
        if (railWidth > 0) LibraryRail.Width = railWidth;
        _vm.ApplyRailMetrics(showCount: size == LayoutSize.Mobile || railWidth > 184);

        // 表格列：净宽 = 窗口宽 − rail − 行左右各 12 内距 − 8 竖滚动条预留。
        // 逐级隐藏从右往左的次列，保证「名称」始终是最宽的一列。
        var netWidth = Math.Max(240, windowWidth - railWidth - 24 - 8);
        var showIndex = size != LayoutSize.Mobile;
        var showMeta = netWidth >= 860;
        var showSource = netWidth >= 700;
        var showPlatform = netWidth >= 600;
        var showDate = netWidth >= 520;
        // 桌面/平板保留行内「复制 / 打开」两枚按钮；移动端只留详情展开（44 触控），标题与网址本身可点开
        var showActions = size != LayoutSize.Mobile;
        _vm.ApplyTableMetrics(showIndex, showMeta, showPlatform, showSource, showDate, showActions, showActions);

        // 抽屉宽 min(屏宽-32, 320)
        LibraryDrawerPanel.Width = Math.Min(320, Math.Max(240, windowWidth - 32));
        ApplyOverlayVisibility();
    }

    /// <summary>rail 宽度三档，每个档位的上下界各偏 16px 做迟滞，拖动窗口时不来回跳。</summary>
    private double RailWidthFor(double width)
    {
        var current = _railWidth;
        _railWidth = width >= (current >= 232 ? 1664 : 1680) ? 232
                   : width >= (current >= 216 ? 1264 : 1280) ? 216
                   : 184;
        return _railWidth;
    }

    /// <summary>折叠态隐藏四维 Grid 与「更多筛选」行；Button 档入口按钮本身就是折叠态。</summary>
    private void ApplyFilterVisibility()
    {
        var collapsed = _vm.IsFiltersCollapsed;
        var headerVisible = _currentLayout != LayoutSize.Mobile && _currentFilterTier != FilterTier.Button;
        FilterHeader.IsVisible = headerVisible;
        DesktopFilterBar.IsVisible = headerVisible && !collapsed && _currentFilterTier == FilterTier.Full;
        TabletFilterBar.IsVisible = headerVisible && !collapsed && _currentFilterTier == FilterTier.Compact;
        MobileFilterButton.IsVisible = _currentFilterTier == FilterTier.Button && _currentLayout != LayoutSize.Mobile;
    }

    private void ApplyOverlayVisibility()
    {
        var settingsOpen = _vm.IsSettingsOpen;
        SettingsPopover.IsVisible = settingsOpen && _currentLayout == LayoutSize.Desktop;
        SettingsDialogScrim.IsVisible = settingsOpen && _currentLayout == LayoutSize.Tablet;
        SettingsFull.IsVisible = settingsOpen && _currentLayout == LayoutSize.Mobile;
        FilterOverlay.IsVisible = _vm.IsFilterPanelOpen;
        LibraryDrawer.IsVisible = _vm.IsLibraryDrawerOpen && _currentLayout == LayoutSize.Mobile;
    }

    // ---------------- 搜索 ----------------

    private void SetSearchFocused(bool focused)
    {
        SearchGlow.Classes.Set("focused", focused);
        SearchBorder.Classes.Set("focused", focused);
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _debounceTimer.Stop(); // Enter 立即触发并取消 pending
        _ = _vm.SearchAsync(SearchReason.Keyword);
        e.Handled = true;
    }

    private async void Search_Click(object? sender, RoutedEventArgs e)
    {
        _debounceTimer.Stop();
        await _vm.SearchAsync(SearchReason.Keyword);
    }

    private void ClearSearch_Click(object? sender, RoutedEventArgs e) => _vm.ClearSearch();

    private void Retry_Click(object? sender, RoutedEventArgs e) => _ = _vm.SearchAsync(SearchReason.FirstLoad);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var meta = OperatingSystem.IsMacOS()
            ? e.KeyModifiers.HasFlag(KeyModifiers.Meta)
            : e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (meta && e.Key == Key.K)
        {
            if (_vm.IsLibraryDrawerOpen) CloseLibraryDrawer();
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_vm.IsLibraryDrawerOpen)
            {
                CloseLibraryDrawer();
                e.Handled = true;
            }
            else if (_vm.IsSettingsOpen)
            {
                _vm.CommitSettingsUrl();
                _vm.CloseSettings();
                ApplyOverlayVisibility();
                e.Handled = true;
            }
            else if (_vm.IsFilterPanelOpen)
            {
                _vm.IsFilterPanelOpen = false;
                ApplyOverlayVisibility();
                e.Handled = true;
            }
            else if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                _debounceTimer.Stop();
                SearchBox.Text = "";
                _ = _vm.SearchAsync(SearchReason.Keyword);
                e.Handled = true;
            }
        }

        base.OnKeyDown(e);
    }

    // ---------------- 发布库 / 筛选 ----------------

    private void LibraryChip_Click(object? sender, RoutedEventArgs e)
    {
        // rail 与移动抽屉都用 Button + Tag
        var chip = sender switch
        {
            Button b when b.Tag is LibraryChipVm c => c,
            _ => null
        };
        if (chip is null || chip.IsSelected) return; // 点已选中库无变化，不可取消当前库

        var wasDrawerOpen = _vm.IsLibraryDrawerOpen;
        _vm.SelectLibrary(chip.Id);                       // 用当前关键词 + 筛选 + 新库重搜
        if (wasDrawerOpen) CloseLibraryDrawer();
        // 指针点击后不移动焦点、也不强制滚动 rail：切库保持旧列表直到新数据返回
    }

    private async void RetryLibraries_Click(object? sender, RoutedEventArgs e) =>
        await _vm.LoadLibrariesAsync();

    /// <summary>
    /// rail 键盘导航：↑/↓ 移焦点（不激活、不循环），Home/End 首末，
    /// PageUp/PageDown 移一屏，Enter 激活当前焦点项（Space 由 Button 原生处理）。
    /// </summary>
    private void LibraryRail_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Enter))
            return;

        var buttons = RailItemButtons();
        if (buttons.Count == 0) return;
        var current = buttons.FindIndex(b => b.IsFocused);

        if (e.Key == Key.Enter)
        {
            if (current < 0 || e.Handled) return; // 原生已激活则不重复
            buttons[current].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
            return;
        }

        // 条目 34 + 间距 2（尺寸取自设计资源键，改 token 时键盘翻屏跟着走）
        var rowHeight = RailRowHeight();
        var page = Math.Max(1, (int)Math.Floor(RailScrollViewer.Bounds.Height / rowHeight));
        var next = e.Key switch
        {
            Key.Home => 0,
            Key.End => buttons.Count - 1,
            Key.Up => current <= 0 ? 0 : current - 1,
            Key.Down => current < 0 ? 0 : Math.Min(current + 1, buttons.Count - 1),
            Key.PageUp => current < 0 ? 0 : Math.Max(0, current - page),
            _ => current < 0 ? 0 : Math.Min(current + page, buttons.Count - 1)
        };

        buttons[next].Focus(NavigationMethod.Tab); // 只移焦点，不切库
        buttons[next].BringIntoView();
        e.Handled = true;
    }

    /// <summary>单条 rail 的纵向步进 = 条目高 + 间距（LibraryRailItemHeight + LibraryRailItemGap）。</summary>
    private double RailRowHeight()
    {
        var height = this.FindResource("LibraryRailItemHeight") is double h ? h : 34;
        var gap = this.FindResource("LibraryRailItemGap") is double g ? g : 2;
        return height + gap;
    }

    /// <summary>rail 内可见的条目按钮，按纵向位置排序（吸顶「全部」恒在首位）。</summary>
    private List<Button> RailItemButtons()
    {
        if (!LibraryRail.IsVisible) return [];
        return LibraryRail.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("railItem") && b.IsEffectivelyVisible)
            .OrderBy(b => b.TranslatePoint(new Point(0, 0), LibraryRail)?.Y ?? 0)
            .ToList();
    }

    /// <summary>移动抽屉打开时把当前库滚进视口（44 = 触控条目高）。</summary>
    private void EnsureLibraryVisible(int id)
    {
        if (_currentLayout != LayoutSize.Mobile) return;
        var index = _vm.LibraryChips.ToList().FindIndex(x => x.Id == id);
        if (index < 0) return;
        const double rowHeight = 44;
        var target = Math.Max(0, index * rowHeight - DrawerScrollViewer.Bounds.Height + rowHeight * 2);
        DrawerScrollViewer.Offset = new Vector(0, target);
    }

    private void ResetResultsScroll() => ResultsScrollViewer.Offset = new Vector(0, 0);

    private void OpenLibraryDrawer_Click(object? sender, RoutedEventArgs e)
    {
        _vm.IsLibraryDrawerOpen = true;
        ApplyOverlayVisibility();
        Dispatcher.UIThread.Post(() =>
        {
            LibraryDrawerPanel.Focus();
            EnsureLibraryVisible(_vm.SelectedLibrary?.Id ?? -1);
        });
    }

    private void CloseLibraryDrawer_Click(object? sender, RoutedEventArgs e) => CloseLibraryDrawer();

    private void LibraryDrawerScrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == LibraryDrawer) CloseLibraryDrawer();
    }

    private void CloseLibraryDrawer()
    {
        if (!_vm.IsLibraryDrawerOpen) return;
        _vm.IsLibraryDrawerOpen = false;
        ApplyOverlayVisibility();
        if (_currentLayout == LayoutSize.Mobile) MobileLibraryPicker.Focus();
    }

    private void FilterChip_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: FilterChipVm chip }) _vm.OnFilterChipToggled(chip);
        ResetResultsScroll();
    }

    private void OpenFilterPanel_Click(object? sender, RoutedEventArgs e)
    {
        _vm.IsFilterPanelOpen = true;
        ApplyOverlayVisibility();
    }

    private void CloseFilterPanel_Click(object? sender, RoutedEventArgs e)
    {
        _vm.IsFilterPanelOpen = false;
        ApplyOverlayVisibility();
    }

    private void ApplyFilters_Click(object? sender, RoutedEventArgs e)
    {
        _vm.IsFilterPanelOpen = false;
        ApplyOverlayVisibility();
        _ = _vm.SearchAsync(SearchReason.Filter);
        ResetResultsScroll();
    }

    private void ClearFilters_Click(object? sender, RoutedEventArgs e)
    {
        _vm.ClearFilters();
        ResetResultsScroll();
    }

    private void ToggleFilters_Click(object? sender, RoutedEventArgs e) => _vm.IsFiltersCollapsed = !_vm.IsFiltersCollapsed;

    private void FilterHeader_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 触发按钮自身走 Click；其余区域点整行切换，避免重复触发
        if (e.Source is Button) return;
        _vm.IsFiltersCollapsed = !_vm.IsFiltersCollapsed;
    }

    // ---------------- 分页 / 每页条数 ----------------

    private void PageSizeOption_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not int size) return;
        _vm.SetPageSize(size);
        PageSizeButton.Flyout?.Hide();
        // 移动筛选面板里的同名触发器位于 ControlTemplate 中，按视觉祖先关闭其 Popup
        foreach (var popup in b.GetVisualAncestors().OfType<Popup>()) popup.IsOpen = false;
        ResetResultsScroll();
    }

    private async void PrevPage_Click(object? sender, RoutedEventArgs e)
    {
        if (!_vm.CanGoPrevious) return;
        await _vm.GoToPageAsync(_vm.Page - 1);
        ResetResultsScroll();
    }

    private async void NextPage_Click(object? sender, RoutedEventArgs e)
    {
        if (!_vm.CanGoNext) return;
        await _vm.GoToPageAsync(_vm.Page + 1);
        ResetResultsScroll();
    }

    private void LoadMore_Click(object? sender, RoutedEventArgs e)
    {
        if (!_vm.CanLoadMore) return;
        _ = _vm.LoadMoreAsync(); // append，不清空已加载卡片
    }

    // ---------------- 设置 / 主题 ----------------

    private void ThemeButton_Click(object? sender, RoutedEventArgs e) => _vm.ToggleTheme();

    private void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        _vm.OpenSettings();
        ApplyOverlayVisibility();
    }

    private void OpenSettingsFromError_Click(object? sender, RoutedEventArgs e)
    {
        _vm.OpenSettings();
        ApplyOverlayVisibility();
        Dispatcher.UIThread.Post(() =>
        {
            _settingsUrlBox?.Focus();
            _settingsUrlBox?.SelectAll();
        });
    }

    private void CloseSettings_Click(object? sender, RoutedEventArgs e)
    {
        _vm.CommitSettingsUrl();
        _vm.CloseSettings();
        ApplyOverlayVisibility();
    }

    private void SettingsUrlBox_Loaded(object? sender, RoutedEventArgs e) => _settingsUrlBox = sender as TextBox;

    private void SettingsUrl_LostFocus(object? sender, RoutedEventArgs e) => _vm.CommitSettingsUrl();

    private void ResetBackend_Click(object? sender, RoutedEventArgs e)
    {
        _vm.ResetBackend();
        Dispatcher.UIThread.Post(() => _settingsUrlBox?.Focus());
    }

    private async void TestConnection_Click(object? sender, RoutedEventArgs e)
    {
        var ok = await _vm.TestConnectionAsync();
        if (!ok) return; // 失败保留输入，内联展示标准化原因
        _vm.ShowToast("连接成功，设置已保存", ToastKind.Success);
        Dispatcher.UIThread.Post(() => _ = ReloadAfterSettingsAsync());
    }

    /// <summary>设置成功后短暂展示内联成功态，再关闭并重跑库/筛选/搜索。</summary>
    private async Task ReloadAfterSettingsAsync()
    {
        await Task.Delay(700);
        _vm.CloseSettings();
        ApplyOverlayVisibility();
        await _vm.LoadLibrariesAsync();
        await _vm.LoadFiltersAsync();
        await _vm.SearchAsync();
    }

    // ---------------- 行内交互 ----------------

    private void ToggleDetail_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is LinkCardViewModel card && card.HasDetail) card.ToggleDetail();
    }

    /// <summary>标题单击打开：表格行没有「选中」语义，标题即入口。</summary>
    private void OpenTitle_Tapped(object? sender, TappedEventArgs e)
    {
        if ((sender as TextBlock)?.DataContext is not LinkCardViewModel card) return;
        e.Handled = true;
        _vm.TryOpenLink(card);
    }

    // ---------------- 工具栏 ----------------

    private void Refresh_Click(object? sender, RoutedEventArgs e) => _ = _vm.SearchAsync(SearchReason.FirstLoad);

    /// <summary>复制当前页全部链接（每行一个，按当前页顺序）。</summary>
    private async void CopyPage_Click(object? sender, RoutedEventArgs e)
    {
        var urls = _vm.Cards
            .Select(c => c.Link.Urls.FirstOrDefault(MainWindowViewModel.IsHttpUrl) ?? c.Link.Url)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToList();
        if (urls.Count == 0)
        {
            _vm.ShowToast("当前页没有可复制的链接", ToastKind.Warning);
            return;
        }
        if (await CopyToClipboardAsync(string.Join("\n", urls)))
            _vm.ShowToast($"已复制 {urls.Count} 条链接", ToastKind.Success);
    }

    /// <summary>侧栏「筛选」段：桌面/平板展开筛选行，按钮档改开筛选面板。</summary>
    private void RailFilterRow_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentFilterTier == FilterTier.Button)
        {
            _vm.IsFilterPanelOpen = true;
            ApplyOverlayVisibility();
            return;
        }
        _vm.IsFiltersCollapsed = false;
        ResetResultsScroll();
    }

    private async void CopyUrl_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not LinkCardViewModel card) return;
        var url = card.Link.Urls.FirstOrDefault(MainWindowViewModel.IsHttpUrl) ?? card.Link.Url;
        if (await CopyToClipboardAsync(url))
            _vm.ShowToast("链接已复制", ToastKind.Success);
    }

    private async void CopyCode_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not LinkCardViewModel card) return;
        var code = card.Link.ExtractCode;
        if (string.IsNullOrWhiteSpace(code)) return;
        if (await CopyToClipboardAsync(code!))
            _vm.ShowToast("提取码已复制", ToastKind.Success);
    }

    private async Task<bool> CopyToClipboardAsync(string text)
    {
        try
        {
            var clipboard = Clipboard; // TopLevel.Clipboard（Avalonia 12）
            if (clipboard is null)
            {
                _vm.ShowToast("当前平台不支持剪贴板", ToastKind.Warning);
                return false;
            }
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch
        {
            _vm.ShowToast("复制失败，请重试", ToastKind.Error);
            return false;
        }
    }

    private void OpenLink_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is LinkCardViewModel card) _vm.TryOpenLink(card);
    }

    /// <summary>③网址行的指针快捷入口：只绑网址 TextBlock，不绑整卡，避免点空白/摘要误开。</summary>
    private void OpenLink_Tapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        if ((sender as TextBlock)?.DataContext is LinkCardViewModel card) _vm.TryOpenLink(card);
    }

    private void OpenSubLink_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string url) _vm.TryOpenUrl(url);
    }
}
