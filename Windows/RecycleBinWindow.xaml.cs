using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FocusCapture.Services;
using FocusCapture.Services.Sync;

namespace FocusCapture.Windows;

/// <summary>回收站列表项 ViewModel：包装 (记录文件名 + 记录内容)，供绑定展示</summary>
public class RecycleItemViewModel
{
    public string FileName { get; }
    public RecycleBinEntry Entry { get; }
    public string Preview => Entry.Preview;
    public string DeletedAtText => Entry.DeletedAt.ToString("yyyy-MM-dd HH:mm");
    public string ExpiresAtText => Entry.ExpiresAt.ToString("yyyy-MM-dd");

    public RecycleItemViewModel(string fileName, RecycleBinEntry entry)
    {
        FileName = fileName;
        Entry = entry;
    }
}

public partial class RecycleBinWindow : Window
{
    private readonly NoteService _noteService;
    private readonly RecycleBinService _recycleBin;
    private readonly SyncEngine? _syncEngine;

    public RecycleBinWindow(NoteService noteService, RecycleBinService recycleBin, SyncEngine? syncEngine = null)
    {
        InitializeComponent();
        _noteService = noteService;
        _recycleBin = recycleBin;
        _syncEngine = syncEngine;
        Reload();
    }

    private void Reload()
    {
        var items = _recycleBin.List()
            .Select(x => new RecycleItemViewModel(x.FileName, x.Entry))
            .ToList();
        RecycleList.ItemsSource = items;
        EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        BtnRestore.IsEnabled = RecycleList.SelectedItem != null;
        BtnPurge.IsEnabled = RecycleList.Items.Count > 0;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { /* DragMove 在窗口未显示时会抛异常 */ }
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void RecycleList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        if (RecycleList.SelectedItem is not RecycleItemViewModel vm) return;
        _recycleBin.Restore(vm.FileName, vm.Entry, _noteService);
        Reload();
    }

    private void BtnPurge_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(
            "确认清空回收站？\n\n清空后这些笔记将被彻底删除，无法恢复。",
            "清空确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var purged = _recycleBin.PurgeAll();
        // 清空回收站 → 触发同步软删（Deleted=true 上传，QUEST-5 第五步 2）
        if (purged.Count > 0)
        {
            _noteService.RaiseNotesChanged();
            _syncEngine?.QueueRecycleBinPurge(purged);
        }
        Reload();
    }
}
