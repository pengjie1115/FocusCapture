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
        InputBox.Text = ""; Placeholder.Visibility = Visibility.Visible;
        Height = 80; _isSaving = false;
        base.Show(); PositionWindow(); InputBox.Focus(); _idleTimer?.Start();
    }

    public new void Hide() { _idleTimer?.Stop(); base.Hide(); }
}
