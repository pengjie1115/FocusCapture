using FocusCapture.Services;

namespace FocusCapture.Windows;

public partial class InputWindow : Window
{
    private readonly NoteService _noteService;
    private readonly VoiceService _voiceService;
    private bool _isSaving;
    private bool _isVoiceListening;
    private DispatcherTimer? _idleTimer;

    public event Action? NoteSaved;

    public InputWindow(NoteService noteService, Models.AppSettings settings)
    {
        InitializeComponent();
        _noteService = noteService;
        _voiceService = new VoiceService();
        Opacity = settings.InputOpacity;

        // 识别结果/状态事件来自后台线程，统一调度回 UI 线程
        _voiceService.FinalText += text => Dispatcher.Invoke(() => AppendAtCaret(text));
        _voiceService.PartialText += text => Dispatcher.Invoke(() =>
        {
            if (_isVoiceListening) VoiceHintText.Text = "正在识别…";
        });
        _voiceService.Ready += text => Dispatcher.Invoke(() =>
        {
            VoiceHintText.Text = "聆听中…";
        });
        _voiceService.StatusChanged += text => Dispatcher.Invoke(() =>
        {
            // 下载/加载模型属于慢过程，展示状态；聆听后由 Ready 接管文案
            if (text.Contains("下载") || text.Contains("加载"))
                VoiceHintText.Text = text;
        });
        _voiceService.Error += text => Dispatcher.Invoke(() =>
        {
            // 不调用 Stop()（错误由后台线程自身收尾，避免在错误线程内等待自己）
            _isVoiceListening = false;
            ResetMicButton();
            System.Windows.MessageBox.Show($"语音识别错误: {text}", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    public void SetOpacity(double o) => Opacity = Math.Clamp(o, 0.3, 1.0);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionWindow();
        InputBox.Focus();
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _idleTimer.Tick += (_, _) => { if (!_isSaving) Hide(); };
        _idleTimer.Start();
    }

    private void PositionWindow()
    {
        var s = SystemParameters.WorkArea;
        Left = (s.Width - Width) / 2 + s.Left;
        Top = s.Bottom - Height - 80;
    }

    private void Window_Deactivated(object sender, EventArgs e) { if (!_isSaving) Hide(); }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = string.IsNullOrEmpty(InputBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _idleTimer?.Stop(); _idleTimer?.Start();
        AdjustHeight();
    }

    private void AdjustHeight()
    {
        var lc = InputBox.LineCount;
        Height = lc <= 5 ? Math.Max(80, lc * 22 + 40) : 190;
        var s = SystemParameters.WorkArea;
        Top = s.Bottom - Height - 80;
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Save(); }
        else if (e.Key == Key.Escape) { e.Handled = true; Hide(); }
    }

    private void Save()
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) { Hide(); return; }
        _isSaving = true;
        try { _noteService.SaveNote(text); } finally { _isSaving = false; }
        InputBox.Text = ""; NoteSaved?.Invoke(); Hide();
    }

    public void SaveDirect(string content)
    {
        _isSaving = true;
        try { _noteService.SaveNote(content); } finally { _isSaving = false; }
        NoteSaved?.Invoke();
    }

    public new void Show()
    {
        InputBox.Text = ""; Placeholder.Visibility = Visibility.Visible;
        Height = 80; _isSaving = false;
        base.Show();
        PositionWindow();
        Activate(); // 确保窗口激活，快捷键唤起后可直接打字
        TryEnableIme();
        // 延迟一拍：窗口完全显示后再抢焦点，避免过早 Focus 被窗口激活流程吞掉
        Dispatcher.BeginInvoke(new Action(() =>
        {
            InputBox.Focus();
            Keyboard.Focus(InputBox);
        }), DispatcherPriority.Input);
        _idleTimer?.Start();
    }

    public new void Hide()
    {
        if (_isVoiceListening) StopVoiceListening();
        _idleTimer?.Stop();
        base.Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _idleTimer?.Stop();
        _idleTimer = null;
        if (_isVoiceListening) StopVoiceListening();
        _voiceService.Dispose();
        base.OnClosed(e);
    }

    // ── 语音输入 ──

    private void BtnMic_Click(object sender, RoutedEventArgs e)
    {
        // 与沉浸式输入互斥：沉浸会话进行中禁止占用麦克风
        if (ImmersiveSessionService.IsActive)
        {
            System.Windows.MessageBox.Show("沉浸式输入正在进行语音识别，暂不可用", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_isVoiceListening) StopVoiceListening();
        else StartVoiceListening();
    }

    private void StartVoiceListening()
    {
        _isVoiceListening = true;
        BtnMic.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        BtnMic.Content = "◼";
        BtnMic.ToolTip = "停止语音输入";
        VoiceHintText.Text = "正在启动…";
        _voiceService.Start();
    }

    private void StopVoiceListening()
    {
        _isVoiceListening = false;
        _voiceService.Stop();
        ResetMicButton();
    }

    private void ResetMicButton()
    {
        BtnMic.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        BtnMic.Content = "🎤";
        BtnMic.ToolTip = "语音输入";
        VoiceHintText.Text = "";
    }

    /// <summary>识别结果追加到输入框光标处</summary>
    private void AppendAtCaret(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var idx = Math.Clamp(InputBox.CaretIndex, 0, InputBox.Text.Length);
        var insert = text;
        if (idx > 0 && !char.IsWhiteSpace(InputBox.Text[idx - 1]))
            insert = " " + text;
        InputBox.Text = InputBox.Text.Insert(idx, insert);
        InputBox.CaretIndex = idx + insert.Length;
        InputBox.Focus();
    }

    /// <summary>IME 激活（最佳努力：个别输入法不生效时焦点已就位，仍可直接打字）</summary>
    private void TryEnableIme()
    {
        try { InputMethod.SetIsInputMethodEnabled(InputBox, true); }
        catch { /* 个别输入法不支持，忽略 */ }
    }
}
