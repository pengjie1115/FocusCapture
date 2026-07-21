using FocusCapture.Services;
using Microsoft.Win32;

namespace FocusCapture.Windows;

public partial class SettingsWindow : Window
{
    private Models.AppSettings _settings = null!;
    private readonly HotkeyService? _hotkeyService;
    private readonly Action? _onChanged;
    private bool _capturing;
    private Action<Models.HotkeyBinding>? _onCaptureDone;
    private bool _suppressEvents = true; // 抑制 InitializeComponent 期间的 ValueChanged 事件

    public SettingsWindow(Models.AppSettings s, HotkeyService? hk = null, Action? onChanged = null)
    {
        _settings = s; _hotkeyService = hk; _onChanged = onChanged;
        InitializeComponent();
        _suppressEvents = false; // 初始化完成，允许事件处理
        LoadSettings(); KeyDown += OnKeyDown;
    }

    private void LoadSettings()
    {
        _suppressEvents = true;
        BtnSummonHotkey.Content = Win32.HotkeyToString(_settings.SummonHotkey);
        BtnClipboardHotkey.Content = Win32.HotkeyToString(_settings.ClipboardToggleHotkey);
        BtnQuickViewHotkey.Content = Win32.HotkeyToString(_settings.QuickViewHotkey);
        BtnVoiceInputHotkey.Content = Win32.HotkeyToString(_settings.VoiceInputHotkey);
        InputOpacitySlider.Value = _settings.InputOpacity;
        BallOpacitySlider.Value = _settings.FloatBallOpacity;
        QuickViewOpacitySlider.Value = _settings.QuickViewOpacity;
        InputOpacityLabel.Text = $"{(int)(_settings.InputOpacity * 100)}%";
        BallOpacityLabel.Text = $"{(int)(_settings.FloatBallOpacity * 100)}%";
        QuickViewOpacityLabel.Text = $"{(int)(_settings.QuickViewOpacity * 100)}%";
        NotesPathText.Text = _settings.NotesPath;
        AutoStartCheck.IsChecked = _settings.AutoStart;
        _suppressEvents = false;
    }

    private void StartCapture(Button btn, Action<Models.HotkeyBinding> done)
    {
        if (_capturing) return;
        _capturing = true; _onCaptureDone = done;
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
    }

    private void CancelCapture() { _capturing = false; _onCaptureDone = null; LoadSettings(); }

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

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        _settings.SummonHotkey = new() { Modifiers = 1, Key = 0x20 };
        _settings.ClipboardToggleHotkey = new() { Modifiers = 3, Key = 0x70 };
        _settings.QuickViewHotkey = new() { Modifiers = 3, Key = 0x56 };
        _settings.VoiceInputHotkey = new() { Modifiers = 3, Key = 0x52 };
        _settings.Save(); LoadSettings(); _onChanged?.Invoke();
    }

    private void InputOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_suppressEvents) return; _settings.InputOpacity = e.NewValue; InputOpacityLabel.Text = $"{(int)(e.NewValue * 100)}%"; _settings.Save(); _onChanged?.Invoke(); }
    private void BallOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_suppressEvents) return; _settings.FloatBallOpacity = e.NewValue; BallOpacityLabel.Text = $"{(int)(e.NewValue * 100)}%"; _settings.Save(); _onChanged?.Invoke(); }
    private void QuickViewOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_suppressEvents) return; _settings.QuickViewOpacity = e.NewValue; QuickViewOpacityLabel.Text = $"{(int)(e.NewValue * 100)}%"; _settings.Save(); _onChanged?.Invoke(); }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    { if (_suppressEvents) return; _settings.AutoStart = AutoStartCheck.IsChecked == true; SetAutoStart(_settings.AutoStart); _settings.Save(); }

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

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
