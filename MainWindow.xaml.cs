using FocusCapture.Services;
using FocusCapture.Services.AI;
using FocusCapture.Services.Sync;
using FocusCapture.Windows;
using System.Runtime.InteropServices;

namespace FocusCapture;

public partial class MainWindow : Window
{
    private readonly Models.AppSettings _settings;
    private HotkeyService? _hotkeyService;
    private NoteService? _noteService;
    private SyncEngine? _syncEngine;            // QUEST-5：云端同步引擎（可插拔 Provider，配置完整才创建）
    private FloatBall? _floatBall;
    private InputWindow? _inputWindow;
    private QuickViewWindow? _quickViewWindow;
    private VoiceInputWindow? _voiceWindow;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private ClipboardHookService? _clipboardHook;
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

            // QUEST-5：云端同步引擎（本机变更 → 30s 合并窗口；自动同步开 → 启动 30min 轮询）
            _noteService.NotesChanged += OnNotesChanged;
            _syncEngine = CreateSyncEngine();
            if (_syncEngine != null && _settings.Sync.AutoSyncEnabled)
                _syncEngine.StartAutoSync();

            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

            _inputWindow = new InputWindow(_noteService, _settings);
            _inputWindow.NoteSaved += () => _floatBall?.FlashGreen();
            _quickViewWindow = new QuickViewWindow(_noteService, _settings);
            _voiceWindow = new VoiceInputWindow(_settings);

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
        }
    });

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

    private void ShowVoiceInput()
    {
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
                _inputWindow?.SetOpacity(_settings.InputOpacity);
                _floatBall?.SetOpacity(_settings.FloatBallOpacity);
                if (_quickViewWindow != null) _quickViewWindow.Opacity = _settings.QuickViewOpacity;
                ApplyAssistantNameToAllEntries();
            }, _noteService, () => _syncEngine, RebuildSyncEngine);
            sw.Owner = this; sw.ShowDialog();
        }
        finally { _settingsOpen = false; }
    }

    // ── QUEST-5：同步引擎生命周期 ──

    /// <summary>本机笔记变更 → 30s 合并窗口推送（订阅一次，_syncEngine 字段实时指向当前引擎）。</summary>
    private void OnNotesChanged() => _syncEngine?.NotifyLocalChange();

    /// <summary>按当前配置创建引擎；配置不完整（无 Provider/无授权码）返回 null（本地功能不受影响）。</summary>
    private SyncEngine? CreateSyncEngine()
    {
        if (_noteService == null) return null;
        var sync = _settings.Sync;
        if (sync.ProviderName != "WebDAV") return null;
        var token = Models.SyncSettings.UnprotectToken(sync.WebDavToken);
        if (string.IsNullOrEmpty(sync.WebDavUser) || string.IsNullOrEmpty(token)) return null;
        var provider = new WebDAVProvider(sync.WebDavUrl, sync.WebDavUser, token);
        return new SyncEngine(_settings, _noteService, provider);
    }

    /// <summary>设置页保存 WebDAV 配置后重建引擎（新配置立即生效，自动同步轮询延续）。</summary>
    private void RebuildSyncEngine()
    {
        _syncEngine?.StopAutoSync();
        _syncEngine = CreateSyncEngine();
        if (_syncEngine != null && _settings.Sync.AutoSyncEnabled)
            _syncEngine.StartAutoSync();
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

    private void ExitApp()
    {
        _clipboardHook?.Dispose();
        _hotkeyService?.Dispose();
        if (_floatBall != null) { var (l, t) = _floatBall.GetPosition(); _settings.BallLeft = l; _settings.BallTop = t; _settings.Save(); }
        AIDialogHelper.CloseAll();
        _floatBall?.Close(); _inputWindow?.Close(); _quickViewWindow?.Close(); _voiceWindow?.Close(); _notifyIcon?.Dispose();
        WpfApp.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e) { _hotkeyService?.Dispose(); _notifyIcon?.Dispose(); base.OnClosed(e); }
}
