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
        _suppressEvents = false;
    }

    /// <summary>云同步设置回填（QUEST-5 第八步）</summary>
    private void LoadSyncSettings()
    {
        SyncUrlInput.Text = _settings.Sync.WebDavUrl;
        SyncUserInput.Text = _settings.Sync.WebDavUser;
        SyncRecoveryCodeText.Text = string.IsNullOrEmpty(_settings.Sync.RecoveryCodeHash)
            ? "" : "（已设置，重置时输入）";
        AutoSyncCheck.IsChecked = _settings.Sync.AutoSyncEnabled;
        var engine = _syncEngineProvider?.Invoke();
        var hasPwd = engine?.IsMasterPasswordSet == true;
        SyncStatusText.Text = hasPwd
            ? $"已解锁主密码 · 上次同步：{_settings.Sync.LastSyncAt} {_settings.Sync.LastSyncResult}"
            : string.IsNullOrEmpty(_settings.Sync.E2eeSalt)
                ? "未配置主密码（首次配置将生成恢复码）"
                : $"已配置主密码（请输入主密码解锁）· 上次同步：{_settings.Sync.LastSyncAt} {_settings.Sync.LastSyncResult}";
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
        var win = new RecycleBinWindow(_noteService, bin) { Owner = this };
        win.ShowDialog();
    }

    // ── 云同步（QUEST-5 第八步：WebDAV 配置 / E2EE 主密码 / 同步控制 / 重置） ──

    /// <summary>保存 WebDAV 配置 + 设置/解锁 E2EE 主密码 + 立即同步一次（连接成功提示开启自动同步）。</summary>
    private async void BtnSyncConnect_Click(object sender, RoutedEventArgs e)
    {
        var url = SyncUrlInput.Text.Trim();
        var user = SyncUserInput.Text.Trim();
        var token = SyncTokenInput.Password;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(token))
        {
            SyncStatusText.Text = "请填写服务器地址、坚果云账号、授权码";
            return;
        }

        _settings.Sync.ProviderName = "WebDAV";
        _settings.Sync.WebDavUrl = url;
        _settings.Sync.WebDavUser = user;
        _settings.Sync.WebDavToken = Models.SyncSettings.ProtectToken(token);
        _settings.Save();
        _onSyncConfigChanged?.Invoke();   // MainWindow 用新配置重建引擎

        var isFirst = string.IsNullOrEmpty(_settings.Sync.E2eeSalt);
        var pwd = SyncMasterPwdInput.Password;
        var pwd2 = SyncMasterPwdConfirmInput.Password;
        if (string.IsNullOrEmpty(pwd))
        {
            SyncStatusText.Text = isFirst ? "首次配置请设置 E2EE 主密码（≥8 位含字母+数字）" : "请输入主密码解锁同步";
            return;
        }
        if (pwd != pwd2)
        {
            SyncStatusText.Text = "两次输入的主密码不一致";
            return;
        }
        if (!CryptoService.IsValidMasterPassword(pwd))
        {
            SyncStatusText.Text = "主密码强度不足：≥8 位且含字母+数字";
            return;
        }

        var engine = _syncEngineProvider?.Invoke();
        if (engine == null)
        {
            SyncStatusText.Text = "同步引擎不可用，请检查配置";
            return;
        }

        SyncStatusText.Text = "正在派生密钥并连接…";
        try
        {
            await engine.SetMasterPasswordAsync(pwd);
        }
        catch (Exception ex)
        {
            SyncStatusText.Text = "密钥派生失败：" + ex.Message;
            return;
        }

        if (isFirst)
        {
            // 首次配置：生成恢复码并展示（哈希存本地；主密码/恢复码明文不进云端）
            var code = CryptoService.GenerateRecoveryCode();
            var (hash, salt) = CryptoService.HashRecoveryCode(code);
            _settings.Sync.RecoveryCodeHash = hash;
            _settings.Sync.RecoveryCodeSalt = salt;
            _settings.Save();
            SyncRecoveryCodeText.Text = code;
            MessageBox.Show(
                $"请抄下恢复码（与主密码分开放置）：\n\n{code}\n\n忘记主密码时可用恢复码 + 新主密码重置。",
                "恢复码", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        var result = await engine.SyncNowAsync(auto: false);
        SyncStatusText.Text = result.Success
            ? $"连接成功，已同步（{_settings.Sync.LastSyncAt}）。建议勾选『自动同步』"
            : "连接失败：" + result.Error;
    }

    private async void BtnSyncNow_Click(object sender, RoutedEventArgs e)
    {
        var engine = _syncEngineProvider?.Invoke();
        if (engine == null || !engine.IsMasterPasswordSet)
        {
            SyncStatusText.Text = "请先『保存并连接』并输入主密码";
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

    private async void BtnResetMasterPwd_Click(object sender, RoutedEventArgs e)
    {
        var code = RecoveryCodeInput.Password;
        var pwd = SyncMasterPwdInput.Password;
        var pwd2 = SyncMasterPwdConfirmInput.Password;
        if (!CryptoService.VerifyRecoveryCode(code, _settings.Sync.RecoveryCodeHash, _settings.Sync.RecoveryCodeSalt))
        {
            SyncStatusText.Text = "恢复码错误";
            return;
        }
        if (string.IsNullOrEmpty(pwd) || pwd != pwd2 || !CryptoService.IsValidMasterPassword(pwd))
        {
            SyncStatusText.Text = "新主密码无效（≥8 位含字母+数字，两次一致）";
            return;
        }
        var engine = _syncEngineProvider?.Invoke();
        if (engine == null)
        {
            SyncStatusText.Text = "同步引擎不可用";
            return;
        }
        SyncStatusText.Text = "正在重置主密码并全量重传…";
        var result = await engine.ResetMasterPasswordAsync(pwd);
        if (result.Success)
        {
            // 重置后恢复码同步更新（主密码与恢复码永远不同期存储明文，各自新生成）
            var newCode = CryptoService.GenerateRecoveryCode();
            var (hash, salt) = CryptoService.HashRecoveryCode(newCode);
            _settings.Sync.RecoveryCodeHash = hash;
            _settings.Sync.RecoveryCodeSalt = salt;
            _settings.Save();
            SyncRecoveryCodeText.Text = newCode;
            MessageBox.Show($"主密码已重置，新的恢复码：\n\n{newCode}\n\n请抄下并妥善保管。", "恢复码已更新",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        SyncStatusText.Text = result.Success ? "主密码已重置并全量重传" : "重置失败：" + result.Error;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
