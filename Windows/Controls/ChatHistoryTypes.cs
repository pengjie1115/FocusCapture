using System.ComponentModel;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;

namespace FocusCapture.Windows.Controls;

// 2026-09-24 从已删除的 Windows/Controls/HistoryDrawer.xaml.cs（旧抽屉死代码，用户拍板删除）迁出：
// 下列三个类型是 ChatSidebar / AIDialogWindow / ChatTrashWindow / ChatExportService 正在使用的活基础设施，
// 删死文件时必须整体搬出来。原文件里的 GroupHeaderViewModel / ChatBatchAction / NonEmptyToVisibilityConverter
// 全仓零引用，随死文件一并退场（没有迁）。

/// <summary>历史会话列表项 ViewModel（列表展示用，FilePath 用于宿主还原完整会话；Id 为主键）。
/// 实现 INPC 仅用于多选模式切换（IsSelected / IsBatchMode），业务字段（Id/Title/Pinned/GroupId）只读。</summary>
public class HistoryItemViewModel : INotifyPropertyChanged
{
    public string Id { get; }
    public string FilePath { get; }
    public string TimeText { get; }
    public string ModeText { get; }
    public string Title { get; }
    public bool Pinned { get; }
    public string GroupId { get; }
    public string GroupName { get; }
    public string Preview { get; }

    private bool _isSelected;
    /// <summary>多选模式下的勾选状态</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Fire(nameof(IsSelected)); } }
    }

    private bool _isBatchMode;
    /// <summary>列表是否处于多选模式（决定勾选框与三点按钮的显隐、条目点击行为）</summary>
    public bool IsBatchMode
    {
        get => _isBatchMode;
        set
        {
            if (_isBatchMode == value) return;
            _isBatchMode = value;
            Fire(nameof(IsBatchMode));
            Fire(nameof(BatchCheckVis));
            Fire(nameof(MoreVis));
        }
    }

    public Visibility BatchCheckVis => _isBatchMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MoreVis => _isBatchMode ? Visibility.Collapsed : Visibility.Visible;

    public HistoryItemViewModel(string id, string filePath, DateTime savedAt, string modeText,
        string title, bool pinned, string groupId, string groupName, string preview)
    {
        Id = id;
        FilePath = filePath;
        TimeText = savedAt.ToString("MM-dd HH:mm");
        ModeText = modeText;
        Title = title ?? "";
        Pinned = pinned;
        GroupId = groupId ?? "";
        GroupName = groupName ?? "";
        Preview = string.IsNullOrEmpty(preview) ? "（空会话）" : preview;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Fire(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>条目操作（三个点菜单）：宿主负责业务（改字段 → Save 自动同步；删除经 MarkDeleted 管道）</summary>
public enum ChatItemAction
{
    BatchStart,  // 进入多选模式
    Rename,      // 重命名（改 Title）
    TogglePin,   // 置顶/取消置顶（切 Pinned）
    Group,       // 分组到…（context = 目标分组 Id；null = 未分组）
    Export,      // 导出（context = ExportFormat 名称）
    Delete,      // 删除（移入会话回收站）
    Favorite,    // 收藏 / 取消收藏（GroupId ↔ FavoriteId 互斥切换，2026-09-23 新增）
}

/// <summary>ExplainMode 的界面文案（侧边栏与对话窗口共用，避免两处漂移）</summary>
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
