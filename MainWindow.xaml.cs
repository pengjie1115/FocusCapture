using FocusCapture.Services;
using FocusCapture.Windows;

namespace FocusCapture;

public partial class MainWindow : Window
{
    private readonly Models.AppSettings _settings;
    private HotkeyService? _hotkeyService;
    private NoteService? _noteService;
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
            _hotkeyService = new HotkeyService(_hwnd, _settings);
            _hotkeyService.HotkeyPressed += OnHotkeyPressed;
            _hotkeyService.RegisterAll();

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
        _floatBall.SetOpacity(_settings.FloatBallOpacity);
        _floatBall.ApplyPosition(_settings.BallLeft, _settings.BallTop);
        _floatBall.InputRequested += () => Dispatcher.Invoke(() => _inputWindow?.Show());
        _floatBall.QuickViewRequested += () => Dispatcher.Invoke(ShowQuickView);
        _floatBall.SettingsRequested += () => Dispatcher.Invoke(OpenSettings);
        _floatBall.VoiceInputRequested += () => Dispatcher.Invoke(ShowVoiceInput);
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
            });
            sw.Owner = this; sw.ShowDialog();
        }
        finally { _settingsOpen = false; }
    }

    private void CreateTrayIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(System.Drawing.Color.FromArgb(0x3A, 0x3A, 0x3A));
        var icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        { Icon = icon, Visible = true, Text = "FocusCapture - 专注力捕捉" };

        var cm = new System.Windows.Forms.ContextMenuStrip();
        cm.Items.Add("显示设置", null, (_, _) => OpenSettings());
        cm.Items.Add("今日速览", null, (_, _) => ShowQuickView());
        cm.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        cm.Items.Add("退出", null, (_, _) => ExitApp());
        _notifyIcon.ContextMenuStrip = cm;
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();
    }

    private void ExitApp()
    {
        _clipboardHook?.Dispose();
        _hotkeyService?.Dispose();
        if (_floatBall != null) { var (l, t) = _floatBall.GetPosition(); _settings.BallLeft = l; _settings.BallTop = t; _settings.Save(); }
        _floatBall?.Close(); _inputWindow?.Close(); _quickViewWindow?.Close(); _voiceWindow?.Close(); _notifyIcon?.Dispose();
        WpfApp.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e) { _hotkeyService?.Dispose(); _notifyIcon?.Dispose(); base.OnClosed(e); }
}
