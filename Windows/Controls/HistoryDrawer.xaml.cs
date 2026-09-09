using System.Collections.ObjectModel;
using System.ComponentModel;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;

namespace FocusCapture.Windows.Controls;

/// <summary>历史会话列表项 ViewModel（抽屉展示用，FilePath 用于宿主还原完整会话；Id 为主键）。
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
    /// <summary>抽屉是否处于多选模式（决定勾选框与三点按钮的显隐、条目点击行为）</summary>
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

/// <summary>分组分区头（列表分区展示用：置顶区 / 各分组 / 未分组）</summary>
public class GroupHeaderViewModel
{
    public string Name { get; }
    public GroupHeaderViewModel(string name) => Name = name;
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
}

/// <summary>批量操作（多选操作条）</summary>
public enum ChatBatchAction
{
    Delete,  // 批量删除（context = null）
    Group,   // 批量移动分组（context = 目标分组 Id；null = 未分组）
    Export,  // 批量导出（context = ExportFormat 名称）
}

/// <summary>
/// AI 对话历史抽屉控件：列表展示（置顶标记/分组分区）+ 三个点菜单 + 多选模式。
/// 与宿主解耦：数据由宿主经 Load() 注入；改字段/删除/导出等业务经 ItemAction/BatchAction 抛宿主执行，
/// 本控件不直接改会话文件（分组清单读写与分组选择菜单除外，属列表组织必需）。
/// </summary>
public partial class HistoryDrawer : UserControl
{
    /// <summary>扁平列表（GroupHeaderViewModel 与 HistoryItemViewModel 混排，隐式 DataTemplate 区分）</summary>
    public ObservableCollection<object> Items { get; } = new();

    /// <summary>点击某条历史会话（普通模式点击条目）</summary>
    public event Action<HistoryItemViewModel>? SessionSelected;

    /// <summary>条目操作（context：Group=分组 Id、Export=格式名，其余 null）</summary>
    public event Action<HistoryItemViewModel, ChatItemAction, string?>? ItemAction;

    /// <summary>批量操作（selected=当前勾选；context 同上）</summary>
    public event Action<ChatBatchAction, IReadOnlyList<HistoryItemViewModel>, string?>? BatchAction;

    /// <summary>打开分组管理窗口</summary>
    public event Action? GroupsManageRequested;

    /// <summary>打开会话回收站</summary>
    public event Action? RecycleBinRequested;

    /// <summary>收起抽屉（宿主复用宽度动画）</summary>
    public event Action? CollapseRequested;

    private IReadOnlyList<HistoryItemViewModel> _flatItems = new List<HistoryItemViewModel>();
    private bool _isBatchMode;

    public HistoryDrawer()
    {
        InitializeComponent();
        SessionList.ItemsSource = Items;
    }

    /// <summary>用会话摘要刷新列表。排序（置顶优先 → SavedAt 倒序）由 ChatSessionService.ListSessions 保证，
    /// 这里按"置顶区 → 各分组 → 未分组"分区展示；分组内时间倒序。</summary>
    public void Load(IEnumerable<SessionSummary> summaries, IReadOnlyList<ChatGroup> groups)
    {
        var groupNameById = groups.GroupBy(g => g.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        var items = summaries.Select(s => new HistoryItemViewModel(
            s.Id, s.FilePath, s.SavedAt, AiModeText.Get(s.Mode),
            s.Title, s.Pinned, s.GroupId,
            groupNameById.TryGetValue(s.GroupId, out var n) ? n : "",
            s.Preview)).ToList();
        _flatItems = items;

        RebuildSections();

        if (_isBatchMode && Items.Count > 0) BatchBar.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>分区重建：置顶区 → 各分组（按清单顺序）→ 未分组；条目已在 Load 中按时间倒序。</summary>
    private void RebuildSections()
    {
        Items.Clear();
        var pinned = new List<HistoryItemViewModel>();
        var byGroup = new Dictionary<string, List<HistoryItemViewModel>>(StringComparer.Ordinal);
        var ungrouped = new List<HistoryItemViewModel>();

        foreach (var item in _flatItems)
        {
            if (item.Pinned) { pinned.Add(item); continue; }
            if (string.IsNullOrEmpty(item.GroupId)) ungrouped.Add(item);
            else
            {
                if (!byGroup.TryGetValue(item.GroupId, out var list))
                    byGroup[item.GroupId] = list = new List<HistoryItemViewModel>();
                list.Add(item);
            }
        }

        if (pinned.Count > 0)
        {
            Items.Add(new GroupHeaderViewModel("置顶"));
            foreach (var i in pinned) Items.Add(i);
        }

        // 分组顺序按 chat_groups.json 清单顺序（Load 的 groups 顺序），清单外的悬空分组兜底追加
        var groupList = ChatGroupStore.Load();
        var orderedIds = groupList.Select(g => g.Id)
            .Concat(byGroup.Keys.Where(id => groupList.All(g => g.Id != id)));
        foreach (var gid in orderedIds)
        {
            if (!byGroup.TryGetValue(gid, out var list)) continue;
            var name = groupList.FirstOrDefault(g => g.Id == gid)?.Name ?? "";
            Items.Add(new GroupHeaderViewModel(string.IsNullOrEmpty(name) ? "（未知分组）" : name));
            foreach (var i in list) Items.Add(i);
        }

        if (ungrouped.Count > 0)
        {
            // 无任何分组时未分组区不加头（等同旧版平铺观感）
            if (Items.Count > 0) Items.Add(new GroupHeaderViewModel("未分组"));
            foreach (var i in ungrouped) Items.Add(i);
        }

        foreach (var i in _flatItems) i.IsBatchMode = _isBatchMode;
    }

    /// <summary>进入多选模式（三个点菜单"批量操作"）</summary>
    public void EnterBatchMode()
    {
        _isBatchMode = true;
        foreach (var i in _flatItems) { i.IsBatchMode = true; i.IsSelected = false; }
        BatchBar.Visibility = Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>退出多选模式（批量操作结束 / 宿主操作完成）</summary>
    public void ExitBatchMode()
    {
        _isBatchMode = false;
        foreach (var i in _flatItems) { i.IsBatchMode = false; i.IsSelected = false; }
        BatchBar.Visibility = Visibility.Collapsed;
    }

    public IReadOnlyList<HistoryItemViewModel> SelectedItems =>
        _flatItems.Where(i => i.IsSelected).ToList();

    // ── 列表交互 ──

    private void Item_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HistoryItemViewModel item }) return;
        if (_isBatchMode)
        {
            item.IsSelected = !item.IsSelected;
            return;
        }
        SessionSelected?.Invoke(item);
    }

    /// <summary>条目悬停高亮（多选模式下不变色，突出勾选状态）</summary>
    private void Item_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border b && !_isBatchMode) b.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    }

    private void Item_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border b) b.Background = Brushes.Transparent;
    }

    // ── 三个点菜单 ──

    private void More_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: HistoryItemViewModel item } btn) return;

        var menu = new ContextMenu();
        menu.Items.Add(MakeItem(item, ChatItemAction.BatchStart, "批量操作"));
        menu.Items.Add(MakeItem(item, ChatItemAction.Rename, "重命名"));
        menu.Items.Add(MakeItem(item, ChatItemAction.TogglePin, item.Pinned ? "取消置顶" : "置顶"));

        var groupMenu = MakeItem(item, ChatItemAction.Group, "分组到…");
        BuildGroupSubmenu(groupMenu, item.GroupId, gid => ItemAction?.Invoke(item, ChatItemAction.Group, gid));
        menu.Items.Add(groupMenu);

        var exportMenu = MakeItem(item, ChatItemAction.Export, "导出");
        foreach (var fmt in ExportFormatList())
        {
            var mi = new MenuItem { Header = fmt.Name, Tag = fmt.Format };
            mi.Click += (_, _) => ItemAction?.Invoke(item, ChatItemAction.Export, fmt.Format.ToString());
            exportMenu.Items.Add(mi);
        }
        menu.Items.Add(exportMenu);

        menu.Items.Add(MakeItem(item, ChatItemAction.Delete, "删除会话"));

        menu.PlacementTarget = btn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private MenuItem MakeItem(HistoryItemViewModel item, ChatItemAction action, string header)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => ItemAction?.Invoke(item, action, null);
        return mi;
    }

    /// <summary>分组子菜单：未分组 + 各分组（当前所在分组打勾）+ 新建分组…。parent 兼容 MenuItem / ContextMenu。</summary>
    private void BuildGroupSubmenu(ItemsControl parent, string currentGroupId, Action<string?> onPicked)
    {
        var none = new MenuItem { Header = "未分组", IsChecked = string.IsNullOrEmpty(currentGroupId) };
        none.Click += (_, _) => onPicked(null);
        parent.Items.Add(none);

        foreach (var g in ChatGroupStore.Load())
        {
            var mi = new MenuItem { Header = g.Name, IsChecked = g.Id == currentGroupId };
            mi.Click += (_, _) => onPicked(g.Id);
            parent.Items.Add(mi);
        }

        var create = new MenuItem { Header = "新建分组…" };
        create.Click += (_, _) =>
        {
            var name = PromptDialog.Show(Window.GetWindow(this), "新建分组", "分组名称：");
            if (string.IsNullOrWhiteSpace(name)) return;
            onPicked(CreateGroup(name));
        };
        parent.Items.Add(create);
    }

    /// <summary>新建分组并落盘（查重：同名直接返回已有分组 Id）</summary>
    private string CreateGroup(string name)
    {
        var groups = ChatGroupStore.Load();
        var exist = groups.FirstOrDefault(g => g.Name == name);
        if (exist != null) return exist.Id;
        var group = ChatGroupStore.Create(name);
        groups.Add(group);
        ChatGroupStore.Save(groups);
        return group.Id;
    }

    private static (string Name, ExportFormat Format)[] ExportFormatList() =>
    [
        ("Markdown", ExportFormat.Markdown),
        ("TXT", ExportFormat.Txt),
        ("JSON", ExportFormat.Json),
        ("Word", ExportFormat.Word),
    ];

    // ── 批量操作条 ──

    private void BatchSelectAll_Click(object sender, RoutedEventArgs e)
    {
        var anyUnselected = _flatItems.Any(i => !i.IsSelected);
        foreach (var i in _flatItems) i.IsSelected = anyUnselected; // 全选中时再点 = 全不选
    }

    private void BatchDelete_Click(object sender, RoutedEventArgs e) =>
        BatchAction?.Invoke(ChatBatchAction.Delete, SelectedItems, null);

    private void BatchGroup_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        BuildGroupSubmenu(menu, "", gid => BatchAction?.Invoke(ChatBatchAction.Group, SelectedItems, gid));
        menu.PlacementTarget = (Button)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void BatchExport_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var fmt in ExportFormatList())
        {
            var mi = new MenuItem { Header = fmt.Name };
            mi.Click += (_, _) => BatchAction?.Invoke(ChatBatchAction.Export, SelectedItems, fmt.Format.ToString());
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = (Button)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void BatchExit_Click(object sender, RoutedEventArgs e) => ExitBatchMode();

    // ── 头部按钮 ──

    private void BtnGroups_Click(object sender, RoutedEventArgs e) => GroupsManageRequested?.Invoke();

    private void BtnRecycleBin_Click(object sender, RoutedEventArgs e) => RecycleBinRequested?.Invoke();

    /// <summary>收起抽屉（头部按钮，宿主复用动画）——按钮随「布局改造」提交接入 XAML</summary>
    private void BtnCollapse_Click(object sender, RoutedEventArgs e) => CollapseRequested?.Invoke();
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

/// <summary>字符串非空显示、空折叠（列表条目分组名等可选标注用）</summary>
public class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
