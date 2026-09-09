using System.Collections.ObjectModel;
using FocusCapture.Services;
using FocusCapture.Services.AI;

namespace FocusCapture.Windows.Controls;

/// <summary>历史会话列表项 ViewModel（抽屉展示用，FilePath 用于宿主还原完整会话；Id 为主键）。
/// Title/Pinned/GroupId 阶段一仅透传（预览仍用首条用户消息），置顶/标题/分组的展示逻辑阶段二实现。</summary>
public class HistoryItemViewModel
{
    public string Id { get; }
    public string FilePath { get; }
    public string TimeText { get; }
    public string ModeText { get; }
    public string Title { get; }
    public bool Pinned { get; }
    public string GroupId { get; }
    public string Preview { get; }

    public HistoryItemViewModel(string id, string filePath, DateTime savedAt, string modeText,
        string title, bool pinned, string groupId, string preview)
    {
        Id = id;
        FilePath = filePath;
        TimeText = savedAt.ToString("MM-dd HH:mm");
        ModeText = modeText;
        Title = title ?? "";
        Pinned = pinned;
        GroupId = groupId ?? "";
        Preview = string.IsNullOrEmpty(preview) ? "（空会话）" : preview;
    }
}

/// <summary>
/// AI 对话历史抽屉控件：扫描 chat_history 目录展示会话摘要列表，点击条目经 SessionSelected 通知宿主。
/// 与宿主解耦：数据由宿主经 Load() 注入，宿主自定展开方式（宽度动画/常驻/独立窗口均可）。
/// </summary>
public partial class HistoryDrawer : UserControl
{
    public ObservableCollection<HistoryItemViewModel> Items { get; } = new();

    /// <summary>点击某条历史会话</summary>
    public event Action<HistoryItemViewModel>? SessionSelected;

    public HistoryDrawer()
    {
        InitializeComponent();
        SessionList.ItemsSource = Items;
    }

    /// <summary>用会话摘要刷新列表（最新在前，由 ChatSessionService.ListSessions 保证）</summary>
    public void Load(IEnumerable<SessionSummary> summaries)
    {
        Items.Clear();
        foreach (var s in summaries)
            Items.Add(new HistoryItemViewModel(s.Id, s.FilePath, s.SavedAt, AiModeText.Get(s.Mode),
                s.Title, s.Pinned, s.GroupId, s.Preview));
        EmptyHint.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Item_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HistoryItemViewModel item })
            SessionSelected?.Invoke(item);
    }
}

/// <summary>ExplainMode 的界面文案（抽屉与对话窗口共用，避免两处漂移）</summary>
public static class AiModeText
{
    public static string Get(string mode) =>
        Enum.TryParse<ExplainMode>(mode, out var m) ? Get(m) : "未知";

    public static string Get(ExplainMode mode) => mode switch
    {
        ExplainMode.Translate => "翻译",
        ExplainMode.Search => "搜索",
        _ => "问答",
    };
}
