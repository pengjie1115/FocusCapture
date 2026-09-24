using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Threading;
using FocusCapture.Services;

namespace FocusCapture.Windows;

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
/// 全局搜索弹窗（2026-09-24）：跨所有会话搜消息正文，点结果跳转会话对应位置。
///
/// 为什么是独立弹窗（用户拍板，而非侧边栏列表区 / 主区覆盖面板）：
/// 点结果直接跳转到会话对应位置 + 搜索词短暂高亮，独立窗体不挤占对话区、关掉即走。
///
/// 防抖 280ms（与组内搜索同口径）：每敲一个字重启计时，停止 280ms 后才搜，避免每键扫一遍磁盘。
/// 搜索在后台线程（ChatSearchService.Search 同步阻塞 + 磁盘 IO），结果经 Dispatcher 回 UI。
/// </summary>
public partial class ChatSearchWindow : Window
{
    private readonly AIDialogWindow _owner;
    private readonly DispatcherTimer _debounce;
    private string _lastQuery = "";

    public ObservableCollection<SearchResultVm> Results { get; } = new();

    public ChatSearchWindow(AIDialogWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        ResultList.ItemsSource = Results;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = DoSearchAsync(); };
        // 弹出即聚焦搜索框（WPF 非模态窗不自动聚焦任何控件）
        Loaded += (_, _) => SearchBox.Focus();
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
            EmptyHint.Visibility = Visibility.Visible;
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

        Results.Clear();
        foreach (var h in hits)
            Results.Add(new SearchResultVm(h));

        EmptyHint.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchingHint.Visibility = Visibility.Collapsed;
    }

    private void ResultRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SearchResultVm vm }) return;
        _owner.OpenSessionFromSearch(vm.FilePath, _lastQuery);
        Close();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
