using FocusCapture.Services;
using FocusCapture.Services.AI;
using FocusCapture.Services.Sync;
using FocusCapture.Windows;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace FocusCapture;

public partial class MainWindow : Window
{
    private readonly Models.AppSettings _settings;
    private HotkeyService? _hotkeyService;
    private NoteService? _noteService;
    private IChatProvider? _aiProvider;              // v3.5：共享 AI provider（面板编辑时间识别 LLM 兜底 + AI 对话框同源配置），设置变更后重建
    private SyncEngine? _syncEngine;            // QUEST-5：云端同步引擎（可插拔 Provider，配置完整才创建）
    private ChatSyncEngine? _chatSyncEngine;    // 2026-09：AI 会话同步引擎（搭 _syncEngine 周期，共享闸；随 CreateSyncEngine 一并创建/重建）
    private FloatBall? _floatBall;
    private InputWindow? _inputWindow;
    private QuickViewWindow? _quickViewWindow;
    private VoiceInputWindow? _voiceWindow;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private ClipboardHookService? _clipboardHook;
    private ReminderService? _reminderService;                    // v3.5 Phase3：提醒定时器/弹窗调度/角标
    private ReminderPopupWindow? _reminderPopup;                  // v3.5 Phase3：单条/多条到点弹窗
    private DailySummaryWindow? _dailySummary;                    // v3.5 Phase3：每日汇总弹窗
    private TodoSummaryWindow? _todoSummaryWindow;                // v3.8：待办汇总面板（热键可唤出/收起，故需持实例）
    private IntPtr _hwnd; // 保存窗口句柄供剪贴板监听和热键切换使用
    private bool _settingsOpen;

    public MainWindow()
    {
        InitializeComponent();
        _settings = Models.AppSettings.Load();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // 硬规则：系统关机/注销时无法弹窗拦截 → 静默尽力一传（带超时），失败由下次启动对账补传
        SystemEvents.SessionEnding += OnSessionEnding;

        // 关键：创建悬浮球放到最前面，托盘失败不影响核心 UI
        try
        {
            CreateFloatBall();
        }
        catch (Exception ex)
        {
            LogStartupError("CreateFloatBall", ex);
        }

        try
        {
            _noteService = new NoteService(_settings);
            AIDialogHelper.Initialize(_noteService, _settings, this);
            _hotkeyService = new HotkeyService(_hwnd, _settings);
            _hotkeyService.HotkeyPressed += OnHotkeyPressed;
            _hotkeyService.RegisterAll();

            // QUEST-5：云端同步引擎（本机变更 → 合并窗口推送；自动同步开 → 启动自动解锁 + 30min 轮询）
            _noteService.NotesChanged += OnNotesChanged;
            _syncEngine = CreateSyncEngine();
            if (_syncEngine != null && _syncEngine.TryUnlockWithStoredToken())
            {
                if (_settings.Sync.AutoSyncEnabled)
                    _syncEngine.StartAutoSync();                      // 内部含首次同步
                else
                    _ = Task.Run(() => _syncEngine.SyncNowAsync());   // 硬规则：启动无条件后台首拉（先拉后推），不阻塞主窗口
            }

            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

            _inputWindow = new InputWindow(_noteService, _settings);
            _inputWindow.NoteSaved += () => _floatBall?.FlashGreen();
            // v3.5：共享 AI provider（与 AI 对话框同源配置；面板编辑待办时间识别 LLM 兜底用，设置变更后由 OpenSettings 回调重建）
            _aiProvider = new OpenAICompatibleProvider(_settings.AiBaseUrl, _settings.AiApiKey, _settings.AiModel, _settings.AiMaxTokens);
            _quickViewWindow = new QuickViewWindow(_noteService, _settings, () => _syncEngine, _aiProvider);
            _voiceWindow = new VoiceInputWindow(_settings);

            // v3.5（Phase 3）：待办提醒服务装配 ——
            // 到点 → ReminderPopupWindow；每日汇总 → DailySummaryWindow；角标 → _floatBall.SetBadge；
            // 角标点击 → TodoSummaryWindow（v3 起每次点击新建实例，见 BadgeClicked）。
            // 启动后立即 Refresh() 一次（角标初始化，验收 9）；NotesChanged → Refresh 保持角标/下次检查同步。
            _reminderPopup = new ReminderPopupWindow(_noteService, _settings);
            _dailySummary = new DailySummaryWindow(_noteService, _settings);
            _reminderService = new ReminderService(_noteService, _settings,
                showDuePopups: items =>
                {
                    // v3.6：悬浮球处于收起态（靠边隐藏）时提醒触发 → 先自动展开，弹窗锚定展开后的主区域；
                    // 未收起（用户在用）则保持现状逻辑不变
                    if (_floatBall?.IsCollapsed == true) _floatBall.ExpandBall();
                    var (lx, ty) = GetBallAnchor();
                    _reminderPopup.ShowPopups(items, lx, ty);
                },
                showDailySummary: () =>
                {
                    var (lx, ty) = GetBallAnchor();
                    _dailySummary.ShowSummary(_noteService.LoadAllEntries(), lx, ty);
                },
                updateBadge: (count, hasRead) => Dispatcher.Invoke(() => _floatBall?.SetBadge(count, hasRead)));
            if (_floatBall != null)
                _floatBall.BadgeClicked += () => Dispatcher.Invoke(() =>
                {
                    // 角标点击语义 = 唤出（不改）：已打开则刷新数据并前置，未打开则新建。
                    // v3.8：实例生命周期改由 ShowTodoSummary 统一管理（关闭事件置 null），
                    // 根上仍是「已关闭的窗口绝不再 Show」；热键入口复用同一方法，杜绝两份逻辑漂移。
                    ShowTodoSummary();
                });
            _noteService.NotesChanged += () => _reminderService?.Refresh();
            // 2026-09-09：云同步拉取落地 → 刷新灵感速览（若开着）+ 待办角标。独立事件，不走 NotesChanged（防反向推回云端）
            _noteService.CloudDataLanded += () => Dispatcher.Invoke(() =>
            {
                try { _quickViewWindow?.Refresh(); _reminderService?.Refresh(); }
                catch { /* 刷新失败不影响同步 */ }
            });
            _reminderService.Start();

            // 剪贴板自动捕获
            _clipboardHook = new ClipboardHookService(_noteService, () =>
                Dispatcher.Invoke(() => _floatBall?.FlashGreen()));
            if (_settings.ClipboardCaptureEnabled)
            {
                _clipboardHook.Install(_hwnd);
                _floatBall?.SetCaptureActive(true);
            }
        }
        catch (Exception ex)
        {
            LogStartupError("InitServices", ex);
        }

        // 托盘最后创建，失败也不影响悬浮球和热键
        try
        {
            CreateTrayIcon();
        }
        catch (Exception ex)
        {
            LogStartupError("CreateTrayIcon", ex);
        }
    }

    private static void LogStartupError(string stage, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FocusCapture");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "startup-error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {stage}\n{ex}\n\n");
        }
        catch { /* 日志写不进也别崩 */ }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY) { _hotkeyService?.HandleHotkey(wParam.ToInt32()); handled = true; }
        else if (msg == ClipboardHookService.WM_CLIPBOARDUPDATE)
        {
            _clipboardHook?.OnClipboardUpdate();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnHotkeyPressed(int id) => Dispatcher.Invoke(() =>
    {
        switch (id)
        {
            case HotkeyService.ID_SUMMON: _inputWindow?.Show(); break;
            case HotkeyService.ID_CLIPBOARD_TOGGLE: ToggleClipboardCapture(); break;
            case HotkeyService.ID_QUICK_VIEW: ShowQuickView(); break;
            case HotkeyService.ID_VOICE_INPUT: ShowVoiceInput(); break;
            case HotkeyService.ID_TODO_SWITCH: _inputWindow?.ToggleType(); break;   // v3.5：全局切换笔记/待办类型
            case HotkeyService.ID_SETTINGS: OpenSettings(); break;                  // v3.7：唤出设置面板
            case HotkeyService.ID_AI_ASK: AIDialogHelper.Open(ExplainMode.Ask); break;   // v3.8：唤起 AI 问答（复用单例窗口，每次开新会话）
            case HotkeyService.ID_TODO_SUMMARY: ToggleTodoSummary(); break;              // v3.8：待办汇总面板（唤出/收起）
        }
    });

    /// <summary>v3.8：待办汇总面板——显示（已打开则刷新数据并前置）。角标点击与热键唤出共用。
    /// 生命周期：单例 + Closed 置 null，已关闭的窗口绝不再 Show（复用前提是窗口无状态，每次 RefreshAll 全量重载）。</summary>
    private void ShowTodoSummary()
    {
        if (_noteService == null) return;
        try
        {
            if (_todoSummaryWindow == null)
            {
                _todoSummaryWindow = new TodoSummaryWindow(_noteService, _settings);
                _todoSummaryWindow.Closed += (_, _) => _todoSummaryWindow = null;
            }
            _todoSummaryWindow.RefreshAll(_noteService.LoadAllEntries());
            _todoSummaryWindow.Show();
            if (_todoSummaryWindow.WindowState == WindowState.Minimized)
                _todoSummaryWindow.WindowState = WindowState.Normal;
            _todoSummaryWindow.Activate();
        }
        catch (Exception ex)
        {
            _todoSummaryWindow = null;   // 异常实例不复用，下次唤出重建
            LogStartupError("OpenTodoSummary", ex);
        }
    }

    /// <summary>v3.8：热键入口——按一次唤出，再按一次收起（与灵感速览/输入框一致的语义）。</summary>
    private void ToggleTodoSummary()
    {
        if (_todoSummaryWindow?.IsVisible == true) { _todoSummaryWindow.Hide(); return; }
        ShowTodoSummary();
    }

    private void ToggleClipboardCapture()
    {
        _settings.ClipboardCaptureEnabled = !_settings.ClipboardCaptureEnabled;
        _settings.Save();

        if (_settings.ClipboardCaptureEnabled)
        {
            _clipboardHook?.Install(_hwnd);
            _floatBall?.SetCaptureActive(true);
        }
        else
        {
            _clipboardHook?.Uninstall();
            _floatBall?.SetCaptureActive(false);
        }
    }

    private void ShowQuickView()
    {
        if (_quickViewWindow?.IsVisible == true) _quickViewWindow.Hide(); else _quickViewWindow?.Show();
    }

    /// <summary>v3.5（Phase 3）：悬浮球的中心坐标，供提醒/汇总弹窗定位在球上方。</summary>
    private (double left, double top) GetBallAnchor()
    {
        if (_floatBall != null && _floatBall.IsVisible)
            return (_floatBall.Left + _floatBall.Width / 2, _floatBall.Top + _floatBall.Height / 2);
        var wa = SystemParameters.WorkArea;
        return (wa.Right - 80, wa.Bottom - 200); // 兜底：默认球位置
    }

    private void ShowVoiceInput()
    {
        if (!LicenseGate.EnsureAllowed(LicenseGate.FeatureVoiceInput, "语音输入")) return;
        if (_voiceWindow?.IsVisible == true) _voiceWindow.Hide(); else _voiceWindow?.Show();
    }

    private void CreateFloatBall()
    {
        _floatBall = new FloatBall();
        _floatBall.AiAssistantName = string.IsNullOrWhiteSpace(_settings.AiAssistantName) ? "AI 问答" : _settings.AiAssistantName;
        _floatBall.SetOpacity(_settings.FloatBallOpacity);
        _floatBall.ApplyPosition(_settings.BallLeft, _settings.BallTop);
        _floatBall.InputRequested += () => Dispatcher.Invoke(() => _inputWindow?.Show());
        _floatBall.QuickViewRequested += () => Dispatcher.Invoke(ShowQuickView);
        _floatBall.SettingsRequested += () => Dispatcher.Invoke(OpenSettings);
        _floatBall.VoiceInputRequested += () => Dispatcher.Invoke(ShowVoiceInput);
        _floatBall.AiAskRequested += () => Dispatcher.Invoke(() => AIDialogHelper.Open(ExplainMode.Ask));
        _floatBall.ExitRequested += () => Dispatcher.Invoke(ExitApp);
        _floatBall.Show();
    }

    private void OpenSettings()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        try
        {
            var sw = new SettingsWindow(_settings, _hotkeyService, () =>
            {
                _hotkeyService?.RegisterAll();
                // v3.5：AI 配置可能变更 → 重建共享 provider 并同步给面板（编辑待办时间识别 LLM 兜底用当前配置）
                _aiProvider = new OpenAICompatibleProvider(_settings.AiBaseUrl, _settings.AiApiKey, _settings.AiModel, _settings.AiMaxTokens);
                _quickViewWindow?.UpdateAiProvider(_aiProvider);
                _inputWindow?.SetOpacity(_settings.InputOpacity);
                _floatBall?.SetOpacity(_settings.FloatBallOpacity);
                if (_quickViewWindow != null) _quickViewWindow.Opacity = _settings.QuickViewOpacity;
                _quickViewWindow?.ApplySettings();   // v3.9：宽度/置顶/标题栏按钮即时生效
                ApplyAssistantNameToAllEntries();
            }, _noteService, () => _syncEngine, RebuildSyncEngine, () => _chatSyncEngine);
            sw.Owner = this; sw.ShowDialog();
        }
        finally { _settingsOpen = false; }
    }

    // ── QUEST-5：同步引擎生命周期 ──

    /// <summary>本机笔记变更 → 合并窗口推送（订阅一次，_syncEngine 字段实时指向当前引擎）。</summary>
    private void OnNotesChanged() => _syncEngine?.NotifyLocalChange();

    /// <summary>会话本地保存（ChatSessionService.Save 静态事件）→ 会话同步防抖窗口。
    /// 静态事件订阅以 -=/+= 防引擎重建后重复触发。</summary>
    private void OnChatSessionChanged() => _chatSyncEngine?.NotifyLocalChange();

    /// <summary>按当前配置创建引擎；配置不完整（无 Provider/无授权码）返回 null（本地功能不受影响）。
    /// ChatSyncEngine 随之一并创建：共享笔记引擎并发闸、订阅 CycleCompleted 搭车 hook，互不影响笔记同步。</summary>
    private SyncEngine? CreateSyncEngine()
    {
        if (_noteService == null) return null;
        var sync = _settings.Sync;
        if (sync.ProviderName != "WebDAV") return null;
        var token = Models.SyncSettings.UnprotectToken(sync.WebDavToken);
        if (string.IsNullOrEmpty(sync.WebDavUser) || string.IsNullOrEmpty(token)) return null;
        var provider = new WebDAVProvider(sync.WebDavUrl, sync.WebDavUser, token);
        var engine = new SyncEngine(_settings, _noteService, provider);
        _chatSyncEngine = new ChatSyncEngine(_settings, provider, engine.Gate);
        engine.CycleCompleted += () => _chatSyncEngine.RunOnceAsync().ContinueWith(_ =>
            Dispatcher.BeginInvoke(new Action(() => AIDialogHelper.RefreshOpenDrawer())));  // 会话同步完成后刷新展开中的抽屉（启动首拉/轮询/flush 全覆盖）
        AIDialogHelper.SessionDeleted = id => _chatSyncEngine?.MarkDeleted(id);  // AI 对话删除 UI → MarkDeleted 闭环①（lambda 读字段，引擎重建后自动指向新实例）
        _chatSyncEngine.ConflictResolutionRequested += OnChatConflictResolution;  // 阶段二：Rev 冲突弹窗裁决（true=本地覆盖上传）
        ChatSessionService.SessionChanged -= OnChatSessionChanged;
        ChatSessionService.SessionChanged += OnChatSessionChanged;
        return engine;
    }

    /// <summary>会话保存冲突裁决（ChatSyncEngine 后台线程回调）：弹窗问用户——
    /// 「是」= 覆盖：用本机这版覆盖云端；「否」= 保留他端：本机这版暂不上传，留痕待下次保存再提示。</summary>
    private Task<bool> OnChatConflictResolution(string sessionId)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var answer = System.Windows.MessageBox.Show(this,
                    $"会话在另一设备有更新（{sessionId[..Math.Min(8, sessionId.Length)]}…）。\n\n" +
                    "【是】覆盖：用本机这版覆盖云端（他端更新会被覆盖）\n" +
                    "【否】保留他端：本机这版暂不上传（下次保存会再次提示）",
                    "会话同步冲突", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                tcs.TrySetResult(answer == MessageBoxResult.Yes);
            }
            catch { tcs.TrySetResult(false); }  // 弹窗失败按"保留他端"处理，绝不静默覆盖
        }));
        return tcs.Task;
    }

    /// <summary>设置页保存 WebDAV 配置后重建引擎（新配置立即生效，自动同步轮询延续）。</summary>
    private void RebuildSyncEngine()
    {
        _syncEngine?.StopAutoSync();
        _syncEngine = CreateSyncEngine();
        if (_syncEngine != null && _syncEngine.TryUnlockWithStoredToken())
        {
            if (_settings.Sync.AutoSyncEnabled)
                _syncEngine.StartAutoSync();
            else
                _ = Task.Run(() => _syncEngine.SyncNowAsync());
        }
        _syncEngine?.RefreshMergeWindow();   // 合并间隔若被调整，重排等待中的合并窗口立即生效
    }

    /// <summary>AI 助手名称同步到三处入口：面板标题栏按钮 / 悬浮球右键菜单 / 托盘菜单（含图标重建）</summary>
    private void ApplyAssistantNameToAllEntries()
    {
        var name = string.IsNullOrWhiteSpace(_settings.AiAssistantName) ? "AI 问答" : _settings.AiAssistantName;
        if (_floatBall != null) _floatBall.AiAssistantName = name;
        _quickViewWindow?.UpdateAiName(name);
        try { CreateTrayIcon(); } catch { /* 托盘重建失败不阻塞设置窗口 */ }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// 托盘图标：优先加载自定义图标（%AppData%\FocusCapture\custom_icon.png），
    /// 否则回退到默认深灰方块（v0.1 兜底逻辑保留）。
    /// </summary>
    private void CreateTrayIcon()
    {
        // 重建时释放旧实例（设置变更后即时刷新托盘）
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        System.Drawing.Icon icon;
        var hIcon = IntPtr.Zero;
        try
        {
            var customPath = _settings.CustomIconPath;
            if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
            {
                // 用户自定义图标：png/jpg 转 HICON（System.Drawing 加载后 GetHicon）
                using var img = System.Drawing.Image.FromFile(customPath);
                hIcon = new System.Drawing.Bitmap(img).GetHicon();
                icon = System.Drawing.Icon.FromHandle(hIcon);
            }
            else
            {
                // 兜底：默认深灰方块
                using var bmp = new System.Drawing.Bitmap(32, 32);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.Clear(System.Drawing.Color.FromArgb(0x3A, 0x3A, 0x3A));
                hIcon = bmp.GetHicon();
                icon = System.Drawing.Icon.FromHandle(hIcon);
            }
        }
        catch
        {
            // 自定义图标损坏等异常 → 回退默认深灰方块
            using var bmp = new System.Drawing.Bitmap(32, 32);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.Clear(System.Drawing.Color.FromArgb(0x3A, 0x3A, 0x3A));
            hIcon = bmp.GetHicon();
            icon = System.Drawing.Icon.FromHandle(hIcon);
        }

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        { Icon = icon, Visible = true, Text = "FocusCapture - 专注力捕捉" };

        var cm = new System.Windows.Forms.ContextMenuStrip();
        cm.Items.Add("显示设置", null, (_, _) => OpenSettings());
        cm.Items.Add("灵感速览", null, (_, _) => ShowQuickView());
        var aiName = string.IsNullOrWhiteSpace(_settings.AiAssistantName) ? "AI 问答" : _settings.AiAssistantName;
        cm.Items.Add(aiName, null, (_, _) => AIDialogHelper.Open(ExplainMode.Ask));
        cm.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        cm.Items.Add("退出", null, (_, _) => ExitApp());
        _notifyIcon.ContextMenuStrip = cm;
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();

        // 释放原始 HICON 句柄，防止 GDI 泄漏
        if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
    }

    /// <summary>系统关机/注销：无法弹窗拦截，静默尽力一传（带超时，会话同步搭车收尾），失败由下次启动对账补传。</summary>
    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        try
        {
            if (_syncEngine == null) return;
            var work = Task.Run(() => _syncEngine.SyncNowAsync(auto: true, includeChat: false));   // 关机只抢救笔记（快）；会话下次启动补传
            work.Wait(TimeSpan.FromSeconds(4));   // 阻塞宽限：关机路径弹窗无意义，只争取 4s
        }
        catch { /* 尽力而为 */ }
    }

    /// <summary>
    /// 退出：彻底退出路径（托盘/悬浮球退出）先做同步 flush（硬规则，带 4s 超时）：
    /// SyncNowAsync 笔记部分 + 会话同步搭车 hook 一起收尾；失败弹窗【重试/仍要退出】，
    /// 选"仍要退出"不丢数据（本地文件是事实源，下次启动对账自动补传）。
    /// </summary>
    private async void ExitApp()
    {
        try { await FlushBeforeExitAsync(); }
        catch { /* flush 流程自身异常不阻塞退出 */ }

        _reminderService?.Stop();   // v3.5（Phase 3）：退出前停掉提醒定时器与弹窗调度
        _clipboardHook?.Dispose();
        _hotkeyService?.Dispose();
        if (_floatBall != null) { var (l, t) = _floatBall.GetPosition(); _settings.BallLeft = l; _settings.BallTop = t; _settings.Save(); }
        AIDialogHelper.CloseAll();
        _floatBall?.Close(); _inputWindow?.Close(); _quickViewWindow?.Close(); _voiceWindow?.Close(); _notifyIcon?.Dispose();
        _todoSummaryWindow?.Close();   // v3.8：待办汇总改为实例常驻，退出时一并关闭
        WpfApp.Current.Shutdown();
    }

    /// <summary>退出前 flush：带超时跑一轮【仅笔记】同步（2026-09-09：includeChat=false——会话对账要拉云端文件，
    /// 4 秒预算等不起，必然超时弹窗；会话未推送内容本地是事实源，下次启动自动补传）。失败循环弹【重试/仍要退出】。关机路径（SessionEnding）不走此方法。</summary>
    private async Task FlushBeforeExitAsync()
    {
        if (_syncEngine == null || !_syncEngine.IsMasterPasswordSet) return;   // 未配置同步：无可 flush
        while (true)
        {
            var work = Task.Run(() => _syncEngine!.SyncNowAsync(auto: false, includeChat: false));
            var completed = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(4))).ConfigureAwait(false);
            if (completed == work && (await work.ConfigureAwait(false)).Success) return;   // 收尾成功

            var reason = completed != work ? "同步超时（4 秒）" : (await work.ConfigureAwait(false)).Error ?? "未知原因";
            var choice = System.Windows.MessageBox.Show(
                $"有记录未上传成功（原因：{reason}）。\n\n要重试上传吗？\n\n选「否」将直接退出；未上传的内容留在本机，下次启动同步时自动补传。",
                "退出前同步失败", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes) return;   // 仍要退出：对账补传兜底，无需额外脏标记
        }
    }

    protected override void OnClosed(EventArgs e) { _hotkeyService?.Dispose(); _notifyIcon?.Dispose(); base.OnClosed(e); }
}
