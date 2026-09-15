using FocusCapture.Services;
using FocusCapture.Services.AI;
using FocusCapture.Services.Baidu;
using FocusCapture.Services.Destinations;
using FocusCapture.Services.Destinations.GetNote;
using FocusCapture.Services.Files;
using FocusCapture.Services.Sync;
using Microsoft.Win32;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FocusCapture.Windows;

public partial class SettingsWindow : Window
{
    private Models.AppSettings _settings = null!;
    private readonly HotkeyService? _hotkeyService;
    private readonly Action? _onChanged;
    private readonly NoteService? _noteService;
    private readonly Func<SyncEngine?>? _syncEngineProvider;   // 实时取 MainWindow 当前引擎（配置保存后由 MainWindow 重建）
    private readonly Func<ChatSyncEngine?>? _chatSyncEngineProvider; // 实时取当前会话同步引擎（状态实时刷新 + 重新拉取入口）
    private readonly Action? _onSyncConfigChanged;              // 保存 WebDAV 配置后通知 MainWindow 重建引擎
    private bool _capturing;
    private Action<Models.HotkeyBinding>? _onCaptureDone;
    private bool _suppressEvents = true; // 抑制 InitializeComponent 期间的 ValueChanged 事件
    private bool _testingAi;

    public SettingsWindow(Models.AppSettings s, HotkeyService? hk = null, Action? onChanged = null,
        NoteService? noteService = null, Func<SyncEngine?>? syncEngineProvider = null, Action? onSyncConfigChanged = null,
        Func<ChatSyncEngine?>? chatSyncEngineProvider = null)
    {
        _settings = s; _hotkeyService = hk; _onChanged = onChanged; _noteService = noteService;
        _syncEngineProvider = syncEngineProvider; _onSyncConfigChanged = onSyncConfigChanged;
        _chatSyncEngineProvider = chatSyncEngineProvider;
        InitializeComponent();
        _suppressEvents = false; // 初始化完成，允许事件处理
        BuildSearchIndex();
        ShowSection(0);
        LoadSettings(); KeyDown += OnKeyDown;
        // v3.8：录制热键期间全局热键被临时注销（见 StartCapture）——录制中途直接关窗时补回注册，
        // 否则用户的热键会整体失效且毫无提示（三个出口：正常录完 / Esc 取消 / 关窗，缺一不可）
        Closed += (_, _) => { if (_capturing) _hotkeyService?.RegisterAll(); };
        SubscribeChatSyncStatus();
    }

    /// <summary>订阅会话同步状态变化：设置页打开期间「AI 问答记录」状态实时刷新（2026-09-09 修复：
    /// 原先只在打开瞬间读一次留痕，会话同步跑的几分钟里界面一直停在旧文案，看起来像卡死）。</summary>
    private void SubscribeChatSyncStatus()
    {
        var chat = _chatSyncEngineProvider?.Invoke();
        if (chat == null) return;
        chat.StatusChanged += msg => Dispatcher.Invoke(() =>
        {
            if (!IsLoaded) return;
            SyncStatusText.Text = SyncStatusText.Text.Split('\n')[0] + $"\nAI 问答记录：{msg}";
        });
    }

    // ═══════ 板块导航 + 搜索（v3.7 设置大改版） ═══════

    /// <summary>搜索索引条目（由 BuildSearchIndex 自动扫描生成）</summary>
    private class SettingEntry
    {
        public required string Section { get; init; }
        public required int SectionIndex { get; init; }
        public required string Title { get; init; }
        public string Hint { get; set; } = "";
        public required FrameworkElement Target { get; init; }
        public string Subtitle => string.IsNullOrEmpty(Hint) ? Section : $"{Section} · {Hint}";
    }

    private readonly List<SettingEntry> _searchIndex = new();
    private readonly string[] _sectionNames = { "热键", "AI 模型", "外观", "显示", "灵感速览", "输入框", "云同步", "待办与提醒", "文件与网盘", "通用" };
    private bool _navSuppress; // 程序化切换导航选中项时抑制事件

    /// <summary>板块面板列表，顺序与 _sectionNames / 左侧导航一一对应</summary>
    private StackPanel[] SectionPanels() => new[]
    {
        PanelHotkey, PanelAi, PanelAppearance, PanelDisplay, PanelQuickView,
        PanelInput, PanelSync, PanelTodo, PanelFiles, PanelGeneral
    };

    /*
     * ══ 新增设置项的写死规则（自动扫描约定）══
     * 1. 每个板块面板（PanelXxx）的【直接子元素】视为一行设置项；
     *    紧跟在某行后面、且颜色为 #999999 的 TextBlock 视为该行的「说明文字」。
     * 2. 设置项名称取行内第一个【非灰色、非空】的 TextBlock 文字；
     *    独立摆放的非灰色 TextBlock 归为【下一行】的名称（如「笔记存储路径」）；
     *    都没有时回退取第一个 Button 的 Content 文字。
     * 3. 跳转/高亮目标取行内第一个可交互控件（Button/TextBox/PasswordBox/ComboBox/Slider/CheckBox/RadioButton）。
     * 4. 名称、说明、板块名都参与搜索匹配。
     * ⇒ 以后新增设置项只需把控件按上述结构加进对应板块面板的 XAML，搜索索引自动生效，无需改 C#。
     * ⇒ 新增板块才需要动三处：导航 ListBox 加一项、_sectionNames 加名字、SectionPanels() 加面板。
     */
    private void BuildSearchIndex()
    {
        _searchIndex.Clear();
        var panels = SectionPanels();
        for (int i = 0; i < panels.Length; i++)
        {
            SettingEntry? last = null;
            string? pendingTitle = null; // 板块内独立摆放的非灰色标签，归为下一行的名称（如「笔记存储路径」）
            foreach (var child in panels[i].Children.OfType<FrameworkElement>())
            {
                // 板块内独立的灰色小字 = 上一行的说明
                if (IsHintTextBlock(child, out var loneHint))
                {
                    if (last != null) last.Hint = loneHint;
                    continue;
                }
                // 板块内独立的非灰色标签 = 下一行的名称
                if (child is TextBlock label && !string.IsNullOrWhiteSpace(label.Text))
                {
                    pendingTitle = label.Text.Trim();
                    continue;
                }

                var descendants = Walk(child).OfType<FrameworkElement>().ToList();
                var target = descendants.FirstOrDefault(IsInteractiveControl);
                if (target == null) continue; // 纯装饰行（分隔线等）不入索引

                var title = pendingTitle
                    ?? descendants.OfType<TextBlock>()
                        .Where(t => !IsGray(t) && !string.IsNullOrWhiteSpace(t.Text))
                        .Select(t => t.Text.Trim())
                        .FirstOrDefault()
                    ?? (target is Button b ? b.Content?.ToString() ?? "" : "").Trim();
                if (title.Length == 0 && target is CheckBox cb) title = cb.Content?.ToString() ?? "";
                if (title.Length == 0) continue;
                pendingTitle = null;

                // 行内嵌的灰色小字也可作说明
                var inlineHint = descendants.OfType<TextBlock>()
                    .Where(t => IsGray(t) && !string.IsNullOrWhiteSpace(t.Text))
                    .Select(t => t.Text.Trim())
                    .FirstOrDefault() ?? "";

                last = new SettingEntry
                {
                    Section = _sectionNames[i], SectionIndex = i,
                    Title = title, Hint = inlineHint, Target = target
                };
                _searchIndex.Add(last);
            }
        }
    }

    private static bool IsInteractiveControl(FrameworkElement fe) => fe is Button or TextBox
        or PasswordBox or ComboBox or Slider or CheckBox or RadioButton;

    private static bool IsGray(TextBlock t) =>
        t.Foreground is SolidColorBrush b && b.Color == Color.FromRgb(0x99, 0x99, 0x99);

    private static bool IsHintTextBlock(FrameworkElement fe, out string text)
    {
        if (fe is TextBlock t && IsGray(t) && !string.IsNullOrWhiteSpace(t.Text))
        { text = t.Text.Trim(); return true; }
        text = ""; return false;
    }

    /// <summary>先序遍历可视化树（含自身）</summary>
    private static IEnumerable<DependencyObject> Walk(DependencyObject node)
    {
        yield return node;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var d in Walk(VisualTreeHelper.GetChild(node, i)))
                yield return d;
    }

    /// <summary>切换到指定板块（右侧只显示对应面板）</summary>
    private void ShowSection(int index)
    {
        var panels = SectionPanels();
        for (int i = 0; i < panels.Length; i++)
            panels[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        SectionTitle.Text = _sectionNames[index];
        ContentScroller.ScrollToTop();
        _navSuppress = true;
        NavList.SelectedIndex = index;
        _navSuppress = false;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navSuppress || NavList.SelectedIndex < 0) return;
        ShowSection(NavList.SelectedIndex);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        SearchWatermark.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnClearSearch.Visibility = q.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (q.Length == 0)
        {
            SearchResultsPanel.Visibility = Visibility.Collapsed;
            NavList.Visibility = Visibility.Visible;
            return;
        }
        var matches = _searchIndex
            .Where(x => x.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || x.Hint.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || x.Section.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SearchCountText.Text = matches.Count > 0 ? $"{matches.Count} 个匹配项" : "无匹配项";
        SearchResults.ItemsSource = matches;
        NavList.Visibility = Visibility.Collapsed;
        SearchResultsPanel.Visibility = Visibility.Visible;
    }

    private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        Keyboard.Focus(SearchBox);
    }

    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResults.SelectedItem is not SettingEntry entry) return;
        SearchResults.SelectedIndex = -1; // 允许重复点击同一结果
        ShowSection(entry.SectionIndex);
        HighlightEntry(entry);
    }

    /// <summary>跳转到设置项：滚动定位 + 所在行短暂绿色高亮</summary>
    private void HighlightEntry(SettingEntry entry)
    {
        var target = entry.Target;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            target.BringIntoView();

            // 向上找到「板块面板的直接子元素」作为整行高亮宿主
            DependencyObject node = target;
            FrameworkElement? row = null;
            while (node != null)
            {
                if (node is FrameworkElement fe && VisualTreeHelper.GetParent(fe) is StackPanel sp
                    && sp.Name.StartsWith("Panel", StringComparison.Ordinal))
                { row = fe; break; }
                node = VisualTreeHelper.GetParent(node);
            }
            if (row is not Panel p) return;

            var brush = new SolidColorBrush(Color.FromArgb(0x55, 0x4C, 0xAF, 0x50));
            p.Background = brush;
            var anim = new ColorAnimation(
                Color.FromArgb(0x55, 0x4C, 0xAF, 0x50), Colors.Transparent,
                new Duration(TimeSpan.FromSeconds(1.5)))
            { FillBehavior = FillBehavior.Stop };
            anim.Completed += (_, _) => p.Background = null;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void LoadSettings()
    {
        _suppressEvents = true;
        BtnSummonHotkey.Content = Win32.HotkeyToString(_settings.SummonHotkey);
        BtnClipboardHotkey.Content = Win32.HotkeyToString(_settings.ClipboardToggleHotkey);
        BtnQuickViewHotkey.Content = Win32.HotkeyToString(_settings.QuickViewHotkey);
        BtnVoiceInputHotkey.Content = Win32.HotkeyToString(_settings.VoiceInputHotkey);
        BtnSettingsHotkey.Content = Win32.HotkeyToString(_settings.SettingsHotkey);
        BtnAiAskHotkey.Content = Win32.HotkeyToString(_settings.AiAskHotkey);
        BtnTodoSummaryHotkey.Content = Win32.HotkeyToString(_settings.TodoSummaryHotkey);
        InputOpacitySlider.Value = _settings.InputOpacity;
        BallOpacitySlider.Value = _settings.FloatBallOpacity;
        QuickViewOpacitySlider.Value = _settings.QuickViewOpacity;
        InputOpacityLabel.Text = $"{(int)(_settings.InputOpacity * 100)}%";
        BallOpacityLabel.Text = $"{(int)(_settings.FloatBallOpacity * 100)}%";
        QuickViewOpacityLabel.Text = $"{(int)(_settings.QuickViewOpacity * 100)}%";
        NotesPathText.Text = _settings.NotesPath;
        AutoStartCheck.IsChecked = _settings.AutoStart;
        // v3.8：灵感速览唤出行为下拉回显（v3.9 起在「灵感速览」板块）
        foreach (ComboBoxItem it in QuickViewSummonCombo.Items)
            if ((string)it.Tag == (_settings.QuickViewRestoreLastFilter ? "Restore" : "Today"))
                QuickViewSummonCombo.SelectedItem = it;
        // v3.9：灵感速览面板宽度 / 置顶 / 标题栏自定义编辑器回显
        QuickViewWidthInput.Text = ((int)Math.Clamp(_settings.QuickViewWidth,
            QuickViewWindow.MinWidthLimit, QuickViewWindow.MaxWidthLimit)).ToString();
        QuickViewTopmostCheck.IsChecked = _settings.QuickViewTopmost;
        RebuildToolbarEditor();
        // 供应商下拉：6 预设 + 自定义（Tag=null 表示自定义）
        AiProviderCombo.Items.Clear();
        foreach (var p in AiProviders.Presets)
            AiProviderCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p });
        AiProviderCombo.Items.Add(new ComboBoxItem { Content = AiProviders.Custom, Tag = null });

        // 图片清晰度档位：索引即档位值（0 省流 / 1 标准 / 2 高清），与 ChatAttachmentService.QualitySpec 对应
        AiImageQualityCombo.Items.Clear();
        AiImageQualityCombo.Items.Add(new ComboBoxItem { Content = "省流（长边 768）" });
        AiImageQualityCombo.Items.Add(new ComboBoxItem { Content = "标准（长边 1568）" });
        AiImageQualityCombo.Items.Add(new ComboBoxItem { Content = "高清（长边 2048）" });

        AiBaseUrlInput.Text = _settings.AiBaseUrl;
        AiApiKeyInput.Password = _settings.AiApiKey;
        AiModelInput.Text = _settings.AiModel;
        AiMaxTokensInput.Text = _settings.AiMaxTokens.ToString();
        AiAssistantNameInput.Text = _settings.AiAssistantName;
        AgentEnabledCheck.IsChecked = _settings.AgentEnabled;
        AgentWriteConfirmCheck.IsChecked = _settings.AgentWriteConfirmPopup;
        AgentMaxToolRoundsInput.Text = _settings.AgentMaxToolRounds.ToString();
        AiToolResultLimitInput.Text = _settings.AiToolResultLimit.ToString();
        AiImageQualityCombo.SelectedIndex = Math.Clamp(_settings.AiImageQualityLevel, 0, 2);
        AiVisionEnabledCheck.IsChecked = _settings.AiVisionEnabled;
        GetNoteKeyInput.Password = _settings.GetNoteApiKey;
        GetNoteClientIdInput.Text = _settings.GetNoteClientId;
        GetNoteTestResult.Text = "";
        LogRetentionInput.Text = _settings.LogRetentionDays.ToString();
        AiTestResult.Text = "";
        AiTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

        // 按 BaseUrl 反推选中项（匹配不上→自定义）
        SelectProviderByUrl(_settings.AiBaseUrl);
        UpdateIconUI();
        LoadSyncSettings();
        LoadFileSettings();
        LoadTodoSettings();
        LoadInputSettings();
        _suppressEvents = false;
        RefreshHotkeyWarning();           // v3.8：回显上次注册失败的键位（多为被其他程序占用）
        _ = LoadGetNoteTopicsQuietly();   // 凭证已配置时后台拉取知识库列表（不阻塞设置打开）
    }

    /// <summary>v3.8：注册失败提示——键位被其他程序占用时 RegisterHotKey 会失败，
    /// 用户表现为「按了没反应」。此处把 HotkeyService 留痕的失败项显式列在热键板块。</summary>
    private void RefreshHotkeyWarning()
    {
        var fails = _hotkeyService?.LastFailures;
        if (fails == null || fails.Count == 0)
        {
            HotkeyWarningBorder.Visibility = Visibility.Collapsed;
            return;
        }
        HotkeyWarningText.Text = "以下快捷键注册失败（可能已被其他程序占用），请换一组："
            + string.Join("、", fails.Select(f => $"{f.Name}（{f.Keys}）"));
        HotkeyWarningBorder.Visibility = Visibility.Visible;
    }

    /// <summary>v3.6 输入框设置回填（自动隐藏模式/秒数 + 位置记忆）</summary>
    private void LoadInputSettings()
    {
        AlwaysVisibleRadio.IsChecked = _settings.InputAlwaysVisible;
        CustomHideRadio.IsChecked = !_settings.InputAlwaysVisible;
        AutoHideSecondsInput.Text = _settings.InputAutoHideSeconds.ToString();
        AutoHideSecondsInput.IsEnabled = !_settings.InputAlwaysVisible;
        RememberPositionCheck.IsChecked = _settings.InputRememberPosition;
    }

    // ── v3.6 输入框：自动隐藏与位置记忆（改即保存） ──

    private void AutoHideMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.InputAlwaysVisible = AlwaysVisibleRadio.IsChecked == true;
        AutoHideSecondsInput.IsEnabled = !_settings.InputAlwaysVisible;
        _settings.Save();
    }

    /// <summary>自定义秒数：输入过程中合法即保存（≥3 整数，上不封顶）；失焦时非法才回退原值，避免打断多位数输入</summary>
    private void AutoHideSeconds_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = AutoHideSecondsInput.Text.Trim();
        if (int.TryParse(v, out var n) && n >= 3) { _settings.InputAutoHideSeconds = n; _settings.Save(); }
    }

    private void AutoHideSeconds_LostFocus(object sender, RoutedEventArgs e)
    {
        var v = AutoHideSecondsInput.Text.Trim();
        if (!int.TryParse(v, out var n) || n < 3)
            AutoHideSecondsInput.Text = _settings.InputAutoHideSeconds.ToString();
    }

    private void RememberPosition_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.InputRememberPosition = RememberPositionCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>v3.5 待办与提醒设置回填</summary>
    private void LoadTodoSettings()
    {
        DefaultTypeCombo.SelectedIndex = _settings.InputDefaultType == "Todo" ? 1 : 0;
        BtnTodoSwitchHotkey.Content = Win32.HotkeyToString(_settings.TodoSwitchHotkey);
        DailySummaryCheck.IsChecked = _settings.DailySummaryEnabled;
        DailySummaryEmptyCheck.IsChecked = _settings.DailySummaryEmptyPopup;
        DailySummaryTimeInput.Text = _settings.DailySummaryTime;
        SnoozeMinutesInput.Text = _settings.SnoozeMinutes.ToString();
        PopupCloseSecondsInput.Text = _settings.PopupAutoCloseSeconds.ToString();
        AskTimeCheck.IsChecked = _settings.AskTimeForDateOnly;
    }

    /// <summary>云同步设置回填（方案A 2026-08-15：授权码自动解锁，无主密码/恢复码）</summary>
    private void LoadSyncSettings()
    {
        SyncUrlInput.Text = _settings.Sync.WebDavUrl;
        SyncUserInput.Text = _settings.Sync.WebDavUser;
        AutoSyncCheck.IsChecked = _settings.Sync.AutoSyncEnabled;
        MergeWindowInput.Text = _settings.Sync.MergeWindowSeconds.ToString();
        ChatSyncCheck.IsChecked = _settings.Sync.ChatSyncEnabled;
        var engine = _syncEngineProvider?.Invoke();
        var unlocked = engine?.IsMasterPasswordSet == true;
        SyncStatusText.Text = unlocked
            ? $"已解锁自动同步 · 上次同步：{_settings.Sync.LastSyncAt} {_settings.Sync.LastSyncResult}"
            : engine?.IsLegacyMasterPasswordMode == true
                ? "检测到旧版主密码配置，点击『保存并连接』一键升级（无需原主密码）"
                : string.IsNullOrEmpty(_settings.Sync.E2eeSalt)
                    ? "未配置云同步（首次点击『保存并连接』即完成配置）"
                    : "授权码已保存，应用启动后自动解锁（重新填写授权码可更换）";
        // 会话同步失败留痕持续可见（不允许无声丢失；成功/等待首配等状态也在此展示）
        if (!string.IsNullOrEmpty(_settings.Sync.ChatSyncResult))
            SyncStatusText.Text += $"\nAI 问答记录：{_settings.Sync.ChatSyncResult}";

        ChatFilesCheck.IsChecked = _settings.Sync.FileSyncEnabled;
        if (!string.IsNullOrEmpty(_settings.Sync.FileSyncResult))
            SyncStatusText.Text += $"\n网盘文件清单：{_settings.Sync.FileSyncResult}";
    }

    private void StartCapture(Button btn, Action<Models.HotkeyBinding> done)
    {
        if (_capturing) return;
        _capturing = true; _onCaptureDone = done;
        // v3.8：录制期间临时注销全部全局热键——否则按下已注册的组合键会当场触发它（例如录 Ctrl+Alt+V 就弹速览）。
        // 恢复点共三个：录完走 _onChanged → RegisterAll、Esc 走 CancelCapture、关窗走 Closed（都要有）。
        _hotkeyService?.UnregisterAll();
        btn.Content = "按下新快捷键…";
        btn.Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        Keyboard.Focus(btn);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        var m = Keyboard.Modifiers;
        if (m == ModifierKeys.None && e.Key != Key.Escape) return;
        e.Handled = true;
        if (e.Key == Key.Escape) { CancelCapture(); return; }
        if (e.Key is Key.System or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var hk = new Models.HotkeyBinding
        {
            Modifiers = (int)m,
            Key = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key)
        };
        _onCaptureDone?.Invoke(hk); _capturing = false; _onCaptureDone = null;
        _onChanged?.Invoke();
        RefreshHotkeyWarning();   // v3.8：注册在 _onChanged 内完成，之后才能读到本次失败清单
    }

    private void CancelCapture()
    {
        _capturing = false; _onCaptureDone = null;
        _hotkeyService?.RegisterAll();   // v3.8：Esc 取消必须补回注册（StartCapture 已临时注销），否则所有热键失效
        LoadSettings();
    }

    private void DoneCapture(Button btn, Models.HotkeyBinding hk)
    {
        btn.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        _settings.Save();
    }

    private void BtnSummon_Click(object sender, RoutedEventArgs e) => StartCapture(BtnSummonHotkey, hk =>
    { _settings.SummonHotkey = hk; BtnSummonHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnSummonHotkey, hk); });
    private void BtnClipboard_Click(object sender, RoutedEventArgs e) => StartCapture(BtnClipboardHotkey, hk =>
    { _settings.ClipboardToggleHotkey = hk; BtnClipboardHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnClipboardHotkey, hk); });
    private void BtnQuickView_Click(object sender, RoutedEventArgs e) => StartCapture(BtnQuickViewHotkey, hk =>
    { _settings.QuickViewHotkey = hk; BtnQuickViewHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnQuickViewHotkey, hk); });
    private void BtnVoiceInput_Click(object sender, RoutedEventArgs e) => StartCapture(BtnVoiceInputHotkey, hk =>
    { _settings.VoiceInputHotkey = hk; BtnVoiceInputHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnVoiceInputHotkey, hk); });
    private void BtnSettings_Click(object sender, RoutedEventArgs e) => StartCapture(BtnSettingsHotkey, hk =>
    { _settings.SettingsHotkey = hk; BtnSettingsHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnSettingsHotkey, hk); });
    private void BtnAiAsk_Click(object sender, RoutedEventArgs e) => StartCapture(BtnAiAskHotkey, hk =>
    { _settings.AiAskHotkey = hk; BtnAiAskHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnAiAskHotkey, hk); });
    private void BtnTodoSummary_Click(object sender, RoutedEventArgs e) => StartCapture(BtnTodoSummaryHotkey, hk =>
    { _settings.TodoSummaryHotkey = hk; BtnTodoSummaryHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnTodoSummaryHotkey, hk); });

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        _settings.SummonHotkey = new() { Modifiers = 1, Key = 0x20 };
        _settings.ClipboardToggleHotkey = new() { Modifiers = 3, Key = 0x70 };
        _settings.QuickViewHotkey = new() { Modifiers = 3, Key = 0x56 };
        _settings.VoiceInputHotkey = new() { Modifiers = 3, Key = 0x52 };
        _settings.SettingsHotkey = new() { Modifiers = 3, Key = 0x53 };
        _settings.AiAskHotkey = new() { Modifiers = 3, Key = 0x41 };
        _settings.TodoSummaryHotkey = new() { Modifiers = 3, Key = 0x54 };
        _settings.Save(); LoadSettings(); _onChanged?.Invoke(); RefreshHotkeyWarning();
    }

    // ── v3.5 待办与提醒（改即保存 + 即时校验） ──

    private void DefaultType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.InputDefaultType = DefaultTypeCombo.SelectedIndex == 1 ? "Todo" : "Note";
        _settings.Save();
    }

    private void BtnTodoSwitch_Click(object sender, RoutedEventArgs e) => StartCapture(BtnTodoSwitchHotkey, hk =>
    { _settings.TodoSwitchHotkey = hk; BtnTodoSwitchHotkey.Content = Win32.HotkeyToString(hk); DoneCapture(BtnTodoSwitchHotkey, hk); });

    private void DailySummary_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.DailySummaryEnabled = DailySummaryCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>v3.7：当天无待办是否仍弹每日汇总弹窗（默认弹）</summary>
    private void DailySummaryEmpty_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.DailySummaryEmptyPopup = DailySummaryEmptyCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>纯日期是否弹窗问几点（取消=默认当天 09:00）</summary>
    private void AskTime_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AskTimeForDateOnly = AskTimeCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>汇总时间：输入过程中合法（HH:mm，00:00~23:59）即保存；失焦时非法才回退原值，避免打断输入</summary>
    private void DailySummaryTime_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = DailySummaryTimeInput.Text.Trim();
        if (IsValidDailyTime(v)) { _settings.DailySummaryTime = v; _settings.Save(); }
    }

    private void DailySummaryTime_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsValidDailyTime(DailySummaryTimeInput.Text.Trim()))
            DailySummaryTimeInput.Text = _settings.DailySummaryTime;
    }

    private static bool IsValidDailyTime(string v)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d{1,2}:\d{2}$")) return false;
        var parts = v.Split(':');
        return int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)
            && h >= 0 && h <= 23 && m >= 0 && m <= 59;
    }

    /// <summary>稍后提醒分钟数：输入过程中合法（正整数）即保存；失焦时非法才回退原值</summary>
    private void SnoozeMinutes_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = SnoozeMinutesInput.Text.Trim();
        if (int.TryParse(v, out var n) && n > 0) { _settings.SnoozeMinutes = n; _settings.Save(); }
    }

    private void SnoozeMinutes_LostFocus(object sender, RoutedEventArgs e)
    {
        var v = SnoozeMinutesInput.Text.Trim();
        if (!int.TryParse(v, out var n) || n <= 0)
            SnoozeMinutesInput.Text = _settings.SnoozeMinutes.ToString();
    }

    private void PopupCloseSeconds_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = PopupCloseSecondsInput.Text.Trim();
        if (int.TryParse(v, out var n) && n > 0) { _settings.PopupAutoCloseSeconds = n; _settings.Save(); }
    }

    private void PopupCloseSeconds_LostFocus(object sender, RoutedEventArgs e)
    {
        var v = PopupCloseSecondsInput.Text.Trim();
        if (!int.TryParse(v, out var n) || n <= 0)
            PopupCloseSecondsInput.Text = _settings.PopupAutoCloseSeconds.ToString();
    }

    private void InputOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_suppressEvents) return; _settings.InputOpacity = e.NewValue; InputOpacityLabel.Text = $"{(int)(e.NewValue * 100)}%"; _settings.Save(); _onChanged?.Invoke(); }
    private void BallOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_suppressEvents) return; _settings.FloatBallOpacity = e.NewValue; BallOpacityLabel.Text = $"{(int)(e.NewValue * 100)}%"; _settings.Save(); _onChanged?.Invoke(); }
    private void QuickViewOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_suppressEvents) return; _settings.QuickViewOpacity = e.NewValue; QuickViewOpacityLabel.Text = $"{(int)(e.NewValue * 100)}%"; _settings.Save(); _onChanged?.Invoke(); }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    { if (_suppressEvents) return; _settings.AutoStart = AutoStartCheck.IsChecked == true; SetAutoStart(_settings.AutoStart); _settings.Save(); }

    // v3.8：灵感速览唤出行为（当天 / 恢复上次筛选），QuickViewWindow.Show 时按此项决定是否重置
    private void QuickViewSummon_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (QuickViewSummonCombo.SelectedItem is ComboBoxItem it)
        {
            _settings.QuickViewRestoreLastFilter = (string)it.Tag == "Restore";
            _settings.Save();
        }
    }

    // ── v3.9 灵感速览板块：面板宽度 / 置顶 / 标题栏自定义编辑器 ──
    // 布局预算与配置清洗都在 QuickViewToolbarCatalog（纯逻辑，tests 快层直链覆盖）。
    // 每次变更：写设置 → 落盘 → 刷新编辑器 → _onChanged（MainWindow → QuickViewWindow.ApplySettings 即时生效）。

    private void QuickViewWidth_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!int.TryParse(QuickViewWidthInput.Text.Trim(), out var w)) return;   // 输入中途不处理，失焦回退
        var clamped = (int)Math.Clamp(w, QuickViewWindow.MinWidthLimit, QuickViewWindow.MaxWidthLimit);
        if (_settings.QuickViewWidth != clamped)
        {
            _settings.QuickViewWidth = clamped;
            _settings.Save();
            _onChanged?.Invoke();
        }
        RefreshToolbarBudget();   // 宽度变了，标题栏预算随之变化
    }

    private void QuickViewWidth_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(QuickViewWidthInput.Text.Trim(), out var w)
            || w < (int)QuickViewWindow.MinWidthLimit || w > (int)QuickViewWindow.MaxWidthLimit)
            QuickViewWidthInput.Text = ((int)Math.Clamp(_settings.QuickViewWidth,
                QuickViewWindow.MinWidthLimit, QuickViewWindow.MaxWidthLimit)).ToString();
    }

    private void QuickViewTopmost_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.QuickViewTopmost = QuickViewTopmostCheck.IsChecked == true;
        _settings.Save();
        _onChanged?.Invoke();
    }

    /// <summary>重建标题栏自定义编辑器：两个已启用列表 + 两个「可用功能」下拉 + 预算文字。</summary>
    private void RebuildToolbarEditor()
    {
        var (left, right) = QuickViewToolbarCatalog.Sanitize(_settings.QuickViewToolbarLeft, _settings.QuickViewToolbarRight);

        FillToolbarList(ToolbarLeftList, left);
        FillToolbarList(ToolbarRightList, right);

        FillToolbarCombo(ToolbarLeftAddCombo, left, right, isLeft: true);
        FillToolbarCombo(ToolbarRightAddCombo, left, right, isLeft: false);
        RefreshToolbarBudget();
    }

    private void FillToolbarList(ListBox list, List<string> ids)
    {
        var selectedId = (list.SelectedItem as ListBoxItem)?.Tag as string;
        list.Items.Clear();
        for (var i = 0; i < ids.Count; i++)
        {
            var def = QuickViewToolbarCatalog.Find(ids[i]);
            if (def == null) continue;
            list.Items.Add(new ListBoxItem
            {
                Content = $"{i + 1}. {def.Label}",
                Tag = def.Id,
                IsSelected = def.Id == selectedId,
                Padding = new Thickness(6, 3, 6, 3),
            });
        }
    }

    /// <summary>「可用功能」下拉：只列两侧都未启用的功能；预算放不下的项禁用并注明。
    /// 下拉为空是**正常情况**（功能池已全部启用完），此时显示提示并把添加按钮/下拉一并置灰 ——
    /// 否则用户点了发现毫无反应，只会以为界面坏了（2026-09-13 实际反馈）。</summary>
    private void FillToolbarCombo(ComboBox combo, List<string> left, List<string> right, bool isLeft)
    {
        combo.Items.Clear();
        foreach (var def in QuickViewToolbarCatalog.All)
        {
            if (left.Contains(def.Id) || right.Contains(def.Id)) continue;
            var canAdd = QuickViewToolbarCatalog.CanAdd(_settings.QuickViewWidth, left, right, def.Id);
            var item = new ComboBoxItem
            {
                Content = canAdd ? def.Label : $"{def.Label}（空间不足）",
                Tag = def.Id,
                IsEnabled = canAdd,
            };
            combo.Items.Add(item);
        }

        var hasAvailable = combo.Items.Count > 0;
        combo.SelectedIndex = hasAvailable ? 0 : -1;
        combo.IsEnabled = hasAvailable;
        (isLeft ? ToolbarLeftEmptyHint : ToolbarRightEmptyHint).Visibility =
            hasAvailable ? Visibility.Collapsed : Visibility.Visible;
        (isLeft ? ToolbarLeftAddBtn : ToolbarRightAddBtn).IsEnabled = hasAvailable;
    }

    /// <summary>预算文字：已用 / 可用像素；超出预算红字提示（宽度过低 + 按钮过多会标题栏重叠）。</summary>
    private void RefreshToolbarBudget()
    {
        var (left, right) = QuickViewToolbarCatalog.Sanitize(_settings.QuickViewToolbarLeft, _settings.QuickViewToolbarRight);
        var used = QuickViewToolbarCatalog.UsedBudget(left, right);
        var avail = QuickViewToolbarCatalog.AvailableBudget(_settings.QuickViewWidth);
        ToolbarBudgetText.Text = $"已用 {used:0} / 可用 {avail:0} px"
            + (used > avail + 0.01 ? "（空间不足：标题栏按钮可能重叠，建议加宽面板或移除按钮）" : "");
        ToolbarBudgetText.Foreground = used > avail + 0.01
            ? new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35))
            : new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    }

    /// <summary>编辑动作统一出口：写回设置并落盘、刷新编辑器、通知 MainWindow 让面板即时重建。</summary>
    private void CommitToolbarConfig()
    {
        _settings.Save();
        RebuildToolbarEditor();
        _onChanged?.Invoke();
    }

    private static List<string> SideList(List<string> left, List<string> right, bool isLeft) => isLeft ? left : right;

    private (List<string> left, List<string> right) WorkingLists()
        => QuickViewToolbarCatalog.Sanitize(_settings.QuickViewToolbarLeft, _settings.QuickViewToolbarRight);

    private void ToolbarAdd_Click(bool isLeft)
    {
        var combo = isLeft ? ToolbarLeftAddCombo : ToolbarRightAddCombo;
        if (combo.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        var (left, right) = WorkingLists();
        if (!QuickViewToolbarCatalog.CanAdd(_settings.QuickViewWidth, left, right, id)) return;
        SideList(left, right, isLeft).Add(id);
        _settings.QuickViewToolbarLeft = left;
        _settings.QuickViewToolbarRight = right;
        CommitToolbarConfig();
    }

    private void ToolbarMove_Click(bool isLeft, int delta)
    {
        var list = isLeft ? ToolbarLeftList : ToolbarRightList;
        if (list.SelectedItem is not ListBoxItem { Tag: string id }) return;
        var (left, right) = WorkingLists();
        var ids = SideList(left, right, isLeft);
        var idx = ids.IndexOf(id);
        var next = idx + delta;
        if (idx < 0 || next < 0 || next >= ids.Count) return;
        (ids[idx], ids[next]) = (ids[next], ids[idx]);
        _settings.QuickViewToolbarLeft = left;
        _settings.QuickViewToolbarRight = right;
        CommitToolbarConfig();
        // 重建后按 id 恢复选中，支持连续点击上移/下移
        SelectInList(isLeft ? ToolbarLeftList : ToolbarRightList, id);
    }

    private void ToolbarRemove_Click(bool isLeft)
    {
        var list = isLeft ? ToolbarLeftList : ToolbarRightList;
        if (list.SelectedItem is not ListBoxItem { Tag: string id }) return;
        var (left, right) = WorkingLists();
        SideList(left, right, isLeft).Remove(id);
        _settings.QuickViewToolbarLeft = left;
        _settings.QuickViewToolbarRight = right;
        CommitToolbarConfig();
    }

    private static void SelectInList(ListBox list, string id)
    {
        foreach (ListBoxItem item in list.Items)
            if ((string)item.Tag == id) { item.IsSelected = true; return; }
    }

    private void ToolbarLeftAdd_Click(object sender, RoutedEventArgs e) => ToolbarAdd_Click(isLeft: true);
    private void ToolbarLeftUp_Click(object sender, RoutedEventArgs e) => ToolbarMove_Click(isLeft: true, delta: -1);
    private void ToolbarLeftDown_Click(object sender, RoutedEventArgs e) => ToolbarMove_Click(isLeft: true, delta: +1);
    private void ToolbarLeftRemove_Click(object sender, RoutedEventArgs e) => ToolbarRemove_Click(isLeft: true);
    private void ToolbarRightAdd_Click(object sender, RoutedEventArgs e) => ToolbarAdd_Click(isLeft: false);
    private void ToolbarRightUp_Click(object sender, RoutedEventArgs e) => ToolbarMove_Click(isLeft: false, delta: -1);
    private void ToolbarRightDown_Click(object sender, RoutedEventArgs e) => ToolbarMove_Click(isLeft: false, delta: +1);
    private void ToolbarRightRemove_Click(object sender, RoutedEventArgs e) => ToolbarRemove_Click(isLeft: false);

    private void BtnToolbarReset_Click(object sender, RoutedEventArgs e)
    {
        _settings.QuickViewToolbarLeft = QuickViewToolbarCatalog.DefaultLeft.ToList();
        _settings.QuickViewToolbarRight = QuickViewToolbarCatalog.DefaultRight.ToList();
        CommitToolbarConfig();
    }

    private void AiBaseUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AiBaseUrl = AiBaseUrlInput.Text.Trim();
        _settings.Save();
        SelectProviderByUrl(_settings.AiBaseUrl);   // 手改 BaseUrl 后反推：匹配不上自动切「自定义」
    }

    private void AiApiKey_PasswordChanged(object sender, RoutedEventArgs e)
    { if (_suppressEvents) return; _settings.AiApiKey = AiApiKeyInput.Password; _settings.Save(); }

    private void AiModel_TextChanged(object sender, TextChangedEventArgs e)
    { if (_suppressEvents) return; _settings.AiModel = AiModelInput.Text.Trim(); _settings.Save(); }

    private void AgentEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AgentEnabled = AgentEnabledCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>图片清晰度档位（下拉索引即档位值）</summary>
    private void AiImageQuality_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var idx = AiImageQualityCombo.SelectedIndex;
        if (idx < 0) return;
        _settings.AiImageQualityLevel = idx;
        _settings.Save();
    }

    /// <summary>允许发送图片总开关（关掉后问答窗口会拦下粘贴的图片并提示）</summary>
    private void AiVisionEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AiVisionEnabled = AiVisionEnabledCheck.IsChecked == true;
        _settings.Save();
    }

    private void AgentWriteConfirm_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AgentWriteConfirmPopup = AgentWriteConfirmCheck.IsChecked == true;
        _settings.Save();
    }

    // ── 得到大脑凭证（按钮「存到得到大脑」与对话推送共用，改即保存） ──

    private void GetNoteKey_PasswordChanged(object sender, RoutedEventArgs e)
    { if (_suppressEvents) return; _settings.GetNoteApiKey = GetNoteKeyInput.Password; _settings.Save(); }

    private void GetNoteClientId_TextChanged(object sender, TextChangedEventArgs e)
    { if (_suppressEvents) return; _settings.GetNoteClientId = GetNoteClientIdInput.Text.Trim(); _settings.Save(); }

    private async void BtnGetNoteTest_Click(object sender, RoutedEventArgs e)
    {
        _settings.GetNoteApiKey = GetNoteKeyInput.Password;   // 兜底落盘（正常已由输入事件保存）
        _settings.GetNoteClientId = GetNoteClientIdInput.Text.Trim();
        _settings.Save();

        BtnGetNoteTest.IsEnabled = false;
        GetNoteTestResult.Text = "正在连接…";
        try
        {
            var (ok, msg, topics) = await new GetNoteDestination(_settings).TestConnectionAsync();
            GetNoteTestResult.Text = msg;
            GetNoteTestResult.Foreground = new SolidColorBrush(
                ok ? Color.FromRgb(0x4C, 0xAF, 0x50) : Color.FromRgb(0xEF, 0x53, 0x50));
            if (ok) FillTopicCombo(topics);
        }
        catch (Exception ex)
        {
            GetNoteTestResult.Text = $"连接出错：{ex.Message}";
            GetNoteTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
        }
        finally
        {
            BtnGetNoteTest.IsEnabled = true;
        }
    }

    /// <summary>回填知识库下拉：首项=账号默认库，其余来自 knowledge/list；命中已存默认库则选中</summary>
    private void FillTopicCombo(List<GetNoteDestination.GetNoteTopic> topics)
    {
        var prev = _suppressEvents;
        _suppressEvents = true;
        try
        {
            CmbGetNoteTopic.Items.Clear();
            CmbGetNoteTopic.Items.Add(new ComboBoxItem { Content = "（账号默认库）", Tag = "" });
            foreach (var t in topics)
                CmbGetNoteTopic.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Id });

            var idx = 0;
            for (var i = 1; i < CmbGetNoteTopic.Items.Count; i++)
                if ((CmbGetNoteTopic.Items[i] as ComboBoxItem)?.Tag as string == _settings.GetNoteDefaultTopicId)
                { idx = i; break; }
            CmbGetNoteTopic.SelectedIndex = idx;
        }
        finally { _suppressEvents = prev; }
    }

    private void CmbGetNoteTopic_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var item = CmbGetNoteTopic.SelectedItem as ComboBoxItem;
        _settings.GetNoteDefaultTopicId = item?.Tag as string ?? "";
        _settings.GetNoteDefaultTopicName = item is { Tag: string id } && id.Length > 0
            ? item.Content as string ?? ""
            : "";
        _settings.Save();
    }

    /// <summary>凭证已配置时打开设置后台拉取知识库列表（失败静默：下拉保持已存默认库，不打断设置使用）</summary>
    private async Task LoadGetNoteTopicsQuietly()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.GetNoteApiKey) ||
                string.IsNullOrWhiteSpace(_settings.GetNoteClientId)) return;
            var (ok, _, topics) = await new GetNoteDestination(_settings).TestConnectionAsync();
            if (ok && topics.Count > 0)
                await Dispatcher.InvokeAsync(() => FillTopicCombo(topics));
        }
        catch { /* 静默失败 */ }
    }

    // ── 运行日志（通用板块） ──

    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppLog.EnsureLogDir(),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("Settings", "打开日志文件夹失败", ex);
            MessageBox.Show(this, $"打开日志文件夹失败：{ex.Message}\n\n可手动打开：{AppLog.EnsureLogDir()}",
                "FocusCapture 提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LogRetention_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!int.TryParse(LogRetentionInput.Text.Trim(), out var days)) return; // 输入中途的空/非数字不处理，失焦或下次输入生效
        var clamped = Math.Clamp(days, 1, 365);
        if (clamped != days)
        {
            _suppressEvents = true;
            LogRetentionInput.Text = clamped.ToString();
            _suppressEvents = false;
        }
        _settings.LogRetentionDays = clamped;
        _settings.Save();
        AppLog.ApplyRetention(clamped); // 立即生效并清理过期日志
    }

    // ── 供应商联动 / 密钥显隐 / 申请跳转 ──

    /// <summary>按 BaseUrl 反推预设并选中对应项；匹配不上选「自定义」。同步更新跳转按钮可见性。
    /// 注意：保存并恢复 _suppressEvents 原值——此方法会在 LoadSettings（抑制中）与用户改 BaseUrl（未抑制）两种场景调用，
    /// 不可在 finally 强制设 false，否则会提前解除 LoadSettings 的全程抑制，致后续板块回填误触发事件。</summary>
    private void SelectProviderByUrl(string? baseUrl)
    {
        var prev = _suppressEvents;
        _suppressEvents = true;
        try
        {
            var matched = AiProviders.MatchByUrl(baseUrl);
            int idx = -1;
            for (int i = 0; i < AiProviders.Presets.Count; i++)
                if (ReferenceEquals(AiProviders.Presets[i], matched)) { idx = i; break; }
            AiProviderCombo.SelectedIndex = idx >= 0 ? idx : AiProviders.Presets.Count;   // 最后一项=自定义
            UpdateKeyApplyVisibility(matched is not null);
        }
        finally { _suppressEvents = prev; }
    }

    private void UpdateKeyApplyVisibility(bool visible)
        => AiKeyApplyLink.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>选预设→自动填 BaseUrl；选自定义→清空 BaseUrl 交给用户自行输入。</summary>
    private void AiProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var preset = (AiProviderCombo.SelectedItem as ComboBoxItem)?.Tag as AiProviderPreset;
        UpdateKeyApplyVisibility(preset is not null);
        if (preset is not null)
        {
            if (AiBaseUrlInput.Text.Trim() != preset.BaseUrl)
                AiBaseUrlInput.Text = preset.BaseUrl;   // 触发 AiBaseUrl_TextChanged 落盘（反推仍命中本预设，不切自定义）
        }
        else
        {
            // 自定义：清空 BaseUrl，交给用户自行输入（触发 TextChanged 落空值并反推仍为自定义）
            if (!string.IsNullOrEmpty(AiBaseUrlInput.Text))
                AiBaseUrlInput.Text = "";
        }
    }

    private bool _aiKeyVisible;
    private void AiKeyToggle_Click(object sender, RoutedEventArgs e)
    {
        _aiKeyVisible = !_aiKeyVisible;
        if (_aiKeyVisible)
        {
            // 切到明文：把 PasswordBox 当前内容带到明文框（设 Text 会触发 TextChanged，抑制避免重复落盘）
            var prev = _suppressEvents;
            _suppressEvents = true;
            try { AiApiKeyPlain.Text = AiApiKeyInput.Password; }
            finally { _suppressEvents = prev; }
            AiApiKeyInput.Visibility = Visibility.Collapsed;
            AiApiKeyPlain.Visibility = Visibility.Visible;
            AiApiKeyPlain.Focus();
            AiApiKeyPlain.CaretIndex = AiApiKeyPlain.Text.Length;
        }
        else
        {
            // 切回星号：把明文框内容带回 PasswordBox
            var prev = _suppressEvents;
            _suppressEvents = true;
            try { AiApiKeyInput.Password = AiApiKeyPlain.Text; }
            finally { _suppressEvents = prev; }
            AiApiKeyPlain.Visibility = Visibility.Collapsed;
            AiApiKeyInput.Visibility = Visibility.Visible;
        }
    }

    /// <summary>明文态编辑：落盘真实值。星号态编辑走 AiApiKey_PasswordChanged。切换时单向同步由 AiKeyToggle_Click 负责。</summary>
    private void AiApiKeyPlain_TextChanged(object sender, TextChangedEventArgs e)
    { if (_suppressEvents) return; _settings.AiApiKey = AiApiKeyPlain.Text; _settings.Save(); }

    private void AiKeyApplyLink_Click(object sender, RoutedEventArgs e)
    {
        var preset = (AiProviderCombo.SelectedItem as ComboBoxItem)?.Tag as AiProviderPreset;
        var url = preset?.KeyApplyUrl;
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 无默认浏览器或被取消，静默 */ }
    }

    private void AiAssistantName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AiAssistantName = AiAssistantNameInput.Text.Trim();
        _settings.Save();
        _onChanged?.Invoke();
    }

    /// <summary>回答长度上限（token）：256~32768 合法才落盘，否则保留旧值（用户输入中途不落盘）</summary>
    private void AiMaxTokens_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (int.TryParse(AiMaxTokensInput.Text.Trim(), out var v) && v is >= 256 and <= 32768)
        { _settings.AiMaxTokens = v; _settings.Save(); }
    }

    /// <summary>Agent 工具轮数上限：1~50 合法才落盘</summary>
    private void AgentMaxToolRounds_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (int.TryParse(AgentMaxToolRoundsInput.Text.Trim(), out var v) && v is >= 1 and <= 50)
        { _settings.AgentMaxToolRounds = v; _settings.Save(); }
    }

    /// <summary>工具结果截断阈值（字符数）：500~65536 合法才落盘</summary>
    private void AiToolResultLimit_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (int.TryParse(AiToolResultLimitInput.Text.Trim(), out var v) && v is >= 500 and <= 65536)
        { _settings.AiToolResultLimit = v; _settings.Save(); }
    }

    private async void BtnTestAi_Click(object sender, RoutedEventArgs e)
    {
        if (_testingAi) return;
        _testingAi = true;
        try
        {
            // 先落盘当前输入框内容，确保用所见即所得的配置测试
            _settings.AiBaseUrl = AiBaseUrlInput.Text.Trim();
            _settings.AiApiKey = _aiKeyVisible ? AiApiKeyPlain.Text : AiApiKeyInput.Password;
            _settings.AiModel = AiModelInput.Text.Trim();
            _settings.Save();

            if (string.IsNullOrWhiteSpace(_settings.AiModel))
            {
                AiTestResult.Text = "请先填写模型名称";
                AiTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
                return;   // finally 会复位 _testingAi 与按钮
            }

            BtnTestAi.IsEnabled = false;
            AiTestResult.Text = "连接中...";
            AiTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

            var provider = new OpenAICompatibleProvider(
                _settings.AiBaseUrl, _settings.AiApiKey, _settings.AiModel, _settings.AiMaxTokens);
            var ok = await provider.TestConnectionAsync();

            AiTestResult.Text = ok ? "连接成功" : "连接失败";
            AiTestResult.Foreground = new SolidColorBrush(
                ok ? Color.FromRgb(0x4C, 0xAF, 0x50) : Color.FromRgb(0xE5, 0x39, 0x35));
        }
        catch (Exception ex)
        {
            AiTestResult.Text = ex.Message;
            AiTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        }
        finally
        {
            _testingAi = false;
            BtnTestAi.IsEnabled = true;
        }
    }

    private static void SetAutoStart(bool enable)
    {
        try
        {
            using var rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (rk == null) return;
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;
            if (enable) rk.SetValue("FocusCapture", $"\"{exe}\"");
            else rk.DeleteValue("FocusCapture", false);
        }
        catch { }
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        { Description = "选择笔记存储目录", SelectedPath = _settings.NotesPath };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        { _settings.NotesPath = dlg.SelectedPath; NotesPathText.Text = dlg.SelectedPath; _settings.Save(); }
    }

    // ── 外观：自定义托盘图标 ──

    private static string CustomIconPath => FocusCapturePaths.Combine("custom_icon.png");

    private void UpdateIconUI()
    {
        var hasCustom = !string.IsNullOrEmpty(_settings.CustomIconPath) && File.Exists(_settings.CustomIconPath);
        BtnResetIcon.Visibility = hasCustom ? Visibility.Visible : Visibility.Collapsed;
        IconStatusText.Text = hasCustom ? "已使用自定义图标" : "";
    }

    private void BtnChooseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择任务栏/托盘图标（png/jpg，≤1MB）",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var file = new FileInfo(dlg.FileName);
        var ext = file.Extension.ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg"))
        {
            IconStatusText.Text = "仅支持 png/jpg 图片";
            IconStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            return;
        }
        if (file.Length > 1024 * 1024)
        {
            IconStatusText.Text = "图片超过 1MB，请换一张更小的";
            IconStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            return;
        }

        // 复制到 %AppData%\FocusCapture\custom_icon.png 并持久化路径
        try
        {
            var dir = Path.GetDirectoryName(CustomIconPath)!;
            Directory.CreateDirectory(dir);
            File.Copy(dlg.FileName, CustomIconPath, true);
            _settings.CustomIconPath = CustomIconPath;
            _settings.Save();
            IconStatusText.Text = "已保存，托盘图标立即生效";
            IconStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
            UpdateIconUI();
            _onChanged?.Invoke();
        }
        catch (Exception ex)
        {
            IconStatusText.Text = $"保存失败：{ex.Message}";
            IconStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        }
    }

    private void BtnResetIcon_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(CustomIconPath)) File.Delete(CustomIconPath);
            _settings.CustomIconPath = "";
            _settings.Save();
            IconStatusText.Text = "已恢复默认图标";
            IconStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
            UpdateIconUI();
            _onChanged?.Invoke();
        }
        catch (Exception ex)
        {
            IconStatusText.Text = $"恢复失败：{ex.Message}";
            IconStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        }
    }

    private void BtnRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        if (_noteService == null) return;
        var bin = new RecycleBinService(_settings.NotesPath);
        var win = new RecycleBinWindow(_noteService, bin, _syncEngineProvider?.Invoke()) { Owner = this };
        win.ShowDialog();
    }

    // ── 云同步（QUEST-5 第八步：WebDAV 配置 / E2EE 主密码 / 同步控制 / 重置） ──

    /// <summary>保存 WebDAV 配置 + 解锁/迁移 + 立即同步一次（授权码即钥匙：填一次永久有效，方案A 2026-08-15）。</summary>
    private async void BtnSyncConnect_Click(object sender, RoutedEventArgs e)
    {
        var url = SyncUrlInput.Text.Trim();
        var user = SyncUserInput.Text.Trim();
        var token = SyncTokenInput.Password;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(user))
        {
            SyncStatusText.Text = "请填写服务器地址、坚果云账号";
            return;
        }
        if (string.IsNullOrEmpty(token))
        {
            // 授权码留空 = 沿用已保存的（DPAPI 密文解出；应用重启后依然有效）
            var saved = Models.SyncSettings.UnprotectToken(_settings.Sync.WebDavToken);
            if (string.IsNullOrEmpty(saved))
            {
                SyncStatusText.Text = "请填写坚果云授权码（坚果云客户端『设置 → 第三方应用管理』生成的应用密码）";
                return;
            }
            token = saved;
        }

        _settings.Sync.ProviderName = "WebDAV";
        _settings.Sync.WebDavUrl = url;
        _settings.Sync.WebDavUser = user;
        _settings.Sync.WebDavToken = Models.SyncSettings.ProtectToken(token);
        _settings.Save();
        _onSyncConfigChanged?.Invoke();   // MainWindow 用新配置重建引擎

        var engine = _syncEngineProvider?.Invoke();
        if (engine == null)
        {
            SyncStatusText.Text = "同步引擎不可用，请检查配置";
            return;
        }

        SyncStatusText.Text = engine.IsLegacyMasterPasswordMode
            ? "检测到旧版主密码配置，正在一键升级（本地明文重新加密上传）…"
            : "正在连接并同步…";
        try
        {
            SyncResult result;
            if (engine.IsLegacyMasterPasswordMode)
                result = await engine.MigrateFromLegacyAsync();          // 旧版：无需原主密码，自动升级
            else
            {
                await engine.SetTokenKeyAsync(token);
                result = await engine.SyncNowAsync(auto: false);
            }
            SyncStatusText.Text = result.Success
                ? $"连接成功，已同步（{_settings.Sync.LastSyncAt}）。建议勾选『自动同步』"
                : "连接失败：" + result.Error;
        }
        catch (Exception ex)
        {
            SyncStatusText.Text = "连接失败：" + ex.Message;
        }
    }

    private async void BtnSyncNow_Click(object sender, RoutedEventArgs e)
    {
        var engine = _syncEngineProvider?.Invoke();
        if (engine == null || !engine.IsMasterPasswordSet)
        {
            SyncStatusText.Text = "请先『保存并连接』（未解锁或未配置）";
            return;
        }
        SyncStatusText.Text = "正在同步…";
        var result = await engine.SyncNowAsync(auto: false);
        SyncStatusText.Text = result.Success
            ? $"同步完成（{_settings.Sync.LastSyncAt}）"
            : "同步失败：" + result.Error;
    }

    /// <summary>从云端重新拉取（2026-09-09）：清空同步进度 → 只拉取。修「状态成功但本地一条没收到」的自助入口；
    /// 本地数据不动、不上传，已有行幂等跳过，缺的补上。</summary>
    private async void BtnRepull_Click(object sender, RoutedEventArgs e)
    {
        var engine = _syncEngineProvider?.Invoke();
        if (engine == null || !engine.IsMasterPasswordSet)
        {
            SyncStatusText.Text = "请先『保存并连接』（未解锁或未配置）";
            return;
        }
        var choice = System.Windows.MessageBox.Show(
            "将清空同步进度，把云端全部笔记重新对一遍：\n\n· 本地已有的内容不会重复、不会改动\n· 缺失的内容会补回来\n· 本机任何数据都不会被上传或覆盖\n\n继续吗？",
            "从云端重新拉取", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes) return;
        BtnRepull.IsEnabled = false;
        SyncStatusText.Text = "正在从云端重新拉取…";
        try
        {
            var result = await engine.RepullFromCloudAsync();
            SyncStatusText.Text = result.Success
                ? $"重新拉取完成（{_settings.Sync.LastSyncAt}）"
                : "重新拉取失败：" + result.Error;
        }
        catch (Exception ex) { SyncStatusText.Text = "重新拉取失败：" + ex.Message; }
        finally { BtnRepull.IsEnabled = true; }
    }

    private void AutoSync_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.Sync.AutoSyncEnabled = AutoSyncCheck.IsChecked == true;
        _settings.Save();
        var engine = _syncEngineProvider?.Invoke();
        if (_settings.Sync.AutoSyncEnabled) engine?.StartAutoSync();
        else engine?.StopAutoSync();
    }

    /// <summary>上传合并间隔（笔记+会话共用）：失焦保存，非数字/低于 30 按 30 兜底（坚果云红线）；重排合并窗口立即生效。</summary>
    private void MergeWindow_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var ok = int.TryParse(MergeWindowInput.Text.Trim(), out var seconds);
        var value = ok ? Math.Max(30, seconds) : 30;
        if (_settings.Sync.MergeWindowSeconds == value) { MergeWindowInput.Text = value.ToString(); return; }
        _settings.Sync.MergeWindowSeconds = value;
        _settings.Save();
        MergeWindowInput.Text = value.ToString();
        _syncEngineProvider?.Invoke()?.RefreshMergeWindow();
    }

    /// <summary>同步 AI 问答记录开关：关 = ChatSyncEngine 完全不跑（笔记同步不受影响）。</summary>
    private void ChatSync_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.Sync.ChatSyncEnabled = ChatSyncCheck.IsChecked == true;
        _settings.Save();
    }

    private async void BtnResetSync_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "重置同步状态将清空云端全部桶并全量重新上传。\n确认继续？",
            "重置同步状态", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        var engine = _syncEngineProvider?.Invoke();
        if (engine == null)
        {
            SyncStatusText.Text = "同步引擎不可用";
            return;
        }
        SyncStatusText.Text = "正在重置并全量重传…";
        var result = await engine.ResetSyncAsync();
        SyncStatusText.Text = result.Success ? "已重置并全量重传" : "重置失败：" + result.Error;
    }

    // ══════════════════ 文件与网盘（2026-09-16） ══════════════════

    private CancellationTokenSource? _baiduAuthCts;

    /// <summary>加载文件/网盘板块。</summary>
    private void LoadFileSettings()
    {
        var creds = BaiduCredentialStore.LoadCredentials();
        BaiduAppKeyInput.Text = creds?.AppKey ?? "";
        // SecretKey 刻意不回显：留在输入框里会跟着截图/录屏一起泄露。留空 = 沿用已保存的那份。
        BaiduSecretInput.Password = "";
        BaiduNetRootInput.Text = _settings.BaiduNetRoot;

        CacheMaxGbInput.Text = _settings.LocalCacheMaxGb.ToString("0.#");
        CacheIdleDaysInput.Text = _settings.LocalCacheIdleDays.ToString();
        CacheEvictCheck.IsChecked = _settings.LocalCacheEvictEnabled;

        AttachmentUploadCheck.IsChecked = _settings.BaiduAttachmentUploadEnabled;
        AttachmentRetentionInput.Text = _settings.BaiduAttachmentRetentionDays.ToString();

        DataRootText.Text = FocusCapturePaths.Root;
        DataRootStatusText.Text = FocusCapturePaths.IsCustomRootInEffect
            ? "当前使用自定义目录；改回默认目录时把指针文件 data_root.txt 删掉即可。"
            : "当前使用默认目录（%AppData%\\FocusCapture）。";

        RefreshBaiduStatus();
        RefreshCacheStatus();
    }

    private void RefreshBaiduStatus()
    {
        var creds = BaiduCredentialStore.LoadCredentials();
        if (creds?.IsComplete != true)
        {
            BaiduStatusText.Text = "尚未配置应用凭据：请填写 AppKey 与 SecretKey（在百度网盘开放平台创建「个人使用」应用后获得）。";
            return;
        }

        var token = BaiduCredentialStore.LoadToken();
        var head = $"已配置应用（AppKey {BaiduCredentialStore.Mask(creds.AppKey)}）";
        BaiduStatusText.Text = token == null
            ? head + "，尚未授权。点「开始授权」按提示用手机扫码或输入授权码即可。"
            : head + $"；授权令牌{(token.IsValid ? "有效" : "已过期，下次使用时会自动刷新")}。";
    }

    private void RefreshCacheStatus()
    {
        var (count, bytes, protectedCount) = CacheEvictor.Measure();
        var text = count == 0
            ? "本机暂无缓存的网盘文件"
            : $"本机缓存 {count} 个文件，共 {RootMigrationService.FormatSize(bytes)}";
        if (protectedCount > 0)
            text += $"；其中 {protectedCount} 个尚未上传完成，不会被自动清理";

        // 「一直传不上去」必须看得见：否则用户以为存成功了，直到某天想取回才发现云端根本没有
        var stuck = FileRepository.StuckUploadCount(UploadQueue.MaxRetry);
        if (stuck > 0)
            text += $"；⚠ 有 {stuck} 个文件多次上传失败（检查网盘授权与网络后，重新「保存到网盘」即可再试）";

        CacheStatusText.Text = text + "。";
    }

    // ── 应用凭据 ──

    private void BaiduAppKey_LostFocus(object sender, RoutedEventArgs e) => SaveBaiduCredentials();

    private void BaiduSecret_LostFocus(object sender, RoutedEventArgs e) => SaveBaiduCredentials();

    /// <summary>
    /// 保存应用凭据。两个输入框是配合关系（AppKey 明文可回显、SecretKey 一律不回显），
    /// 所以只在「两个都填了」或「AppKey 变了且已存有 SecretKey」时才落盘，避免半截覆盖。
    /// </summary>
    private void SaveBaiduCredentials()
    {
        var appKey = BaiduAppKeyInput.Text.Trim();
        var secret = BaiduSecretInput.Password.Trim();
        var saved = BaiduCredentialStore.LoadCredentials();

        if (appKey.Length == 0 && secret.Length == 0) return;
        if (secret.Length == 0)
        {
            if (saved?.IsComplete == true && appKey.Length > 0 && appKey != saved.AppKey)
            {
                // 只改了 AppKey：保留原 SecretKey（用户没重填就代表沿用）
                if (!BaiduCredentialStore.SaveCredentials(appKey, saved.SecretKey))
                    AppLog.Warn("Settings", "应用凭据保存失败");
                BaiduSecretInput.Password = "";
                RefreshBaiduStatus();
            }
            return;
        }
        if (appKey.Length == 0)
        {
            System.Windows.MessageBox.Show(this, "请同时填写 AppKey。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (appKey == saved?.AppKey && secret == saved.SecretKey) return;

        if (!BaiduCredentialStore.SaveCredentials(appKey, secret))
            System.Windows.MessageBox.Show(this, "凭据保存失败，请检查磁盘权限。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        BaiduSecretInput.Password = "";   // 存完就清空输入框，屏幕上不留明文
        RefreshBaiduStatus();
    }

    private void BaiduNetRoot_LostFocus(object sender, RoutedEventArgs e)
    {
        var v = BaiduNetdiskClient.NormalizeNetRoot(BaiduNetRootInput.Text);
        BaiduNetRootInput.Text = v;
        if (v == _settings.BaiduNetRoot) return;
        _settings.BaiduNetRoot = v;
        _settings.Save();
        FileRepository.Cloud = new BaiduCloudStorage(v);   // 立即生效，不必重启
    }

    // ── 授权 ──

    private async void BtnBaiduAuthorize_Click(object sender, RoutedEventArgs e)
    {
        if (_baiduAuthCts != null)
        {
            _baiduAuthCts.Cancel();
            _baiduAuthCts = null;
            return;   // 再点一次 = 取消正在进行的授权
        }

        SaveBaiduCredentials();
        var creds = BaiduCredentialStore.LoadCredentials();
        if (creds?.IsComplete != true)
        {
            System.Windows.MessageBox.Show(this,
                "请先填写并保存 AppKey 与 SecretKey。\n\n" +
                "获取方式：pan.baidu.com/union → 申请接入 → 实名认证 → 创建应用（用途选「个人使用」，类型选「软件」）。",
                "缺少应用凭据", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cts = new CancellationTokenSource();
        _baiduAuthCts = cts;
        BtnBaiduAuthorize.Content = "取消授权中…";
        try
        {
            BaiduStatusText.Text = "正在获取授权码…";
            var code = await BaiduNetdiskClient.RequestDeviceCodeAsync(creds.AppKey, cts.Token);

            BaiduStatusText.Text =
                $"已获取授权码：{code.UserCode}\n" +
                $"请在浏览器打开 {code.VerificationUrl} 并输入上面的授权码（已尝试自动打开浏览器）。\n" +
                "等待授权中…";

            try { Process.Start(new ProcessStartInfo(code.VerificationUrl) { UseShellExecute = true }); }
            catch { /* 打不开浏览器也不影响：用户可自行复制地址 */ }

            var token = await BaiduNetdiskClient.PollDeviceTokenAsync(creds.AppKey, creds.SecretKey, code,
                msg => Dispatcher.Invoke(() => BaiduStatusText.Text = "等待授权… " + msg), cts.Token);

            // 授权令牌必须立刻落盘：它是本机专属凭据，丢了要重新走一遍流程
            if (!BaiduCredentialStore.SaveToken(token))
                AppLog.Warn("Settings", "授权令牌保存失败");

            BaiduStatusText.Text = "授权成功。文件现在可以存到网盘了。";
            FileRepository.Cloud = new BaiduCloudStorage(_settings.BaiduNetRoot);
            UploadQueue.Kick();   // 把之前因未授权而滞留的待传文件补上
        }
        catch (OperationCanceledException)
        {
            BaiduStatusText.Text = "已取消授权。";
        }
        catch (Exception ex)
        {
            BaiduStatusText.Text = "授权失败：" + ex.Message;
        }
        finally
        {
            BtnBaiduAuthorize.Content = "开始授权";
            _baiduAuthCts = null;
            cts.Dispose();
        }
    }

    private async void BtnBaiduTest_Click(object sender, RoutedEventArgs e)
    {
        BtnBaiduTest.IsEnabled = false;
        BaiduStatusText.Text = "正在连接…";
        try
        {
            var (ok, message) = await BaiduCloudStorage.TestAsync(_settings.BaiduNetRoot, CancellationToken.None);
            BaiduStatusText.Text = (ok ? "✓ " : "✗ ") + message;
        }
        catch (Exception ex)
        {
            BaiduStatusText.Text = "✗ 连接失败：" + ex.Message;
        }
        finally
        {
            BtnBaiduTest.IsEnabled = true;
        }
    }

    private void BtnBaiduRevoke_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this,
                "取消授权？\n\n本机保存的授权令牌会被清除，需要重新授权才能继续存取网盘文件。\n" +
                "（网盘上已有的文件不受影响；应用凭据会保留）",
                "取消授权", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        BaiduCredentialStore.ClearToken();
        RefreshBaiduStatus();
    }

    // ── 本地缓存 ──

    private void CacheMaxGb_LostFocus(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(CacheMaxGbInput.Text.Trim(), out var gb) && gb >= 0.5 && gb <= 1024)
        {
            _settings.LocalCacheMaxGb = gb;
            _settings.Save();
            CacheEvictor.MaxBytes = (long)(gb * 1024 * 1024 * 1024);
        }
        CacheMaxGbInput.Text = _settings.LocalCacheMaxGb.ToString("0.#");
    }

    private void CacheIdleDays_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(CacheIdleDaysInput.Text.Trim(), out var days) && days is >= 1 and <= 3650)
        {
            _settings.LocalCacheIdleDays = days;
            _settings.Save();
            CacheEvictor.IdleDays = days;
        }
        CacheIdleDaysInput.Text = _settings.LocalCacheIdleDays.ToString();
    }

    private void CacheEvict_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.LocalCacheEvictEnabled = CacheEvictCheck.IsChecked == true;
        _settings.Save();
        CacheEvictor.Enabled = _settings.LocalCacheEvictEnabled;
    }

    private void BtnCacheClean_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = CacheEvictor.Run(force: true);
            var tail = report.Removed == 0
                ? "没有符合清理条件的本地副本（未上传完成的文件不会被清理）。"
                : $"已释放 {report.Removed} 个本地副本，回收 {RootMigrationService.FormatSize(report.FreedBytes)}。";
            RefreshCacheStatus();
            CacheStatusText.Text = tail + " " + CacheStatusText.Text;
        }
        catch (Exception ex)
        {
            CacheStatusText.Text = "清理失败：" + ex.Message;
        }
    }

    private void BtnOpenFilesDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(FileRepository.FilesDir);
            Process.Start(new ProcessStartInfo(FileRepository.FilesDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "打开失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── 附件上传 ──

    private void AttachmentUpload_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.BaiduAttachmentUploadEnabled = AttachmentUploadCheck.IsChecked == true;
        _settings.Save();
    }

    private void AttachmentRetention_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(AttachmentRetentionInput.Text.Trim(), out var days) && days is >= 0 and <= 3650)
        {
            _settings.BaiduAttachmentRetentionDays = days;
            _settings.Save();
            FileRepository.AttachmentRetentionDays = days;
        }
        AttachmentRetentionInput.Text = _settings.BaiduAttachmentRetentionDays.ToString();
    }

    private void ChatFiles_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.Sync.FileSyncEnabled = ChatFilesCheck.IsChecked == true;
        _settings.Save();
    }

    // ── 数据目录迁移 ──

    private void BtnOpenDataRoot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(FocusCapturePaths.Root);
            Process.Start(new ProcessStartInfo(FocusCapturePaths.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "打开失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnChangeRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择新的数据目录（建议新建一个空的、好找的文件夹）",
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != true) return;

        var target = dlg.FolderName;
        var source = FocusCapturePaths.Root;

        var validation = RootMigrationService.ValidateTarget(target, source);
        if (!validation.Ok)
        {
            System.Windows.MessageBox.Show(this, validation.Message, "该位置不可用",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var copyData = validation.State != RootTargetState.NonEmpty;

        if (!copyData)
        {
            if (System.Windows.MessageBox.Show(this,
                    "这个目录里已经有内容。\n\n" +
                    "为避免冲掉现有数据，切换后将**直接使用该目录里的现有内容**，不再复制当前数据。\n" +
                    "如果你想搬一份过去，请选一个空的文件夹。\n\n继续切换？",
                    "目录已有内容", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }
        else
        {
            var (files, bytes) = RootMigrationService.Measure(source, applyExcludes: true);
            if (System.Windows.MessageBox.Show(this,
                    $"确定把数据目录改到：\n{target}\n\n" +
                    $"现有数据会完整复制过去（{files} 个文件，{RootMigrationService.FormatSize(bytes)}）。\n" +
                    "原目录不会被修改也不会被删除，随时可以改回来。\n" +
                    "复制完成并校验通过后才会生效，改完需要重启程序。\n\n继续？",
                    "更改数据目录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }

        BtnChangeRoot.IsEnabled = false;
        DataRootStatusText.Text = "正在处理…";
        try
        {
            var result = await Task.Run(() => RootMigrationService.Migrate(
                source, target, copyData,
                msg => Dispatcher.Invoke(() => DataRootStatusText.Text = msg)));

            DataRootStatusText.Text = result.Message + (result.Ok ? "  请重启程序以使用新目录。" : "");
            DataRootText.Text = FocusCapturePaths.Root;

            if (result.Ok)
                System.Windows.MessageBox.Show(this,
                    "数据目录已切换。\n\n请关闭并重新打开 FocusCapture 让新目录生效。\n" +
                    "原目录里的内容仍然保留，确认新位置一切正常后可以自行删除。",
                    "需要重启", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                System.Windows.MessageBox.Show(this, result.Message, "迁移未完成",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            DataRootStatusText.Text = "迁移失败：" + ex.Message;
            System.Windows.MessageBox.Show(this, "迁移失败：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnChangeRoot.IsEnabled = true;
        }
    }

    /// <summary>界面快照专用（诊断工具用，正常流程不调用）：滚到内容底部再出图。</summary>
    internal void ScrollToEndForSnapshot()
    {
        try { ContentScroller.ScrollToEnd(); } catch { /* 快照辅助失败不影响主流程 */ }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
