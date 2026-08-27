using System.ComponentModel;
using System.Runtime.CompilerServices;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;
using FocusCapture.Services.Sync;

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

    // ── v3.5 待办展示属性 ──
    public bool IsTodo => Entry.Type == NoteType.Todo;                                    // 待办徽标显示
    public bool IsDone => Entry.Type == NoteType.Todo && Entry.TodoStatus == TodoStatus.Done;  // 已办：灰显+删除线+沉底
    public bool IsRead => Entry.Type == NoteType.Todo && Entry.TodoStatus == TodoStatus.Read;  // 已读：橙字小标
    public bool IsTodoNotDone => Entry.Type == NoteType.Todo && Entry.TodoStatus != TodoStatus.Done; // 未办（Open+Read，供「待办」筛选档）
    public string? DueTimeText => Entry.DueTime?.ToString("MM-dd HH:mm");                  // 可有可无的提醒角标辅助

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

    /// <summary>是否重复（按 Content 完全相同聚合，频次>=2 视为重复）。控制面板条目文字颜色：默认白，重复红。</summary>
    private bool _isDuplicate;
    public bool IsDuplicate
    {
        get => _isDuplicate;
        set
        {
            if (_isDuplicate == value) return;
            _isDuplicate = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDuplicate)));
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
    private readonly Func<SyncEngine?>? _syncEngineProvider;   // 2026-08-15：注入引擎引用（引擎可能因配置变更重建，故传 provider）
    private IChatProvider? _aiProvider;                        // v3.5：面板编辑时间识别 LLM 兜底（MainWindow 装配传入；设置变更后由 UpdateAiProvider 重建）

    /// <summary>v3.5：设置窗口保存 AI 配置后由 MainWindow 调用来重建共享 provider</summary>
    public void UpdateAiProvider(IChatProvider? p) => _aiProvider = p;
    private ExportDialog? _exportDialog;
    private List<NoteEntryViewModel> _viewModels = new();
    private DateTime _selectedDate = DateTime.Today;
    private DispatcherTimer? _selectionTimer;
    private TextBox? _activeEditBox;
    private NoteEntryViewModel? _activeEditVm;

    // ── 2026-08-15 新增：列表加载模式（单日 / 区间 / 全局查找） ──
    private NoteLoadMode _loadMode = NoteLoadMode.Date;
    private DateTime _rangeStart;
    private DateTime _rangeEnd;
    private string _searchKeyword = "";

    private enum NoteLoadMode { Date, Range, Search }

    // ── v3.5 筛选（内存层过滤，切日期/刷新后保持生效；字段驱动，非前端一次性 filter） ──
    private readonly HashSet<string> _typeFilter = new(StringComparer.Ordinal) { "All" }; // All / Note / Todo(未办) / Done(已办)，多选组合
    private string _sourceFilter = "";                                                    // "" = 全部来源
    private bool _suppressSourceFilterEvent;                                              // 刷新来源下拉时抑制 SelectionChanged 防递归 ReloadNotes

    // ── v3.5 编辑时间识别建议条 ──
    private NoteEntryViewModel? _suggestVm;                                                // 建议条关联的条目（保存后同步了 Content）
    private DateTime? _suggestDue;                                                         // 待设为提醒的时间
    private DispatcherTimer? _suggestTimer;                                                // 建议条 10 秒自动消失

    public QuickViewWindow(NoteService noteService, AppSettings settings, Func<SyncEngine?>? syncEngineProvider = null, IChatProvider? aiProvider = null)
    {
        InitializeComponent();
        _noteService = noteService;
        _settings = settings;
        _syncEngineProvider = syncEngineProvider;
        _aiProvider = aiProvider;
        Opacity = settings.QuickViewOpacity;
        // AI 助手名称自定义：标题栏入口按钮文案同源读取（三处入口之一）
        BtnAiAsk.Content = string.IsNullOrWhiteSpace(settings.AiAssistantName) ? "AI 问答" : settings.AiAssistantName;
        BtnAiAsk.Width = Math.Max(72, BtnAiAsk.Content.ToString()!.Length * 14 + 24);
        UpdateSyncButtonsState();   // 启动时根据引擎状态决定按钮是否可用
    }

    /// <summary>重新加载当前选中日期的笔记（打开时与刷新按钮共用）</summary>
    public void Refresh()
    {
        ReloadNotes();
    }

    /// <summary>AI 助手名称变更时同步标题栏按钮文案（设置窗口保存后调用）</summary>
    public void UpdateAiName(string name)
    {
        var final = string.IsNullOrWhiteSpace(name) ? "AI 问答" : name;
        BtnAiAsk.Content = final;
        BtnAiAsk.Width = Math.Max(72, final.Length * 14 + 24);
    }

    private void ReloadNotes()
    {
        // 2026-08-15：按当前加载模式分派（Date / Range / Search）
        var entries = _loadMode switch
        {
            NoteLoadMode.Range => _noteService.LoadNotesRange(_rangeStart, _rangeEnd),
            NoteLoadMode.Search => _noteService.LoadNotesSearch(_searchKeyword),
            _ => _noteService.LoadNotes(_selectedDate)
        };

        // v3.5：筛选在内存层应用（ReloadNotes 后），切日期/刷新后保持生效——不是前端一次性 filter
        var filtered = ApplyFilters(entries);
        // v3.5：已办沉底——稳定排序：时间倒序基础上，已办待办排到该日期列表最后
        var sorted = filtered
            .OrderByDescending(e => e.Timestamp)
            .ThenBy(e => e.Type == NoteType.Todo && e.TodoStatus == TodoStatus.Done ? 1 : 0);

        _viewModels = sorted.Select(e => new NoteEntryViewModel(e)).ToList();
        ApplyDuplicateMarkers();
        UpdateSourceFilterOptions();   // v3.5：来源下拉从当前加载列表聚合（刷新后仍保持选中）
        NotesList.ItemsSource = _viewModels;
        UpdateEmptyHint();
        UpdateSelectionUI();
        UpdateModeIndicator();
    }

    /// <summary>v3.5：类型多选 + 来源 筛选（内存过滤；“全部”时不过滤）</summary>
    private IEnumerable<NoteEntry> ApplyFilters(IEnumerable<NoteEntry> entries)
    {
        if (!_typeFilter.Contains("All"))
        {
            var showNote = _typeFilter.Contains("Note");
            var showTodo = _typeFilter.Contains("Todo");   // 「待办」档=未办（Open+Read，不含已办）
            var showDone = _typeFilter.Contains("Done");   // 「已办」档=仅 Done
            entries = entries.Where(e =>
                (showNote && e.Type != NoteType.Todo) ||
                (showTodo && e.Type == NoteType.Todo && e.TodoStatus != TodoStatus.Done) ||
                (showDone && e.Type == NoteType.Todo && e.TodoStatus == TodoStatus.Done));
        }
        if (!string.IsNullOrEmpty(_sourceFilter))
            entries = entries.Where(e => e.SourceWindow == _sourceFilter);
        return entries;
    }

    /// <summary>v3.5：来源下拉回填当前加载列表全部来源，保持 _sourceFilter 选中（抑制事件防递归 ReloadNotes）</summary>
    private void UpdateSourceFilterOptions()
    {
        _suppressSourceFilterEvent = true;
        var sources = _viewModels.Select(v => v.SourceWindow).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s).ToList();
        SourceFilter.ItemsSource = sources;
        if (!string.IsNullOrEmpty(_sourceFilter) && sources.Contains(_sourceFilter))
            SourceFilter.SelectedItem = _sourceFilter;
        else
        {
            SourceFilter.SelectedItem = null;
            _sourceFilter = "";
        }
        _suppressSourceFilterEvent = false;
    }

    /// <summary>
    /// 按 Content 完全相同聚合判定重复：频次 ≥2 的 vm 标记 IsDuplicate=true，
    /// 触发 XAML 样式把字体颜色切到红色（#FF6B6B）。空内容不参与。
    /// </summary>
    private void ApplyDuplicateMarkers()
    {
        var contentCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var vm in _viewModels)
        {
            var c = vm.Entry.Content?.Trim();
            if (string.IsNullOrEmpty(c)) continue;
            contentCounts[c] = contentCounts.GetValueOrDefault(c) + 1;
        }
        foreach (var vm in _viewModels)
        {
            var c = vm.Entry.Content?.Trim();
            vm.IsDuplicate = !string.IsNullOrEmpty(c) && contentCounts.GetValueOrDefault(c) >= 2;
        }
    }

    private void UpdateEmptyHint()
    {
        EmptyHint.Text = _loadMode switch
        {
            NoteLoadMode.Search => $"未找到包含「{_searchKeyword}」的笔记",
            NoteLoadMode.Range => $"区间 {_rangeStart:yyyy-MM-dd} ~ {_rangeEnd:yyyy-MM-dd} 内还没有笔记",
            _ => _selectedDate.Date == DateTime.Today ? "今天还没有笔记" : "这一天还没有笔记"
        };
        EmptyHint.Visibility = _viewModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateModeIndicator()
    {
        if (_loadMode == NoteLoadMode.Range)
            ModeIndicator.Text = $"区间：{_rangeStart:yyyy-MM-dd}  ~  {_rangeEnd:yyyy-MM-dd}";
        else if (_loadMode == NoteLoadMode.Search)
            ModeIndicator.Text = $"查找：\"{_searchKeyword}\"";
        else
            ModeIndicator.Text = $"单日：{_selectedDate:yyyy-MM-dd}";
        BtnReturnToDate.Visibility = _loadMode == NoteLoadMode.Date ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>焦点回归自动刷新（切回面板/关闭弹窗后列表同步最新笔记）</summary>
    private void Window_Activated(object sender, EventArgs e)
    {
        try { Refresh(); }
        catch { /* 刷新失败不阻塞窗口激活 */ }
        UpdateSyncButtonsState();   // 引擎可能因配置变更被重建（MainWindow.RebuildSyncEngine），每次激活时同步按钮可用性
    }

    /// <summary>标题栏刷新按钮：立即重新加载列表</summary>
    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>日历按钮：打开热力图弹窗，选中区间后切换到区间笔记。单日选=start=end=选中日；区间选=start..end。</summary>
    private void BtnCalendar_Click(object sender, RoutedEventArgs e)
    {
        // 非模态 Show：点击日历弹窗以外的任何区域 → 窗口失活 → Deactivated 自动收起
        var cal = new CalendarWindow(_noteService, _selectedDate) { Owner = this };
        cal.DateRangeSelected += (start, end) =>
        {
            if (start == end)
            {
                // 单日选 → 回到 Date 模式
                _selectedDate = start;
                _loadMode = NoteLoadMode.Date;
            }
            else
            {
                // 区间选 → Range 模式
                _rangeStart = start;
                _rangeEnd = end;
                _loadMode = NoteLoadMode.Range;
            }
            ReloadNotes();
        };
        cal.Show();
    }

    /// <summary>查找按钮：弹出 SearchDialog 输入关键词，确认后进入 Search 模式替换列表。</summary>
    private void BtnSearch_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SearchDialog { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Keyword)) return;
        _searchKeyword = dlg.Keyword.Trim();
        _loadMode = NoteLoadMode.Search;
        ReloadNotes();
    }

    /// <summary>返回按钮：仅 Range/Search 模式显示。点击 → 回到 _selectedDate 单日模式。</summary>
    private void BtnReturnToDate_Click(object sender, RoutedEventArgs e)
    {
        _loadMode = NoteLoadMode.Date;
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

            _exportDialog = new ExportDialog(_settings, _noteService) { Owner = this };
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

    // ── 同步（2026-08-15 灵感速览同步入口：上传=全量推；下载=只 pull 不 push） ──

    /// <summary>根据当前引擎/解锁状态刷新同步按钮可用性 + ToolTip（窗口激活时由 UpdateSyncButtonsState 触发）</summary>
    private void UpdateSyncButtonsState()
    {
        var engine = _syncEngineProvider?.Invoke();
        var enabled = engine != null && engine.IsMasterPasswordSet;
        BtnSyncUpload.IsEnabled = enabled;
        BtnSyncDownload.IsEnabled = enabled;
        var tip = enabled
            ? null
            : "云同步未配置或未解锁，请到设置页连接";
        BtnSyncUpload.ToolTip = enabled ? "上传笔记到云端（沿用全量同步机制）" : tip;
        BtnSyncDownload.ToolTip = enabled ? "从云端拉取笔记到本地（仅拉不推）" : tip;
    }

    /// <summary>正在同步：禁用两个按钮，避免并发同步（SyncEngine 自身也有 SemaphoreSlim 闸）</summary>
    private void SetSyncButtonsRunning(bool running)
    {
        BtnSyncUpload.IsEnabled = !running;
        BtnSyncDownload.IsEnabled = !running;
        BtnSyncUpload.Content = running ? "上传中…" : "↑";
        BtnSyncDownload.Content = running ? "拉取中…" : "↓";
    }

    private void ShowSyncStatus(string text, bool error = false)
    {
        SyncStatusText.Text = text;
        SyncStatusText.Foreground = new SolidColorBrush(Color.FromRgb(
            error ? (byte)0xE5 : (byte)0x88,
            error ? (byte)0x73 : (byte)0xCC,
            error ? (byte)0x73 : (byte)0xCC));
    }

    /// <summary>标题栏 ↑ 按钮：触发一次全量推（与设置面板"立即同步"语义一致，机制不变）</summary>
    private async void BtnSyncUpload_Click(object sender, RoutedEventArgs e)
    {
        var engine = _syncEngineProvider?.Invoke();
        if (engine == null || !engine.IsMasterPasswordSet)
        {
            ShowSyncStatus("云同步未配置或未解锁，请到设置页连接", error: true);
            return;
        }
        SetSyncButtonsRunning(true);
        ShowSyncStatus("正在上传到云端（全量推）…");
        try
        {
            var result = await engine.SyncNowAsync(auto: false);
            ShowSyncStatus(result.Success ? "✓ 上传成功" : "✗ 上传失败：" + result.Error, error: !result.Success);
        }
        catch (Exception ex)
        {
            ShowSyncStatus("✗ 上传失败：" + ex.Message, error: true);
        }
        finally
        {
            SetSyncButtonsRunning(false);
        }
    }

    /// <summary>标题栏 ↓ 按钮：触发一次只 pull 不 push（避免把本机未确认内容顺手推上云端）</summary>
    private async void BtnSyncDownload_Click(object sender, RoutedEventArgs e)
    {
        var engine = _syncEngineProvider?.Invoke();
        if (engine == null || !engine.IsMasterPasswordSet)
        {
            ShowSyncStatus("云同步未配置或未解锁，请到设置页连接", error: true);
            return;
        }
        SetSyncButtonsRunning(true);
        ShowSyncStatus("正在从云端拉取…");
        try
        {
            var result = await engine.PullOnlyAsync();
            ShowSyncStatus(result.Success ? "✓ 拉取完成" : "✗ 拉取失败：" + result.Error, error: !result.Success);
        }
        catch (Exception ex)
        {
            ShowSyncStatus("✗ 拉取失败：" + ex.Message, error: true);
        }
        finally
        {
            SetSyncButtonsRunning(false);
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
        // v3.5：「设置提醒/取消提醒」仅待办条目可用（普通笔记置灰）。
        // 注意：MenuItem 在 ContextMenu 自己的 namescope 内，x:Name 不会提升为窗口字段，
        // 这里通过 cm.Items 遍历（按 Header 识别），避免直接字段访问。
        var isTodo = _contextTarget?.IsTodo == true;
        if (sender is ContextMenu cm)
        {
            foreach (var item in cm.Items.OfType<MenuItem>())
            {
                if (item.Header is string h && (h == "设置提醒…" || h == "取消提醒"))
                    item.IsEnabled = isTodo;
            }
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
            "（将移入回收站，30 天内可恢复）",
            "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        if (!_noteService.DeleteNote(vm.Entry))
        {
            System.Windows.MessageBox.Show("删除失败：未在笔记文件中找到该条目，可能已被外部修改", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        CancelEditState(vm);
        Refresh();   // 2026-08-15 修复：原 _viewModels.Remove + Items.Refresh() 对 ItemsSource=List 不触发 UI 重绘（List 无集合通知且 Items.Refresh 对 ItemsSource 模式无效），单条删除后不实时刷新；统一走 ReloadNotes 全量重载（与批量删除路径一致）
    }

    // ── v3.5 右键提醒（仅待办显示） ──

    /// <summary>右键「设置提醒」：弹出 yyyy-MM-dd HH:mm 输入对话框 → UpdateTodo 原地设提醒时间（正文带当前展示内容，防覆盖编辑）</summary>
    private void CtxSetDue_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextTarget(sender);
        if (vm == null || !vm.IsTodo) return;
        var dlg = new DueTimeDialog(vm.Entry.DueTime) { Owner = this };
        if (dlg.ShowDialog() != true || !dlg.DueTime.HasValue) return;
        if (_noteService.UpdateTodo(vm.Entry,
                newContent: vm.Entry.EditedContent ?? vm.Entry.Content,
                dueTime: dlg.DueTime.Value))
            Refresh();
        else
            System.Windows.MessageBox.Show("设置提醒失败：未在笔记文件中找到该条目", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>右键「取消提醒」：UpdateTodo 清除提醒时间（正文带当前展示内容，防覆盖编辑）</summary>
    private void CtxClearDue_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextTarget(sender);
        if (vm == null || !vm.IsTodo) return;
        if (_noteService.UpdateTodo(vm.Entry,
                newContent: vm.Entry.EditedContent ?? vm.Entry.Content,
                clearDue: true))
            Refresh();
        else
            System.Windows.MessageBox.Show("取消提醒失败：未在笔记文件中找到该条目", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>v3.5：待办徽标点击 → 已办（原地改行 + 落盘）。e.Handled 吞事件防冒泡触发条目复制；已办条目不重复点。</summary>
    private void TodoBadge_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.DataContext is NoteEntryViewModel vm)
        {
            e.Handled = true;
            if (!vm.IsTodo || vm.IsDone) return;
            if (_noteService.UpdateTodo(vm.Entry,
                    newContent: vm.Entry.EditedContent ?? vm.Entry.Content,
                    status: TodoStatus.Done))
                Refresh();
        }
    }

    // ── v3.5 筛选（类型多选 + 来源） ──

    private void TypeFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn || btn.Tag is not string tag) return;

        // 点「全部」：清空其余档位，只留 All
        if (tag == "All")
        {
            _typeFilter.Clear();
            _typeFilter.Add("All");
            FilterAll.IsChecked = true;
            FilterNote.IsChecked = false;
            FilterTodo.IsChecked = false;
            FilterDone.IsChecked = false;
        }
        else
        {
            _typeFilter.Remove("All");
            if (btn.IsChecked == true) _typeFilter.Add(tag);
            else _typeFilter.Remove(tag);
            // 四档全取消 → 自动回「全部」
            if (_typeFilter.Count == 0)
            {
                _typeFilter.Add("All");
                FilterAll.IsChecked = true;
            }
            else
            {
                FilterAll.IsChecked = false;
            }
        }
        ReloadNotes();
    }

    private void SourceFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSourceFilterEvent) return;
        _sourceFilter = (SourceFilter.SelectedItem as string) ?? "";
        ReloadNotes();
    }

    // ── v3.5 编辑时间识别建议条 ──

    /// <summary>显示建议条（编辑框区域上方浮出，10 秒自动消失）。文案用 TimeParser.FormatNaturalTime 生成。</summary>
    private void ShowSuggestBar(DateTime due)
    {
        SuggestText.Text = $"检测到{TimeParser.FormatNaturalTime(due)}，设为提醒？";
        // 定位：建议条浮在当前编辑条目容器上方（Canvas 覆盖层坐标）
        if (_suggestVm != null && NotesList.ItemContainerGenerator.ContainerFromItem(_suggestVm) is FrameworkElement container)
        {
            try
            {
                var p = container.TransformToVisual(FloatToolbarCanvas).Transform(new Point(0, 0));
                Canvas.SetLeft(SuggestBar, Math.Max(0, Math.Min(p.X, FloatToolbarCanvas.ActualWidth - SuggestBar.ActualWidth - 4)));
                Canvas.SetTop(SuggestBar, Math.Max(0, p.Y - SuggestBar.ActualHeight - 4));
            }
            catch { Canvas.SetLeft(SuggestBar, 10); Canvas.SetTop(SuggestBar, 10); }
        }
        else { Canvas.SetLeft(SuggestBar, 10); Canvas.SetTop(SuggestBar, 10); }

        SuggestBar.Visibility = Visibility.Visible;
        _suggestTimer?.Stop();
        _suggestTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _suggestTimer.Tick += (_, _) => { _suggestTimer.Stop(); HideSuggestBar(); };
        _suggestTimer.Start();
    }

    private void HideSuggestBar()
    {
        _suggestTimer?.Stop();
        SuggestBar.Visibility = Visibility.Collapsed;
        _suggestVm = null;
        _suggestDue = null;
    }

    /// <summary>设为提醒：UpdateTodo(newContent: vm.Content(已同步新正文), dueTime) 原地改行</summary>
    private void SuggestDueSet_Click(object sender, RoutedEventArgs e)
    {
        var vm = _suggestVm;
        var due = _suggestDue;
        HideSuggestBar();
        if (vm == null || !due.HasValue) return;
        if (_noteService.UpdateTodo(vm.Entry, newContent: vm.Entry.Content, dueTime: due.Value))
            Refresh();
    }

    /// <summary>忽略：仅收起建议条，编辑内容保留</summary>
    private void SuggestDueIgnore_Click(object sender, RoutedEventArgs e)
    {
        HideSuggestBar();
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
            $"编辑笔记 · {vm.Entry.Timestamp:yyyy-MM-dd HH:mm}", _aiProvider)
        { Owner = this };

        if (win.ShowDialog() == true)
            Refresh();
    }

    /// <summary>
    /// 保存编辑（v3.5 改造）：笔记走 AppendEdit 追加【编辑】行（现状不变）；待办走 TodoEditService.SaveEdited
    /// 原地改行（红线 2 例外，禁止追加【编辑】行）。保存成功后对待办做时间识别——规则优先（TimeParser），
    /// 规则未命中才调 LLM 兜底（DetectDueAsync），识别到未来时间弹建议条；未识别到时间不自动清除原提醒。
    /// async void：LLM 调用放后台线程（DetectDueAsync 内部），禁止 UI 线程同步阻塞等 LLM。
    /// </summary>
    private async void SaveEditNote(NoteEntryViewModel vm)
    {
        if (ImmersiveSessionService.IsLocked(vm.Entry.Timestamp))
        {
            System.Windows.MessageBox.Show("沉浸式输入进行中，暂不可编辑", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            vm.CancelEdit();
            return;
        }

        // 分离 AI 释义块（编辑时拼接显示，保存时剥离开来保持"MD 只增不减"结构）：
        // 主内容保存；释义行只能由 AI 对话框追加，编辑框内改动不写回存储
        var (contentToSave, _) = NoteEntryViewModel.SplitEditText(vm.EditText?.Trim() ?? "");
        if (string.IsNullOrEmpty(contentToSave))
        {
            System.Windows.MessageBox.Show("内容不能为空", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 内容未变化：不追加冗余【编辑】行也不改行
        var displayBase = vm.Entry.EditedContent ?? vm.Entry.Content;
        if (contentToSave != displayBase)
        {
            if (!TodoEditService.SaveEdited(_noteService, vm.Entry, contentToSave))
            {
                System.Windows.MessageBox.Show("保存失败：未在笔记文件中找到该条目，可能已被外部修改", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                vm.CancelEdit();
                return;
            }
            // 存储层状态同步：
            // - 待办原地改行 → Content 即新正文（供后续 UpdateTodo 定位用变更前字段）
            // - 笔记追加【编辑】行 → EditedContent 即展示新内容（原行不动）
            if (vm.Entry.Type == NoteType.Todo)
                vm.Entry.Content = contentToSave;
            else
                vm.Entry.EditedContent = contentToSave;
        }

        CancelEditState(vm);
        Refresh();

        // v3.5：待办时间识别建议（规则优先；规则未命中才调 LLM 兜底；未配 Key/异常 → 优雅降级不弹建议条不崩）
        if (vm.Entry.Type == NoteType.Todo)
        {
            var due = await TodoEditService.DetectDueAsync(contentToSave, _aiProvider);
            if (due.HasValue)
            {
                _suggestVm = vm;
                _suggestDue = due.Value;
                ShowSuggestBar(due.Value);
            }
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
                "（将移入回收站，30 天内可恢复）",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            if (!_noteService.DeleteNote(vm.Entry))
            {
                System.Windows.MessageBox.Show("删除失败：未在笔记文件中找到该条目，可能已被外部修改", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Refresh();   // 2026-08-15 修复：同上（单条 × 删除后实时刷新，原 Remove+Items.Refresh 对 ItemsSource=List 无效）
        }
    }

    private void BtnDeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedEntries();
        if (selected.Count == 0) return;

        var confirm = System.Windows.MessageBox.Show(
            $"确认删除选中的 {selected.Count} 条笔记？\n\n" +
            "（将移入回收站，30 天内可恢复）",
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
