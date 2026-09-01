using System.Diagnostics;
using FocusCapture.Services;
using FocusCapture.Services.AI;
using FocusCapture.Services.Sync;
using Microsoft.Win32;

namespace FocusCapture.Windows;

public partial class SettingsWindow : Window
{
    private Models.AppSettings _settings = null!;
    private readonly HotkeyService? _hotkeyService;
    private readonly Action? _onChanged;
    private readonly NoteService? _noteService;
    private readonly Func<SyncEngine?>? _syncEngineProvider;   // 实时取 MainWindow 当前引擎（配置保存后由 MainWindow 重建）
    private readonly Action? _onSyncConfigChanged;              // 保存 WebDAV 配置后通知 MainWindow 重建引擎
    private bool _capturing;
    private Action<Models.HotkeyBinding>? _onCaptureDone;
    private bool _suppressEvents = true; // 抑制 InitializeComponent 期间的 ValueChanged 事件
    private bool _testingAi;

    public SettingsWindow(Models.AppSettings s, HotkeyService? hk = null, Action? onChanged = null,
        NoteService? noteService = null, Func<SyncEngine?>? syncEngineProvider = null, Action? onSyncConfigChanged = null)
    {
        _settings = s; _hotkeyService = hk; _onChanged = onChanged; _noteService = noteService;
        _syncEngineProvider = syncEngineProvider; _onSyncConfigChanged = onSyncConfigChanged;
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
        AiBaseUrlInput.Text = _settings.AiBaseUrl;
        AiApiKeyInput.Text = _settings.AiApiKey;
        AiModelInput.Text = _settings.AiModel;
        AiAssistantNameInput.Text = _settings.AiAssistantName;
        AiTestResult.Text = "";
        AiTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        UpdateIconUI();
        LoadSyncSettings();
        LoadTodoSettings();
        _suppressEvents = false;
    }

    /// <summary>v3.5 待办与提醒设置回填</summary>
    private void LoadTodoSettings()
    {
        DefaultTypeCombo.SelectedIndex = _settings.InputDefaultType == "Todo" ? 1 : 0;
        BtnTodoSwitchHotkey.Content = Win32.HotkeyToString(_settings.TodoSwitchHotkey);
        DailySummaryCheck.IsChecked = _settings.DailySummaryEnabled;
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
        var engine = _syncEngineProvider?.Invoke();
        var unlocked = engine?.IsMasterPasswordSet == true;
        SyncStatusText.Text = unlocked
            ? $"已解锁自动同步 · 上次同步：{_settings.Sync.LastSyncAt} {_settings.Sync.LastSyncResult}"
            : engine?.IsLegacyMasterPasswordMode == true
                ? "检测到旧版主密码配置，点击『保存并连接』一键升级（无需原主密码）"
                : string.IsNullOrEmpty(_settings.Sync.E2eeSalt)
                    ? "未配置云同步（首次点击『保存并连接』即完成配置）"
                    : "授权码已保存，应用启动后自动解锁（重新填写授权码可更换）";
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

    /// <summary>纯日期是否弹窗问几点（取消=默认当天 09:00）</summary>
    private void AskTime_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AskTimeForDateOnly = AskTimeCheck.IsChecked == true;
        _settings.Save();
    }

    /// <summary>汇总时间校验：HH:mm 且 00:00~23:59；非法即时回退原值</summary>
    private void DailySummaryTime_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = DailySummaryTimeInput.Text.Trim();
        if (IsValidDailyTime(v)) { _settings.DailySummaryTime = v; _settings.Save(); }
        else if (!string.IsNullOrEmpty(v))
        {
            DailySummaryTimeInput.Text = _settings.DailySummaryTime;
            DailySummaryTimeInput.CaretIndex = DailySummaryTimeInput.Text.Length;
        }
    }

    private static bool IsValidDailyTime(string v)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d{1,2}:\d{2}$")) return false;
        var parts = v.Split(':');
        return int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)
            && h >= 0 && h <= 23 && m >= 0 && m <= 59;
    }

    /// <summary>正整数校验：非法即时回退原值</summary>
    private void SnoozeMinutes_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = SnoozeMinutesInput.Text.Trim();
        if (int.TryParse(v, out var n) && n > 0) { _settings.SnoozeMinutes = n; _settings.Save(); }
        else if (!string.IsNullOrEmpty(v))
        {
            SnoozeMinutesInput.Text = _settings.SnoozeMinutes.ToString();
            SnoozeMinutesInput.CaretIndex = SnoozeMinutesInput.Text.Length;
        }
    }

    private void PopupCloseSeconds_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = PopupCloseSecondsInput.Text.Trim();
        if (int.TryParse(v, out var n) && n > 0) { _settings.PopupAutoCloseSeconds = n; _settings.Save(); }
        else if (!string.IsNullOrEmpty(v))
        {
            PopupCloseSecondsInput.Text = _settings.PopupAutoCloseSeconds.ToString();
            PopupCloseSecondsInput.CaretIndex = PopupCloseSecondsInput.Text.Length;
        }
    }

    private void InputOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_suppressEvents) return; _settings.InputOpacity = e.NewValue; InputOpacityLabel.Text = $"{(int)(e.NewValue * 100)}%"; _settings.Save(); _onChanged?.Invoke(); }
    private void BallOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_suppressEvents) return; _settings.FloatBallOpacity = e.NewValue; BallOpacityLabel.Text = $"{(int)(e.NewValue * 100)}%"; _settings.Save(); _onChanged?.Invoke(); }
    private void QuickViewOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_suppressEvents) return; _settings.QuickViewOpacity = e.NewValue; QuickViewOpacityLabel.Text = $"{(int)(e.NewValue * 100)}%"; _settings.Save(); _onChanged?.Invoke(); }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    { if (_suppressEvents) return; _settings.AutoStart = AutoStartCheck.IsChecked == true; SetAutoStart(_settings.AutoStart); _settings.Save(); }

    private void AiBaseUrl_TextChanged(object sender, TextChangedEventArgs e)
    { if (_suppressEvents) return; _settings.AiBaseUrl = AiBaseUrlInput.Text.Trim(); _settings.Save(); }

    private void AiApiKey_TextChanged(object sender, TextChangedEventArgs e)
    { if (_suppressEvents) return; _settings.AiApiKey = AiApiKeyInput.Text.Trim(); _settings.Save(); }

    private void AiModel_TextChanged(object sender, TextChangedEventArgs e)
    { if (_suppressEvents) return; _settings.AiModel = AiModelInput.Text.Trim(); _settings.Save(); }

    private void AiAssistantName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.AiAssistantName = AiAssistantNameInput.Text.Trim();
        _settings.Save();
        _onChanged?.Invoke();
    }

    private async void BtnTestAi_Click(object sender, RoutedEventArgs e)
    {
        if (_testingAi) return;
        _testingAi = true;
        try
        {
            // 先落盘当前输入框内容，确保用所见即所得的配置测试
            _settings.AiBaseUrl = AiBaseUrlInput.Text.Trim();
            _settings.AiApiKey = AiApiKeyInput.Text.Trim();
            _settings.AiModel = AiModelInput.Text.Trim();
            _settings.Save();

            BtnTestAi.IsEnabled = false;
            AiTestResult.Text = "连接中...";
            AiTestResult.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

            var provider = new OpenAICompatibleProvider(
                _settings.AiBaseUrl, _settings.AiApiKey, _settings.AiModel);
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

    private static string CustomIconPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FocusCapture", "custom_icon.png");

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

    /// <summary>一键跳转坚果云网页端『安全-第三方应用管理』页，省去用户自己找入口。</summary>
    private void BtnOpenAuthPage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://www.jianguoyun.com/d/home#/safe",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开浏览器失败，请手动访问：\nhttps://www.jianguoyun.com/d/home#/safe\n\n" + ex.Message,
                "获取授权码", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>保存 WebDAV 配置 + 解锁/迁移 + 立即同步一次（授权码即钥匙：填一次永久有效，方案A 2026-08-15）。</summary>
    private async void BtnSyncConnect_Click(object sender, RoutedEventArgs e)
    {
        var url = SyncUrlInput.Text.Trim();
        var user = SyncUserInput.Text.Trim();
        var token = SyncTokenInput.Password.Trim();
        if (string.IsNullOrEmpty(user))
        {
            SyncStatusText.Text = "请先填写第①项：坚果云账号（注册邮箱）";
            SyncUserInput.Focus();
            return;
        }
        if (string.IsNullOrEmpty(url)) url = _settings.Sync.WebDavUrl;   // 高级选项留空 = 沿用默认坚果云地址
        if (string.IsNullOrEmpty(token))
        {
            // 授权码留空 = 沿用已保存的（DPAPI 密文解出；应用重启后依然有效）
            var saved = Models.SyncSettings.UnprotectToken(_settings.Sync.WebDavToken);
            if (string.IsNullOrEmpty(saved))
            {
                SyncStatusText.Text = "还差最后一步：点上方『获取授权码』生成并粘贴到第②项";
                SyncTokenInput.Focus();
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
                ? $"✓ 连接成功，已同步（{_settings.Sync.LastSyncAt}），之后改动会自动同步"
                : "连接失败：" + result.Error;
            if (result.Success && _settings.Sync.AutoSyncEnabled != true)
            {
                // 连接成功即默认开启自动同步：减少一步操作和一次理解成本
                _settings.Sync.AutoSyncEnabled = true;
                _settings.Save();
                _suppressEvents = true;
                AutoSyncCheck.IsChecked = true;
                _suppressEvents = false;
                engine.StartAutoSync();
            }
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

    private void AutoSync_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.Sync.AutoSyncEnabled = AutoSyncCheck.IsChecked == true;
        _settings.Save();
        var engine = _syncEngineProvider?.Invoke();
        if (_settings.Sync.AutoSyncEnabled) engine?.StartAutoSync();
        else engine?.StopAutoSync();
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

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
