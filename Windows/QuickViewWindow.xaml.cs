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
    public string FirstLine => (Entry.EditedContent ?? Entry.Content).Split('\n')[0].Trim();
    public string SourceWindow => Entry.SourceWindow;
    public string? Tag => Entry.Tag;

    /// <summary>展示内容：编辑过的笔记优先显示编辑后内容（存储层仍保留原行）</summary>
    public string Content => Entry.EditedContent ?? Entry.Content;

    /// <summary>面板预览：原文首行 +（如有）最近一条 AI 释义首行（等高保持，40px 内容区截断）</summary>
    public string DisplayPreview
    {
        get
        {
            if (Entry.AiFills.Count == 0) return FirstLine;
            var lastFill = Entry.AiFills[^1].Split('\n')[0].Trim();
            return $"{FirstLine}\n【AI 释义】{lastFill}";
        }
    }

    /// <summary>是否有 AI 释义（用于面板右侧 AI 徽章标识）</summary>
    public bool HasAiFills => Entry.AiFills.Count > 0;

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
        // 编辑时把 AiFills 拼接到 EditText（用户能看到+编辑全部内容）；保存时 SaveEditNote 会检测并分离
        var sb = new System.Text.StringBuilder(Entry.EditedContent ?? Entry.Content);
        if (Entry.AiFills.Count > 0)
        {
            sb.Append("\n\n—— AI 释义 ——\n");
            foreach (var fill in Entry.AiFills)
                sb.AppendLine($"【AI 释义】{fill}");
        }
        EditText = sb.ToString();
        IsEditing = true;
    }

    public void CancelEdit() => IsEditing = false;

    /// <summary>从编辑框全文分离主内容与 AI 释义块；无分隔符时全文视为内容</summary>
    public static (string Content, List<string> Fills) SplitEditText(string text)
    {
        const string separator = "\n\n—— AI 释义 ——\n";
        var idx = text.IndexOf(separator, StringComparison.Ordinal);
        if (idx < 0) return (text, new List<string>());

        var content = text[..idx].TrimEnd();
        var fills = text[(idx + separator.Length)..].Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("【AI 释义】", StringComparison.Ordinal))
            .Select(l => l.Substring("【AI 释义】".Length).Trim())
            .Where(l => l.Length > 0)
            .ToList();
        return (content, fills);
    }

    public NoteEntryViewModel(NoteEntry entry) { Entry = entry; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class QuickViewWindow : Window
{
    private readonly NoteService _noteService;
    private readonly AppSettings _settings;
    private ExportDialog? _exportDialog;
    private List<NoteEntryViewModel> _viewModels = new();
    private DateTime _selectedDate = DateTime.Today;
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

    /// <summary>重新加载当前选中日期的笔记（打开时与刷新按钮共用）</summary>
    public void Refresh()
    {
        ReloadNotes();
    }

    private void ReloadNotes()
    {
        var entries = _noteService.LoadNotes(_selectedDate);
        _viewModels = entries.Select(e => new NoteEntryViewModel(e)).ToList();
        NotesList.ItemsSource = _viewModels;
        EmptyHint.Visibility = _viewModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionUI();
    }

    /// <summary>焦点回归自动刷新（切回面板/关闭弹窗后列表同步最新笔记）</summary>
    private void Window_Activated(object sender, EventArgs e)
    {
        try { Refresh(); }
        catch { /* 刷新失败不阻塞窗口激活 */ }
    }

    /// <summary>标题栏刷新按钮：立即重新加载列表</summary>
    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>日历按钮：打开热力图弹窗，选中日期后切换到当天笔记</summary>
    private void BtnCalendar_Click(object sender, RoutedEventArgs e)
    {
        var cal = new CalendarWindow(_noteService, _selectedDate) { Owner = this };
        cal.DateSelected += d => _selectedDate = d;
        if (cal.ShowDialog() == true && cal.SelectedDate.HasValue)
            _selectedDate = cal.SelectedDate.Value;
        ReloadNotes();
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
            WpfClipboard.SetText(vm.Content); // 复制展示内容（编辑过则复制编辑后内容）
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
        // 传整条笔记作为 selectedText，让 OpenSession 走 selectedText 路径自动发起第一轮（修复：之前漏传，对话框空白）
        if (vm != null) AIDialogHelper.Open(ExplainMode.Translate, vm.Entry, vm.Content);
    }

    private void CtxAiSearch_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextTarget(sender);
        if (vm != null) AIDialogHelper.Open(ExplainMode.Search, vm.Entry, vm.Content);
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
            "（对应行将从 .md 源文件删除，软删除记录保留）",
            "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        if (!_noteService.DeleteNote(vm.Entry))
        {
            System.Windows.MessageBox.Show("删除失败：未在笔记文件中找到该条目，可能已被外部修改", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        CancelEditState(vm);
        _viewModels.Remove(vm);
        NotesList.Items.Refresh();
        UpdateSelectionUI();
    }

    /// <summary>双击进入编辑态：沉浸式锁定时弹窗拦截</summary>
    private double _scrollOffsetBeforeEdit; // 进入编辑前的列表滚动位置，编辑后恢复

    // 进入编辑时 SelectAll 会触发一次 SelectionChanged，用此标志抑制（全选不弹浮动工具条，用户主动拖选才弹）
    private bool _suppressToolbarOnSelectAll;

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

        // 记录编辑前 ScrollViewer 滚动偏移，编辑后恢复（ScrollViewer 是 NotesList 的父级，不是后代）
        _scrollOffsetBeforeEdit = NotesScroll?.VerticalOffset ?? 0;

        vm.BeginEdit();
        _activeEditVm = vm;
        // 等模板切换完成后聚焦编辑框
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (NotesList.ItemContainerGenerator.ContainerFromItem(vm) is FrameworkElement container)
            {
                var box = FindVisualChild<TextBox>(container);
                AttachEditBox(vm, box);
                _suppressToolbarOnSelectAll = true; // 全选触发 SelectionChanged 时不弹浮动工具条
                box?.Focus();
                box?.SelectAll();
                // 修复：长文本全选后 TextBox 自动滚动到选区末尾，内容"被拉到下面看不见"——拉回开头
                box?.ScrollToHome();

                // 恢复列表滚动位置到编辑前（Focus 触发的 ScrollIntoView 会让编辑项滚到视口边缘，后面笔记被挤出去）
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    NotesScroll?.ScrollToVerticalOffset(_scrollOffsetBeforeEdit);
                }), DispatcherPriority.Background);
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

    /// <summary>全屏编辑：独立 Window，与行内编辑共享 EditText（双向同步）</summary>
    private void BtnFullEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NoteEntryViewModel vm }) OpenFullEdit(vm);
    }

    private void OpenFullEdit(NoteEntryViewModel vm)
    {
        if (ImmersiveSessionService.IsLocked(vm.Entry.Timestamp))
        {
            System.Windows.MessageBox.Show("沉浸式输入进行中，暂不可编辑", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 若未在行内编辑，先同步 EditText = 原始内容；若在行内编辑过，保留行内最新内容
        if (!vm.IsEditing)
            vm.EditText = vm.Content;

        // 关闭行内编辑态（EditText 值保留给全屏窗口）
        CancelEditState(vm);
        vm.CancelEdit();

        var win = new NoteEditWindow(_noteService, vm,
            $"编辑笔记 · {vm.Entry.Timestamp:yyyy-MM-dd HH:mm}")
        { Owner = this };

        if (win.ShowDialog() == true)
            Refresh();
    }

    /// <summary>保存编辑：追加【编辑】标记行（MD 只增不减），成功后重新加载面板</summary>
    private void SaveEditNote(NoteEntryViewModel vm)
    {
        if (ImmersiveSessionService.IsLocked(vm.Entry.Timestamp))
        {
            System.Windows.MessageBox.Show("沉浸式输入进行中，暂不可编辑", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            vm.CancelEdit();
            return;
        }

        // 分离 AI 释义块（编辑时拼接显示，保存时剥离开来保持"MD 只增不减"结构）：
        // 只保存主内容为【编辑】行；释义行只能由 AI 对话框追加，编辑框内改动不写回存储
        var (contentToSave, _) = NoteEntryViewModel.SplitEditText(vm.EditText?.Trim() ?? "");
        if (string.IsNullOrEmpty(contentToSave))
        {
            System.Windows.MessageBox.Show("内容不能为空", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 内容未变化：不追加冗余【编辑】行
        var displayBase = vm.Entry.EditedContent ?? vm.Entry.Content;
        if (contentToSave != displayBase)
        {
            if (!_noteService.AppendEdit(vm.Entry, contentToSave))
            {
                System.Windows.MessageBox.Show("保存失败：未在笔记文件中找到该条目，可能已被外部修改", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                vm.CancelEdit();
                return;
            }
            vm.Entry.EditedContent = contentToSave;
        }

        CancelEditState(vm);
        Refresh();
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
        _suppressToolbarOnSelectAll = false;
        HideFloatToolbar();
    }

    private void EditBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        var selected = box.SelectionLength > 0 && !string.IsNullOrEmpty(box.SelectedText);
        _selectionTimer?.Stop();

        // 进入编辑时 SelectAll 触发的全选：不弹工具条（用户主动拖选才弹）
        if (_suppressToolbarOnSelectAll)
        {
            _suppressToolbarOnSelectAll = false;
            HideFloatToolbar();
            return;
        }

        if (selected && box.IsKeyboardFocused)
        {
            _selectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _selectionTimer.Tick += (_, _) =>
            {
                _selectionTimer.Stop();
                try { ShowFloatToolbar(); }
                catch { HideFloatToolbar(); } // 编辑框已失效等异常，兜底隐藏不崩
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
        // 延迟：避免点击浮动工具条/编辑态内按钮时 TextBox 失焦导致按钮 IsHitTestVisible=false（Collapsed）收不到 Click
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // 焦点仍在编辑态 UI（EditBox/全屏/保存/取消，DataContext 都是当前 vm）或浮动工具条内 → 不自动保存退出
            if (!FloatToolbar.IsKeyboardFocusWithin && !IsFocusInEditingArea())
            {
                // 焦点真正离开编辑区：自动保存退出（用户期望：点击被编辑笔记以外的任何位置都默认保存退出）
                if (_activeEditVm != null && _activeEditVm.IsEditing)
                {
                    try { SaveEditNote(_activeEditVm); }
                    catch { /* 已被外部流程处理（如删除/刷新）则忽略 */ }
                }
                HideFloatToolbar();
            }
        }), DispatcherPriority.Background);
    }

    /// <summary>焦点是否落在当前编辑态 UI 内（EditBox / 全屏 / 保存 / 取消，DataContext 均指向当前 vm）</summary>
    private bool IsFocusInEditingArea()
    {
        if (_activeEditVm == null) return false;
        var focused = Keyboard.FocusedElement as DependencyObject;
        while (focused != null)
        {
            if (focused is FrameworkElement fe && ReferenceEquals(fe.DataContext, _activeEditVm))
                return true;
            focused = VisualTreeHelper.GetParent(focused);
        }
        return false;
    }

    /// <summary>在选中文字附近显示浮动工具条（Canvas 浮层定位，不参与布局；超界时靠边）</summary>
    private void ShowFloatToolbar()
    {
        var box = _activeEditBox;
        if (box == null || _activeEditVm == null) return;
        if (box.SelectionLength <= 0) return;

        try
        {
            // 选中起点相对覆盖层 Canvas 的坐标
            // 注意：必须用 TransformToVisual（不要求祖先关系）——FloatToolbarCanvas 是 ScrollViewer 的兄弟，不是 EditBox 的祖先，
            // 用 TransformToAncestor 会抛 InvalidOperationException 导致闪退
            var rect = box.GetRectFromCharacterIndex(box.SelectionStart, false);
            var point = box.TransformToVisual(FloatToolbarCanvas).Transform(new Point(rect.Left, rect.Bottom));

            FloatToolbar.Visibility = Visibility.Visible;
            FloatToolbar.UpdateLayout();

            var left = Math.Min(Math.Max(0, point.X), FloatToolbarCanvas.ActualWidth - FloatToolbar.ActualWidth - 4);
            var top = Math.Min(Math.Max(0, point.Y + 4), FloatToolbarCanvas.ActualHeight - FloatToolbar.ActualHeight - 4);
            Canvas.SetLeft(FloatToolbar, left);
            Canvas.SetTop(FloatToolbar, top);
        }
        catch
        {
            // 编辑框可能已从可视树移除（退出编辑/删除等），任何布局异常都不崩，直接隐藏
            HideFloatToolbar();
        }
    }

    private void HideFloatToolbar()
    {
        FloatToolbar.Visibility = Visibility.Collapsed;
    }

    /// <summary>工具条拖动手柄：Thumb 拖动时限制在 Canvas 范围内</summary>
    private void ToolbarDragThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var left = Canvas.GetLeft(FloatToolbar) + e.HorizontalChange;
        var top = Canvas.GetTop(FloatToolbar) + e.VerticalChange;
        left = Math.Max(0, Math.Min(left, FloatToolbarCanvas.ActualWidth - FloatToolbar.ActualWidth));
        top = Math.Max(0, Math.Min(top, FloatToolbarCanvas.ActualHeight - FloatToolbar.ActualHeight));
        Canvas.SetLeft(FloatToolbar, left);
        Canvas.SetTop(FloatToolbar, top);
    }

    private void FloatToolbar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button { Tag: string mode }) return;
            var vm = _activeEditVm;
            // 优先选中文字；若点击瞬间选区丢失（失焦等），兜底用编辑框全文——保证点翻译/搜索一定有内容可发
            var selected = _activeEditBox?.SelectedText?.Trim();
            if (string.IsNullOrEmpty(selected))
                selected = vm?.EditText?.Trim();
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
        catch
        {
            // 任何异常都不崩，静默隐藏工具条
            HideFloatToolbar();
        }
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
                "（对应行将从 .md 源文件删除，软删除记录保留）",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            if (!_noteService.DeleteNote(vm.Entry))
            {
                System.Windows.MessageBox.Show("删除失败：未在笔记文件中找到该条目，可能已被外部修改", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
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
            "（对应行将从 .md 源文件删除，软删除记录保留）",
            "批量删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var failed = 0;
        foreach (var entry in selected)
        {
            if (!_noteService.DeleteNote(entry)) failed++;
        }
        Refresh(); // 重载后已删除项自然消失，失败项保留
        if (failed > 0)
        {
            System.Windows.MessageBox.Show($"{failed} 条笔记删除失败（未在笔记文件中找到，可能已被外部修改）", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>任一勾选即显示"删除已选"按钮（勾选/取消由 NoteCheckBox_Click 同步触发）</summary>
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

    /// <summary>CheckBox 勾选/取消 → 同步选中状态并更新删除按钮可见性</summary>
    private void NoteCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: NoteEntryViewModel vm } cb)
            vm.IsSelected = cb.IsChecked == true;
        UpdateSelectionUI();
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
