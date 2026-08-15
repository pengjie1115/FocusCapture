using FocusCapture.Services;

namespace FocusCapture.Windows;

public partial class InputWindow : Window
{
    private readonly NoteService _noteService;
    private bool _isSaving;
    private DispatcherTimer? _idleTimer;

    public event Action? NoteSaved;

    public InputWindow(NoteService noteService, Models.AppSettings settings)
    {
        InitializeComponent();
        _noteService = noteService;
        Opacity = settings.InputOpacity;
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
        // 2026-08-15 修复：保留未保存草稿。自动隐藏/失焦/Esc 只收起窗口不清空内容，
        // 再次唤起（悬浮球/快捷键）可继续编辑；保存成功（Save 内已清空）后下次唤起才是空白。
        var hasDraft = !string.IsNullOrEmpty(InputBox.Text);
        Placeholder.Visibility = hasDraft ? Visibility.Collapsed : Visibility.Visible;
        if (hasDraft) { AdjustHeight(); InputBox.CaretIndex = InputBox.Text.Length; }
        else Height = 80;
        _isSaving = false;
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
        _idleTimer?.Stop();
        base.Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _idleTimer?.Stop();
        _idleTimer = null;
        base.OnClosed(e);
    }

    /// <summary>IME 激活（最佳努力：个别输入法不生效时焦点已就位，仍可直接打字）</summary>
    private void TryEnableIme()
    {
        try { InputMethod.SetIsInputMethodEnabled(InputBox, true); }
        catch { /* 个别输入法不支持，忽略 */ }
    }
}
