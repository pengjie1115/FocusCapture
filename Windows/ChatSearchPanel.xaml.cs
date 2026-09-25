using System.Collections.ObjectModel;
using System.Windows.Threading;
using FocusCapture.Services;

namespace FocusCapture.Windows;

/// <summary>最近会话条目视图（面板第③区未输入关键词时显示，最多 100 条，点击直达会话）。</summary>
public sealed class RecentSessionVm
{
    public string Title { get; }
    public string TimeText { get; }
    public string FilePath { get; }

    public RecentSessionVm(SessionSummary session)
    {
        // 标题回退：Title 空 → 首条用户消息预览；再空 → 占位文案
        Title = string.IsNullOrWhiteSpace(session.Title)
            ? (string.IsNullOrWhiteSpace(session.Preview) ? "无标题会话" : session.Preview)
            : session.Title;
        TimeText = session.SavedAt.ToString("MM-dd HH:mm");
        FilePath = session.FilePath;
    }
}

/// <summary>全局搜索结果条目视图（供 XAML 绑定）。</summary>
public sealed class SearchResultVm
{
    public string Title { get; }
    public string TimeText { get; }
    public string Snippet { get; }
    public string FilePath { get; }
    public string SessionId { get; }

    public SearchResultVm(ChatSearchHit hit)
    {
        Title = string.IsNullOrWhiteSpace(hit.Title) ? hit.Preview : hit.Title;
        TimeText = hit.SavedAt.ToString("MM-dd HH:mm");
        Snippet = hit.Snippet;
        FilePath = hit.FilePath;
        SessionId = hit.SessionId;
    }
}

/// <summary>
/// 全局搜索面板（2026-09-25）：原独立窗体 ChatSearchWindow 改造为 AIDialogWindow 内的居中覆盖层。
///
/// 为什么搬进主窗（用户拍板）：面板要居中浮在 AI 问答窗口正中、面板以外区域毛玻璃模糊 ——
/// 独立窗体做不到模糊宿主内容，覆盖层 + 主内容 BlurEffect 才行。独立窗体不挤占对话区、关掉即走的
/// 原有优点由「点面板外/Esc/× 即关」保持。
///
/// 三段结构：头部（历史对话 + ×）/ 搜索框 / 列表区两态：
/// 未输入 = 最近会话列表（ListSessions 前 100 条，点击直达会话）；输入后 = 全文搜索结果（防抖 280ms）。
/// 搜索在后台线程（ChatSearchService.Search 同步阻塞 + 磁盘 IO），结果经 Dispatcher 回 UI。
/// </summary>
public partial class ChatSearchPanel : UserControl
{
    /// <summary>最近会话保留条数上限（用户拍板：最多 100 条）。</summary>
    public const int RecentLimit = 100;

    private readonly DispatcherTimer _debounce;
    private string _lastQuery = "";
    private AIDialogWindow _owner = null!;

    /// <summary>宿主窗口（XAML 常驻实例化走默认构造，owner 由 AIDialogWindow 构造时注入）。</summary>
    public AIDialogWindow OwnerWindow { set => _owner = value; }

    public ObservableCollection<SearchResultVm> Results { get; } = new();
    public ObservableCollection<RecentSessionVm> Recents { get; } = new();

    /// <summary>XAML 直接实例化必须留默认构造；此时 owner 未定，回调方法在 owner 注入前不会被触发。</summary>
    public ChatSearchPanel()
    {
        InitializeComponent();
        RecentList.ItemsSource = Recents;
        ResultList.ItemsSource = Results;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = DoSearchAsync(); };
    }

    /// <summary>面板每次打开时刷新最近会话（新会话/改名后不至于显示旧数据）。</summary>
    public void ReloadRecents()
    {
        Recents.Clear();
        foreach (var vm in BuildRecentList(ChatSessionService.ListSessions()))
            Recents.Add(vm);
        ShowRecentsView();
    }

    /// <summary>重新打开面板时清空上次的输入，回到最近会话视图。</summary>
    public void ResetInput()
    {
        SearchBox.Text = "";
        _lastQuery = "";
        _debounce.Stop();
        Results.Clear();
        ShowRecentsView();
    }

    public void FocusSearchBox() => SearchBox.Focus();

    /// <summary>最近会话列表构建：置顶优先 → 时间倒序，截前 RecentLimit 条。
    /// 抽成纯静态函数（不触碰 UI 控件）供检查点直接断言 —— 排序口径与 ListSessions 一致，双保险。</summary>
    public static List<RecentSessionVm> BuildRecentList(IEnumerable<SessionSummary> sessions) =>
        sessions.OrderByDescending(s => s.Pinned).ThenByDescending(s => s.SavedAt)
                .Take(RecentLimit)
                .Select(s => new RecentSessionVm(s))
                .ToList();

    private void ShowRecentsView()
    {
        RecentList.Visibility = Visibility.Visible;
        ResultList.Visibility = Visibility.Collapsed;
        EmptyHint.Text = "暂无历史会话";
        EmptyHint.Visibility = Recents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchingHint.Visibility = Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        Placeholder.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _lastQuery = q;
        _debounce.Stop();
        if (q.Length == 0)
        {
            Results.Clear();
            ShowRecentsView();
            return;
        }
        _debounce.Start();
    }

    private async Task DoSearchAsync()
    {
        var q = _lastQuery;
        if (q.Length == 0) return;

        SearchingHint.Visibility = Visibility.Visible;
        // 铁律：ChatSearchService.Search 同步阻塞 + 磁盘 IO，必须在后台线程，否则卡住 UI 消息循环
        var hits = await Task.Run(() => ChatSearchService.Search(q, null));

        RecentList.Visibility = Visibility.Collapsed;
        ResultList.Visibility = Visibility.Visible;
        Results.Clear();
        foreach (var h in hits)
            Results.Add(new SearchResultVm(h));

        EmptyHint.Text = "未找到相关消息";
        EmptyHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchingHint.Visibility = Visibility.Collapsed;
    }

    private void RecentRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RecentSessionVm vm }) return;
        _owner.OpenSessionFromSearchPanel(vm.FilePath, query: null);
    }

    private void ResultRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SearchResultVm vm }) return;
        _owner.OpenSessionFromSearchPanel(vm.FilePath, _lastQuery);
    }

    private void BtnClose_Click(object sender, MouseButtonEventArgs e) => _owner.CloseSearchOverlay();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) _owner.CloseSearchOverlay();
    }
}
