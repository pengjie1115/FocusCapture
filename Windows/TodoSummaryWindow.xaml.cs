using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FocusCapture.Models;
using FocusCapture.Services;

namespace FocusCapture.Windows;

/// <summary>
/// v3.5（Phase 3）：分组待办汇总窗（点击悬浮球角标弹出）。
/// 两组：标题「待处理」(Open，未读未办) /「已暂缓」(Read)。
/// 待处理条目操作与每日汇总一致（按钮按类型补全）；已暂缓条目：恢复提醒(Read→Open) / 标记完成(→Done)。
/// 两组都空 → 「没有待办事项」。
/// 始终有打开按钮兜底：角标/汇总靠本窗处理状态，处理完刷新列表。
/// </summary>
public partial class TodoSummaryWindow : Window
{
    private readonly NoteService _notes;
    private readonly AppSettings _settings;

    public TodoSummaryWindow(NoteService notes, AppSettings settings)
    {
        InitializeComponent();
        _notes = notes;
        _settings = settings;
    }

    /// <summary>载入并分组展示所有未办待办。items 为空时显示空态。</summary>
    public void RefreshAll(List<NoteEntry>? allItems)
    {
        var all = allItems ?? new List<NoteEntry>();
        var pending = all.Where(e => e.Type == NoteType.Todo && e.TodoStatus == TodoStatus.Open).ToList();
        var read = all.Where(e => e.Type == NoteType.Todo && e.TodoStatus == TodoStatus.Read).ToList();

        PendingList.Children.Clear();
        ReadList.Children.Clear();

        EmptyText.Visibility = (pending.Count == 0 && read.Count == 0)
            ? Visibility.Visible : Visibility.Collapsed;

        PendingHeaderText.Visibility = pending.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ReadHeaderText.Visibility = read.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var e in pending) PendingList.Children.Add(BuildPendingRow(e));
        foreach (var e in read) ReadList.Children.Add(BuildReadRow(e));
    }

    /// <summary>待处理行：按钮按类型补全（同每日汇总）。</summary>
    private UIElement BuildPendingRow(NoteEntry e)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(10, 8, 10, 8)
        };
        var contentText = new TextBlock
        {
            Text = e.EditedContent ?? e.Content,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 320
        };
        var timeText = new TextBlock
        {
            Text = e.DueTime.HasValue ? $"提醒时间:{e.DueTime:yyyy-MM-dd HH:mm}" : "无提醒时间",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11, Margin = new Thickness(0, 2, 0, 6)
        };

        var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnDone = MakeButton("已完成", Color.FromRgb(0x4C, 0xAF, 0x50));
        var btnRight = e.DueTime.HasValue
            ? MakeButton("顺延到明天", Color.FromRgb(0xE0, 0xE0, 0xE0))
            : MakeButton("稍后查看", Color.FromRgb(0xE0, 0xE0, 0xE0));
        var btnRead = MakeButton("已知悉", Color.FromRgb(0xE0, 0xE0, 0xE0));

        btnDone.Click += (_, _) => { _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, status: TodoStatus.Done); ReloadFromNotes(); };
        if (e.DueTime.HasValue)
            btnRight.Click += (_, _) => { _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, dueTime: (e.DueTime ?? DateTime.Now).AddDays(1)); ReloadFromNotes(); };
        else
            btnRight.Click += (_, _) => ReloadFromNotes(); // 稍后查看 = 仅收起该行，下次再显示
        btnRead.Click += (_, _) => { _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, status: TodoStatus.Read); ReloadFromNotes(); };

        btnBar.Children.Add(btnDone);
        btnBar.Children.Add(btnRight);
        btnBar.Children.Add(btnRead);

        var panel = new StackPanel();
        panel.Children.Add(contentText);
        panel.Children.Add(timeText);
        panel.Children.Add(btnBar);
        row.Child = panel;
        return row;
    }

    /// <summary>已暂缓行：恢复提醒(Read→Open 回待处理) / 标记完成(→Done)。</summary>
    private UIElement BuildReadRow(NoteEntry e)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(10, 8, 10, 8)
        };
        var contentText = new TextBlock
        {
            Text = e.EditedContent ?? e.Content,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 320
        };
        var timeText = new TextBlock
        {
            Text = e.DueTime.HasValue ? $"提醒时间:{e.DueTime:yyyy-MM-dd HH:mm}" : "无提醒时间",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11, Margin = new Thickness(0, 2, 0, 6)
        };

        var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnRestore = MakeButton("恢复提醒", Color.FromRgb(0x4C, 0xAF, 0x50));
        var btnDone = MakeButton("标记完成", Color.FromRgb(0xE0, 0xE0, 0xE0));

        btnRestore.Click += (_, _) => { _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, status: TodoStatus.Open); ReloadFromNotes(); };
        btnDone.Click += (_, _) => { _notes.UpdateTodo(e, newContent: e.EditedContent ?? e.Content, status: TodoStatus.Done); ReloadFromNotes(); };

        btnBar.Children.Add(btnRestore);
        btnBar.Children.Add(btnDone);

        var panel = new StackPanel();
        panel.Children.Add(contentText);
        panel.Children.Add(timeText);
        panel.Children.Add(btnBar);
        row.Child = panel;
        return row;
    }

    private static Button MakeButton(string text, Color accent) => new()
    {
        Content = text, Margin = new Thickness(6, 0, 0, 0),
        Foreground = new SolidColorBrush(accent), BorderBrush = new SolidColorBrush(accent),
        Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), FontSize = 12
    };

    /// <summary>按钮处理后重新从文件载入刷新分组（状态已落盘）。</summary>
    private void ReloadFromNotes()
    {
        RefreshAll(_notes.LoadAllEntries());
    }
}