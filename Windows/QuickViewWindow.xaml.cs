using System.ComponentModel;
using System.Runtime.CompilerServices;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;

namespace FocusCapture.Windows;

public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => string.IsNullOrEmpty(v as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>笔记列表项的 ViewModel：包装 NoteEntry，添加 IsSelected（INPC 支持）</summary>
public class NoteEntryViewModel : INotifyPropertyChanged
{
    public NoteEntry Entry { get; }
    public DateTime Timestamp => Entry.Timestamp;
    public string FirstLine => Entry.FirstLine;
    public string SourceWindow => Entry.SourceWindow;
    public string? Tag => Entry.Tag;
    public string Content => Entry.Content;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
        }
    }

    private string _editText = string.Empty;
    public string EditText
    {
        get => _editText;
        set
        {
            if (_editText == value) return;
            _editText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditText)));
        }
    }

    public void BeginEdit()
    {
        EditText = Entry.Content;
        IsEditing = true;
    }

    public void CancelEdit() => IsEditing = false;

    public NoteEntryViewModel(NoteEntry entry) { Entry = entry; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>日期下拉框选项：日期 + 显示文案</summary>
public sealed record DayOption(DateTime Date, string Label);

public partial class QuickViewWindow : Window
{
    private readonly NoteService _noteService;
    private readonly AppSettings _settings;
    private ExportDialog? _exportDialog;
    private List<NoteEntryViewModel> _viewModels = new();
    private List<DayOption> _dayOptions = new();
    private DateTime _selectedDate = DateTime.Today;
    private bool _suppressDaySelector;
    private DispatcherTimer? _selectionTimer;
    private TextBox? _activeEditBox;
    private NoteEntryViewModel? _activeEditVm;

    public QuickViewWindow(NoteService noteService, AppSettings settings)
    {
        InitializeComponent();
        _noteService = noteService;
        _settings = settings;
        Opacity = settings.QuickViewOpacity;
    }

    /// <summary>打开时重建“今天/昨天/前天”选项并加载今天的笔记</summary>
    public void Refresh()
    {
        BuildDayOptions();
        ReloadNotes();
    }

    private void BuildDayOptions()
    {
        _suppressDaySelector = true;
        try
        {
            _dayOptions = new List<DayOption>();
            for (var i = 0; i < 3; i++)
            {
                var d = DateTime.Today.AddDays(-i);
                var prefix = i switch { 0 => "今天", 1 => "昨天", _ => "前天" };
                _dayOptions.Add(new DayOption(d, $"{prefix}（{d:yyyy年M月d日 dddd}）"));
            }
            DaySelector.ItemsSource = _dayOptions;
            DaySelector.SelectedIndex = 0;
            _selectedDate = DateTime.Today;
        }
        finally
        {
            _suppressDaySelector = false;
        }
    }

    private void DaySelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDaySelector || DaySelector.SelectedItem is not DayOption option) return;
        _selectedDate = option.Date;
        ReloadNotes();
    }

    private void ReloadNotes()
    {
        DateText.Text = _selectedDate.ToString("yyyy年M月d日 dddd");
        var entries = _noteService.LoadNotes(_selectedDate);
        _viewModels = entries.Select(e => new NoteEntryViewModel(e)).ToList();
        NotesList.ItemsSource = _viewModels;
        EmptyHint.Visibility = _viewModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionUI();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 编辑态优先：Esc 取消编辑，Ctrl+S 保存
        var editing = _viewModels.FirstOrDefault(vm => vm.IsEditing);
        if (editing != null)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                editing.CancelEdit();
                return;
            }
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                SaveEditNote(editing);
                return;
            }
        }
        if (e.Key == Key.Escape) Hide();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { /* DragMove 在窗口未显示时会抛 InvalidOperationException */ }
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>标题栏 AI 问答入口（全局，无笔记绑定）</summary>
    private void BtnAiAsk_Click(object sender, RoutedEventArgs e)
    {
        AIDialogHelper.Open(ExplainMode.Ask);
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = GetSelectedEntries();
            var notes = selected.Count > 0
                ? selected
                : _viewModels.Select(vm => vm.Entry).ToList();

            if (notes.Count == 0)
            {
                var msg = _selectedDate.Date == DateTime.Today
                    ? "今天还没有笔记，快去记录灵感吧。"
                    : "这一天还没有笔记。";
                System.Windows.MessageBox.Show(msg, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _exportDialog = new ExportDialog(_settings) { Owner = this };
            _exportDialog.ShowDialog();

            if (!_exportDialog.Confirmed) return;

            var svc = new NoteExportService();
            var config = _exportDialog.Config;
            var folder = _exportDialog.FolderPath;
            var ext = svc.GetFileExtension(config.Format);
            var baseName = selected.Count > 0
                ? $"灵感_已选{selected.Count}条_{_selectedDate:yyyy-MM-dd}"
                : $"灵感_{_selectedDate:yyyy-MM-dd}";
            var fileName = NoteExportService.SanitizeFileName(baseName) + ext;
            var filePath = NoteExportService.GetUniquePath(Path.Combine(folder, fileName));

            Directory.CreateDirectory(folder);

            if (config.Format == ExportFormat.Word)
            {
                var bytes = svc.BuildWord(notes, config);
                File.WriteAllBytes(filePath, bytes);
            }
            else
            {
                var content = svc.BuildExport(notes, config);
                File.WriteAllText(filePath, content, Encoding.UTF8);
            }

            var successDialog = new SuccessDialog(filePath) { Owner = this };
            successDialog.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"导出失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _exportDialog = null;
        }
    }

    private void NoteItem_Click(object sender, MouseButtonEventArgs e)
    {
        // 防止点击 CheckBox 区域时也触发复制
        if (IsClickFromCheckBox(e.OriginalSource as DependencyObject)) return;

        if (sender is Border b && b.DataContext is NoteEntryViewModel vm)
        {
            // 双击 → 进入编辑态
            if (e.ClickCount == 2)
            {
                BeginEditNote(vm);
                return;
            }

            // 编辑态内的点击不触发复制
            if (vm.IsEditing) return;

            ClipboardHookService.MarkSelfCopy(); // 抑制剪贴板监控反馈
            WpfClipboard.SetText(vm.Content);
            b.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x50, 0x3A));
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            t.Tick += (_, _) => { b.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)); t.Stop(); };
            t.Start();
        }
    }

    // ── 条目右键菜单 ──

    private void NoteContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        // 记录右键目标条目，供各菜单项点击时使用
        if (sender is ContextMenu menu && menu.PlacementTarget is FrameworkElement target)
        {
            _contextTarget = target.DataContext as NoteEntryViewModel;
        }
    }

    private NoteEntryViewModel? _contextTarget;

    private NoteEntryViewModel? GetContextTarget(object sender)
        => _contextTarget;

    private void CtxAiTranslate_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextTarget(sender);
        if (vm != null) AIDialogHelper.Open(ExplainMode.Translate, vm.Entry);
    }

    private void CtxAiSearch_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextTarget(sender);
        if (vm != null) AIDialogHelper.Open(ExplainMode.Search, vm.Entry);
    }

    private void CtxCopy_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextTarget(sender);
        if (vm == null) return;
        ClipboardHookService.MarkSelfCopy();
        WpfClipboard.SetText(vm.Content);
    }

    private void CtxEdit_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextTarget(sender);
        if (vm != null) BeginEditNote(vm);
    }

    private void CtxDelete_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextTarget(sender);
        if (vm == null) return;

        var confirm = System.Windows.MessageBox.Show(
            $"确认删除这条笔记？\n\n时间：{vm.Timestamp:HH:mm}\n内容：{vm.FirstLine}\n\n" +
            "（源 .md 文件未修改，可从源文件回溯）",
            "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _noteService.DeletedService.MarkDeleted(vm.Entry);
        CancelEditState(vm);
        _viewModels.Remove(vm);
        NotesList.Items.Refresh();
        UpdateSelectionUI();
    }

    /// <summary>双击进入编辑态：沉浸式锁定时弹窗拦截</summary>
    private void BeginEditNote(NoteEntryViewModel vm)
    {
        if (ImmersiveSessionService.IsLocked(vm.Entry.Timestamp))
        {
            System.Windows.MessageBox.Show("沉浸式输入进行中，暂不可编辑", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 同一时间只允许编辑一条
        foreach (var other in _viewModels.Where(v => v.IsEditing && !ReferenceEquals(v, vm)))
            other.CancelEdit();
        CancelEditState(vm);

        vm.BeginEdit();
        _activeEditVm = vm;
        // 等模板切换完成后聚焦编辑框
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (NotesList.ItemContainerGenerator.ContainerFromItem(vm) is FrameworkElement container)
            {
                var box = FindVisualChild<TextBox>(container);
                AttachEditBox(vm, box);
                box?.Focus();
                box?.SelectAll();
            }
        }), DispatcherPriority.Background);
    }

    private void BtnEditSave_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NoteEntryViewModel vm) SaveEditNote(vm);
    }

    private void BtnEditCancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NoteEntryViewModel vm)
        {
            CancelEditState(vm);
            vm.CancelEdit();
        }
    }

    /// <summary>保存编辑：保存前检查沉浸式锁定，成功后重新加载面板</summary>
    private void SaveEditNote(NoteEntryViewModel vm)
    {
        if (ImmersiveSessionService.IsLocked(vm.Entry.Timestamp))
        {
            System.Windows.MessageBox.Show("沉浸式输入进行中，暂不可编辑", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            vm.CancelEdit();
            return;
        }

        var newContent = vm.EditText?.Trim() ?? "";
        if (string.IsNullOrEmpty(newContent))
        {
            System.Windows.MessageBox.Show("内容不能为空", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_noteService.UpdateNote(vm.Entry, newContent))
        {
            CancelEditState(vm);
            Refresh();
        }
        else
        {
            System.Windows.MessageBox.Show("保存失败：未在笔记文件中找到该条目", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            vm.CancelEdit();
        }
    }

    // ── 编辑态浮动工具条 ──

    /// <summary>绑定当前编辑框的选中/失焦事件；切换编辑目标时解绑旧的</summary>
    private void AttachEditBox(NoteEntryViewModel vm, TextBox? box)
    {
        if (_activeEditBox != null)
        {
            _activeEditBox.SelectionChanged -= EditBox_SelectionChanged;
            _activeEditBox.LostFocus -= EditBox_LostFocus;
        }
        _activeEditBox = box;
        if (box != null)
        {
            box.SelectionChanged += EditBox_SelectionChanged;
            box.LostFocus += EditBox_LostFocus;
        }
        HideFloatToolbar();
    }

    /// <summary>取消编辑状态：解绑编辑框、清计时器、隐藏工具条</summary>
    private void CancelEditState(NoteEntryViewModel? vm)
    {
        if (vm != null && _activeEditVm != null && !ReferenceEquals(vm, _activeEditVm)) return;

        if (_activeEditBox != null)
        {
            _activeEditBox.SelectionChanged -= EditBox_SelectionChanged;
            _activeEditBox.LostFocus -= EditBox_LostFocus;
        }
        _activeEditBox = null;
        _activeEditVm = null;
        _selectionTimer?.Stop();
        HideFloatToolbar();
    }

    private void EditBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        var selected = box.SelectionLength > 0 && !string.IsNullOrEmpty(box.SelectedText);
        _selectionTimer?.Stop();

        if (selected && box.IsKeyboardFocused)
        {
            _selectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _selectionTimer.Tick += (_, _) =>
            {
                _selectionTimer.Stop();
                ShowFloatToolbar();
            };
            _selectionTimer.Start();
        }
        else
        {
            HideFloatToolbar();
        }
    }

    private void EditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _selectionTimer?.Stop();
        HideFloatToolbar();
    }

    /// <summary>在选中文字附近显示浮动工具条（超界时靠边）</summary>
    private void ShowFloatToolbar()
    {
        var box = _activeEditBox;
        if (box == null || _activeEditVm == null) return;
        if (box.SelectionLength <= 0) return;

        // 选中起点在窗口内的坐标
        var rect = box.GetRectFromCharacterIndex(box.SelectionStart, false);
        var point = box.TransformToAncestor(this).Transform(new Point(rect.Left, rect.Bottom));

        FloatToolbar.Visibility = Visibility.Visible;
        FloatToolbar.UpdateLayout();

        var left = Math.Min(Math.Max(0, point.X), ActualWidth - FloatToolbar.ActualWidth - 4);
        var top = Math.Min(Math.Max(0, point.Y + 4), ActualHeight - FloatToolbar.ActualHeight - 4);
        FloatToolbar.Margin = new Thickness(left, top, 0, 0);
    }

    private void HideFloatToolbar()
    {
        FloatToolbar.Visibility = Visibility.Collapsed;
    }

    private void FloatToolbar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode }) return;
        var vm = _activeEditVm;
        var selected = _activeEditBox?.SelectedText?.Trim() ?? "";
        if (vm == null || string.IsNullOrEmpty(selected)) { HideFloatToolbar(); return; }

        var explainMode = mode switch
        {
            "Translate" => ExplainMode.Translate,
            "Search" => ExplainMode.Search,
            _ => ExplainMode.Ask,
        };

        HideFloatToolbar();
        AIDialogHelper.Open(explainMode, vm.Entry, selected);
    }

    /// <summary>在可视树中查找指定类型的第一个后代</summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    /// <summary>从原始点击源向上回溯，判断是否落在 CheckBox 上</summary>
    private static bool IsClickFromCheckBox(DependencyObject? src)
    {
        var d = src;
        while (d != null)
        {
            if (d is CheckBox) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    // ── 多选 / 删除 ──

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _viewModels) vm.IsSelected = true;
        UpdateSelectionUI();
    }

    private void BtnInvert_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _viewModels) vm.IsSelected = !vm.IsSelected;
        UpdateSelectionUI();
    }

    private void BtnDeleteOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NoteEntryViewModel vm)
        {
            var confirm = System.Windows.MessageBox.Show(
                $"确认删除这条笔记？\n\n时间：{vm.Timestamp:HH:mm}\n内容：{vm.FirstLine}\n\n" +
                "（源 .md 文件未修改，可从源文件回溯）",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _noteService.DeletedService.MarkDeleted(vm.Entry);
            _viewModels.Remove(vm);
            NotesList.Items.Refresh();
            UpdateSelectionUI();
        }
    }

    private void BtnDeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedEntries();
        if (selected.Count == 0) return;

        var confirm = System.Windows.MessageBox.Show(
            $"确认删除选中的 {selected.Count} 条笔记？\n\n" +
            "（源 .md 文件未修改，可从源文件回溯）",
            "批量删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _noteService.DeletedService.MarkDeletedRange(selected);
        // 反向遍历删除，避免索引错位
        for (var i = _viewModels.Count - 1; i >= 0; i--)
        {
            if (_viewModels[i].IsSelected) _viewModels.RemoveAt(i);
        }
        NotesList.Items.Refresh();
        UpdateSelectionUI();
    }

    /// <summary>更新选择工具栏的可见性 + 计数 + 导出按钮文案</summary>
    private void UpdateSelectionUI()
    {
        var count = _viewModels.Count(vm => vm.IsSelected);
        if (count > 0)
        {
            SelectedCountText.Text = $"已选 {count} 条";
            SelectedCountText.Visibility = Visibility.Visible;
            BtnDeleteSelected.Visibility = Visibility.Visible;
            BtnExport.Content = $"▼ 导出已选 {count} 条";
        }
        else
        {
            SelectedCountText.Visibility = Visibility.Collapsed;
            BtnDeleteSelected.Visibility = Visibility.Collapsed;
            BtnExport.Content = "▼ 导出";
        }
    }

    private List<NoteEntry> GetSelectedEntries()
        => _viewModels.Where(vm => vm.IsSelected).Select(vm => vm.Entry).ToList();

    public new void Show()
    {
        Refresh();
        base.Show();
        Activate();
        Focus();
    }

    public new void Hide()
    {
        if (_exportDialog != null && _exportDialog.IsVisible)
        {
            _exportDialog.Close();
            _exportDialog = null;
        }
        base.Hide();
    }
}
