using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using FocusCapture.Models;
using FocusCapture.Services;

namespace FocusCapture.Windows.Controls;

/// <summary>侧边栏的纯分区标题（置顶 / 最近）—— 不可点击，只是把列表分组。</summary>
public sealed class SidebarSectionHeader
{
    public string Name { get; }
    public SidebarSectionHeader(string name) => Name = name;
}

/// <summary>
/// 侧边栏的可点击分组行。收藏是内置保留分区，也走这一支（靠 IsFavorite 区分）。
/// 认的始终是 <see cref="GroupId"/> 而不是名字 —— 用户可以把任意分组改名叫「收藏」。
/// </summary>
public sealed class SidebarGroupHeader
{
    public string Name { get; }
    public string GroupId { get; }
    public bool IsFavorite { get; }

    public SidebarGroupHeader(string name, string groupId, bool isFavorite)
    {
        Name = name;
        GroupId = groupId;
        IsFavorite = isFavorite;
    }

    /// <summary>收藏带一个记号，免得与用户自己建的同名分组混淆</summary>
    public string DisplayName => IsFavorite ? "★  " + Name : Name;

    public string Tooltip => IsFavorite
        ? "收藏的对话（收藏是分组的另一种形式：一条会话要么在某分组、要么在收藏）"
        : $"进入分组「{Name}」，在里面说的指令默认都对着这个分组";
}

/// <summary>分组三点菜单的动作（宿主执行）</summary>
public enum GroupMenuAction
{
    Rename,   // 重命名
    Pin,      // 置顶此分组
    Delete,   // 删除此分组
}

/// <summary>
/// AI 问答侧边栏（2026-09-23 重构，取代历史抽屉）。
///
/// 与旧抽屉的两条关键行为差异（都是用户拍板的）：
/// ① **已分组的会话不出现在「最近」里** —— 只有点进分组才看得到。旧抽屉把所有会话平铺分区，找不到重点。
/// ② **置顶是叠加的** —— 置顶会话既在「置顶」区、也在它所属的分组里（旧实现会把它从分组里摘出去）。
///
/// 纪律同旧抽屉：数据由宿主经 <see cref="Load"/> 注入，业务动作一律抛事件给宿主执行，
/// 本控件不直接改会话文件。
/// </summary>
public partial class ChatSidebar : UserControl
{
    /// <summary>扁平列表（分区头 / 分组行 / 会话条目混排，靠隐式 DataTemplate 区分）</summary>
    public ObservableCollection<object> Items { get; } = new();

    /// <summary>点「新对话」</summary>
    public event Action? NewChatRequested;

    /// <summary>点「新分组」</summary>
    public event Action? NewGroupRequested;

    /// <summary>点某条会话</summary>
    public event Action<HistoryItemViewModel>? SessionSelected;

    /// <summary>点某个分组行（进入分组视图；收藏也走这里）</summary>
    public event Action<SidebarGroupHeader>? GroupSelected;

    /// <summary>分组三点菜单</summary>
    public event Action<SidebarGroupHeader, GroupMenuAction>? GroupAction;

    /// <summary>会话三点菜单（context：Group = 目标分组 Id、Export = 格式名，其余 null）</summary>
    public event Action<HistoryItemViewModel, ChatItemAction, string?>? SessionAction;

    public ChatSidebar()
    {
        InitializeComponent();
        ListHost.ItemsSource = Items;
    }

    /// <summary>刷新侧边栏（分区构造见 <see cref="BuildSections"/>）</summary>
    public void Load(IEnumerable<SessionSummary> sessions, IReadOnlyList<ChatGroup> groups)
    {
        var sections = BuildSections(sessions, groups);

        Items.Clear();
        foreach (var entry in sections) Items.Add(entry);
    }

    /// <summary>
    /// 分区构造（**纯逻辑，刻意抽成 static 以便被慢层检查点直接守住**）。
    /// 分区顺序：收藏 · 各分组 → 置顶 → 最近（仅未分组且未置顶）。
    ///
    /// 为什么必须有检查点：这几条规则错了都不会报错，只会表现为
    /// 「某条会话莫名从列表里消失」或「同一条在同屏出现两遍」—— 靠肉眼很难发现漏了谁。
    /// </summary>
    public static List<object> BuildSections(IEnumerable<SessionSummary> sessions, IReadOnlyList<ChatGroup> groups)
    {
        var items = sessions.Select(s => new HistoryItemViewModel(
            s.Id, s.FilePath, s.SavedAt, AiModeText.Get(s.Mode),
            // 没重命名过就用预览当标题 —— 旧抽屉靠 XAML 里的空值回落，这里在源头兜掉，
            // 免得侧边栏出现一行没有文字的条目（点了也不知道是什么）。
            string.IsNullOrWhiteSpace(s.Title) ? s.Preview : s.Title,
            s.Pinned, s.GroupId, "", s.Preview)).ToList();

        var result = new List<object>();

        // ① 收藏 + 用户建的分组（顺序跟清单走，与分组管理办法一致）
        var favorite = groups.FirstOrDefault(g => ChatGroupStore.IsFavorite(g.Id));
        if (favorite != null)
            result.Add(new SidebarGroupHeader(favorite.Name, favorite.Id, true));
        foreach (var g in groups.Where(g => !ChatGroupStore.IsFavorite(g.Id)))
            result.Add(new SidebarGroupHeader(g.Name, g.Id, false));

        // ② 置顶区（跨分组；置顶会话在所属分组里**也会**再出现一次 —— 这是用户要的叠加语义）
        var pinned = items.Where(i => i.Pinned).ToList();
        if (pinned.Count > 0)
        {
            result.Add(new SidebarSectionHeader("置顶"));
            result.AddRange(pinned);
        }

        // ③ 最近：只列「未分组 **且** 未置顶」的会话
        // 已分组的会话刻意不在这里出现（用户 2026-09-23 拍板：分组里的会话只有点进分组才看得到）；
        // 置顶的未分组会话也不重复列 —— 它已经在上面「置顶」区里了，同屏出现两遍只是噪音。
        var recent = items.Where(i => !i.Pinned && string.IsNullOrEmpty(i.GroupId)).ToList();
        if (recent.Count > 0)
        {
            result.Add(new SidebarSectionHeader("最近"));
            result.AddRange(recent);
        }

        return result;
    }

    /// <summary>底部用户区：昵称 + 头像（头像为空则用昵称首字画色块）</summary>
    public void SetUser(string? nickname, BitmapImage? avatar)
    {
        var name = string.IsNullOrWhiteSpace(nickname) ? "我" : nickname.Trim();
        UserNickname.Text = name;
        AvatarInitial.Text = name[..1].ToUpperInvariant();

        if (avatar != null)
        {
            AvatarImage.Source = avatar;
            AvatarImage.Visibility = Visibility.Visible;
            AvatarInitial.Visibility = Visibility.Collapsed;
        }
        else
        {
            AvatarImage.Source = null;
            AvatarImage.Visibility = Visibility.Collapsed;
            AvatarInitial.Visibility = Visibility.Visible;
        }
    }

    // ── 交互 ──

    private void BtnNewChat_Click(object sender, RoutedEventArgs e) => NewChatRequested?.Invoke();

    private void BtnNewGroup_Click(object sender, RoutedEventArgs e) => NewGroupRequested?.Invoke();

    private void SessionRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HistoryItemViewModel item })
            SessionSelected?.Invoke(item);
    }

    private void GroupRow_Click(object sender, MouseButtonEventArgs e)
    {
        // 三点按钮自己会 e.Handled = true，所以点它不会走到这里
        if (sender is FrameworkElement { Tag: SidebarGroupHeader group })
            GroupSelected?.Invoke(group);
    }

    private void GroupMore_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: SidebarGroupHeader group } btn) return;

        // 刻意用 new ContextMenu() 而不是对象初始化器：自带 Style 会顶掉 App.xaml 的深色模板，
        // 弹出层会变成白条（项目在待办汇总右键菜单上踩过同一个坑）。
        var menu = new ContextMenu();
        menu.Items.Add(MakeGroupMenuItem(group, GroupMenuAction.Rename, "重命名"));
        menu.Items.Add(MakeGroupMenuItem(group, GroupMenuAction.Pin, "置顶此分组"));
        menu.Items.Add(MakeGroupMenuItem(group, GroupMenuAction.Delete, "删除此分组"));

        menu.PlacementTarget = btn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private MenuItem MakeGroupMenuItem(SidebarGroupHeader group, GroupMenuAction action, string header)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => GroupAction?.Invoke(group, action);
        return item;
    }

    private static readonly (string Name, ExportFormat Format)[] ExportFormats =
    [
        ("Markdown", ExportFormat.Markdown),
        ("TXT", ExportFormat.Txt),
        ("JSON", ExportFormat.Json),
        ("Word", ExportFormat.Word),
    ];

    private void SessionMore_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: HistoryItemViewModel item } btn) return;

        var menu = new ContextMenu();
        menu.Items.Add(MakeSessionMenuItem(item, ChatItemAction.Rename, "重命名"));
        menu.Items.Add(MakeSessionMenuItem(item, ChatItemAction.TogglePin, item.Pinned ? "取消置顶" : "置顶"));

        // 收藏与分组是**互斥**的归属（用户 2026-09-23 定：收藏是分组的另一种形式），
        // 所以这里不叫「加入收藏」而叫「收藏」—— 点下去会把它从原分组移到收藏。
        menu.Items.Add(MakeSessionMenuItem(item, ChatItemAction.Favorite,
            ChatGroupStore.IsFavorite(item.GroupId) ? "取消收藏" : "收藏"));

        var groupMenu = MakeSessionMenuItem(item, ChatItemAction.Group, "分组到…");
        foreach (var g in ChatGroupStore.Load().Where(g => !ChatGroupStore.IsFavorite(g.Id)))
        {
            var menuItem = new MenuItem { Header = g.Name, IsChecked = g.Id == item.GroupId };
            var targetId = g.Id;
            menuItem.Click += (_, _) => SessionAction?.Invoke(item, ChatItemAction.Group, targetId);
            groupMenu.Items.Add(menuItem);
        }
        // 一个分组都没有时，「分组到…」点开是个空菜单 —— 不如直接禁掉
        if (groupMenu.Items.Count == 0) groupMenu.IsEnabled = false;
        menu.Items.Add(groupMenu);

        var exportMenu = MakeSessionMenuItem(item, ChatItemAction.Export, "导出");
        foreach (var (formatName, format) in ExportFormats)
        {
            var menuItem = new MenuItem { Header = formatName };
            var captured = format;
            menuItem.Click += (_, _) => SessionAction?.Invoke(item, ChatItemAction.Export, captured.ToString());
            exportMenu.Items.Add(menuItem);
        }
        menu.Items.Add(exportMenu);

        menu.Items.Add(MakeSessionMenuItem(item, ChatItemAction.Delete, "删除会话"));

        menu.PlacementTarget = btn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private MenuItem MakeSessionMenuItem(HistoryItemViewModel item, ChatItemAction action, string header)
    {
        var menuItem = new MenuItem { Header = header };
        menuItem.Click += (_, _) => SessionAction?.Invoke(item, action, null);
        return menuItem;
    }
}
