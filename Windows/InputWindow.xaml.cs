using FocusCapture.Models;
using FocusCapture.Services;

namespace FocusCapture.Windows;

public partial class InputWindow : Window
{
    private readonly NoteService _noteService;
    private readonly Models.AppSettings _settings;
    private bool _isSaving;
    private DispatcherTimer? _idleTimer;

    /// <summary>v3.5：当前类型（Note/Todo）。Ctrl+T 走全局热键（MainWindow → ToggleType），不在输入框按键内处理防双触发。</summary>
    private string _currentType = "Note";

    public event Action? NoteSaved;

    public InputWindow(NoteService noteService, Models.AppSettings settings)
    {
        InitializeComponent();
        _noteService = noteService;
        _settings = settings;
        Opacity = settings.InputOpacity;
    }

    /// <summary>v3.5：切换笔记/待办类型（全局热键调用）。窗口可见时立即刷新高亮，不可见时仅切字段（下次打开生效）。</summary>
    public void ToggleType()
    {
        _currentType = _currentType == "Note" ? "Todo" : "Note";
        UpdateTypeButtons();
        if (IsVisible) InputBox.Focus();
    }

    /// <summary>刷新类型按钮高亮：选中 = 边框 #4CAF50 + 文字亮，未选中 = 灰色</summary>
    private void UpdateTypeButtons()
    {
        var isTodo = _currentType == "Todo";
        SetTypeButtonStyle(BtnNoteType, !isTodo);
        SetTypeButtonStyle(BtnTodoType, isTodo);
    }

    private static void SetTypeButtonStyle(Button btn, bool selected)
    {
        btn.BorderBrush = new SolidColorBrush(selected
            ? Color.FromRgb(0x4C, 0xAF, 0x50)
            : Color.FromRgb(0x3A, 0x3A, 0x3A));
        btn.Foreground = new SolidColorBrush(selected
            ? Color.FromRgb(0xE0, 0xE0, 0xE0)
            : Color.FromRgb(0x88, 0x88, 0x88));
    }

    private void TypeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string t && t != _currentType)
        {
            _currentType = t;
            UpdateTypeButtons();
        }
        InputBox.Focus();   // 焦点还回输入框：否则回车触发的是按钮 Click 而非保存
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

    /// <summary>
    /// v2：待办保存前做时间识别——命中且未来 → 直接保存；纯日期（30号/下周三）→ 弹窗问几点；
    /// 裸时钟已过（下午输"8点"）→ 弹三选一确认；规则未命中 → 不弹（创建路径暂不接 LLM，本地规则已覆盖常用表达）。
    /// 识别结果通过 dueTime 显式传给 SaveNote，避免二次解析。
    /// </summary>
    private async void Save()
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) { Hide(); return; }
        _isSaving = true;
        try
        {
            if (_currentType == "Todo")
            {
                var due = await TodoEditService.ResolveDueAsync(this, text, null);
                _noteService.SaveNote(text, type: NoteType.Todo, dueTime: due);
            }
            else
            {
                _noteService.SaveNote(text);
            }
        }
        finally { _isSaving = false; }
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
        else
        {
            // v3.5：草稿为空 → 类型重置为设置默认值，并刷新按钮高亮
            _currentType = _settings.InputDefaultType == "Todo" ? "Todo" : "Note";
            UpdateTypeButtons();
            Height = 80;
        }
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
