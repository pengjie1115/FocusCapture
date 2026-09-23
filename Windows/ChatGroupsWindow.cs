using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FocusCapture.Services;
using FocusCapture.Windows.Controls;

namespace FocusCapture.Windows;

/// <summary>
/// 会话分组管理窗口（代码构造，无 XAML）：新建 / 重命名 / 删除分组。
/// 删除分组 = 该组会话全部回到未分组（逐个 Load → GroupId="" → Save，Rev 自增走同步管道），分组记录从清单删除。
/// 分组清单改动本身经 ChatGroupStore 落盘，由 ChatSyncEngine 下一周期上传（ChatGroupStore 无 Notify 钩子，
/// 会话改动的 Save() 会触发 NotifyLocalChange，故组内非空时即已触发；空分组随下次任意周期同步）。
/// </summary>
public class ChatGroupsWindow : Window
{
    private readonly StackPanel _listPanel = new();

    public ChatGroupsWindow()
    {
        Title = "会话分组管理";
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        FontSize = 13;

        var newBtn = MakeLinkButton("＋ 新建分组", NewGroup_Click);
        var closeBtn = MakeLinkButton("关闭", (_, _) => Close());

        var bottom = new DockPanel { Margin = new Thickness(16, 8, 16, 14) };
        DockPanel.SetDock(newBtn, Dock.Left);
        DockPanel.SetDock(closeBtn, Dock.Right);
        bottom.Children.Add(newBtn);
        bottom.Children.Add(closeBtn);

        var root = new StackPanel();
        root.Children.Add(_listPanel);
        root.Children.Add(new System.Windows.Controls.Separator
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Margin = new Thickness(16, 8, 16, 0),
        });
        root.Children.Add(bottom);
        Content = root;

        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        _listPanel.Children.Clear();
        // 收藏是系统保留分区（2026-09-23）：不出现在分组管理窗里，也不允许重命名 / 删除
        var groups = ChatGroupStore.Load().Where(g => !ChatGroupStore.IsFavorite(g.Id)).ToList();
        if (groups.Count == 0)
        {
            _listPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "还没有分组。通过会话条目「⋮ → 分组到… → 新建分组」或下方按钮创建。",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 12,
                Margin = new Thickness(16, 14, 16, 4),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        _listPanel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "删除分组时组内会话回到未分组（不删除会话）。",
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
            FontSize = 11,
            Margin = new Thickness(16, 12, 16, 2),
        });

        foreach (var g in groups)
        {
            var row = new DockPanel { Margin = new Thickness(16, 6, 16, 0) };
            var name = new System.Windows.Controls.TextBlock
            {
                Text = g.Name,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var renameBtn = MakeLinkButton("重命名", (_, _) => RenameGroup(g));
            var delBtn = MakeLinkButton("删除", (_, _) => DeleteGroup(g));
            DockPanel.SetDock(delBtn, Dock.Right);
            DockPanel.SetDock(renameBtn, Dock.Right);
            row.Children.Add(name);
            row.Children.Add(delBtn);
            row.Children.Add(renameBtn);
            _listPanel.Children.Add(row);
        }
    }

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptDialog.Show(this, "新建分组", "分组名称：");
        if (string.IsNullOrWhiteSpace(name)) return;

        // 2026-09-23：查重与落盘统一走 ChatGroupService —— 原先窗口与历史抽屉各写一套，
        // 撞名行为还不一致（这里拒绝、抽屉那边静默复用），现在只有一处定义
        ChatGroupService.Create(name, out var result);
        if (result == ChatGroupService.CreateResult.NameExists)
        {
            System.Windows.MessageBox.Show(this, "已存在同名分组", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Reload();
    }

    private void RenameGroup(ChatGroup group)
    {
        var name = PromptDialog.Show(this, "重命名分组", "新名称：", group.Name);
        if (string.IsNullOrWhiteSpace(name) || name == group.Name) return;

        if (!ChatGroupService.Rename(group.Id, name, out var error))
        {
            if (error.Length > 0)
            {
                System.Windows.MessageBox.Show(this, error, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }
        Reload();
    }

    private void DeleteGroup(ChatGroup group)
    {
        if (System.Windows.MessageBox.Show(this,
                $"删除分组「{group.Name}」？组内会话将回到未分组（会话本身不删除）。",
                "删除分组", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        // 2026-09-23 顺序修正：先清组内会话的分组引用、再删分组记录 —— 原先反着来，
        // 中途失败会留下「分组没了但会话还挂着失效 GroupId」，界面上冒出一个「（未知分组）」幽灵分区。
        // 现在整个顺序由 ChatGroupService 保证。
        ChatGroupService.DeleteGroup(group.Id, out _);
        Reload();
    }

    private static Button MakeLinkButton(string text, RoutedEventHandler onClick) => new Button
    {
        Content = text,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x9E, 0x7A)),
        Padding = new Thickness(6, 2, 6, 2),
        Cursor = System.Windows.Input.Cursors.Hand,
    }.Also(b => b.Click += onClick);
}

/// <summary>小工具：链式执行副作用后返回原值（MakeLinkButton 单表达式构造用）</summary>
internal static class PromptDialogExtensions
{
    public static T Also<T>(this T obj, Action<T> act)
    {
        act(obj);
        return obj;
    }
}
