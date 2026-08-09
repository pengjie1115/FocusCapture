using FocusCapture.Services;

namespace FocusCapture.Windows;

/// <summary>
/// 日历热力图弹窗：单月视图 + 4 档热力色（数据来自 MD 文件真实统计），
/// 点击日期返回选中日期（DateSelected 事件 + DialogResult）。
/// </summary>
public partial class CalendarWindow : Window
{
    private readonly NoteService _noteService;
    private readonly ThemeColors _theme;
    private DateTime _displayMonth;   // 当月 1 号
    private DateTime? _selectedDate;

    /// <summary>选中日期事件（点击日期即触发）</summary>
    public event Action<DateTime>? DateSelected;

    /// <summary>本次打开的选中日期（ShowDialog 返回后读取）</summary>
    public DateTime? SelectedDate => _selectedDate;

    public CalendarWindow(NoteService noteService, DateTime currentDate)
    {
        InitializeComponent();
        _noteService = noteService;
        _theme = new ThemeService().GetColors(); // 热力色接主题（默认 Dark，切换主题后取当前）
        _selectedDate = currentDate.Date;
        _displayMonth = new DateTime(currentDate.Year, currentDate.Month, 1);
        Render();
    }

    private void Render()
    {
        MonthTitle.Text = _displayMonth.ToString("yyyy年M月");
        DayGrid.Children.Clear();

        var counts = _noteService.LoadNoteCounts(_displayMonth.Year, _displayMonth.Month);
        var daysInMonth = DateTime.DaysInMonth(_displayMonth.Year, _displayMonth.Month);

        // 周日起始对齐
        var first = new DateTime(_displayMonth.Year, _displayMonth.Month, 1);
        for (var i = 0; i < (int)first.DayOfWeek; i++)
            DayGrid.Children.Add(CreateEmptyCell());

        for (var day = 1; day <= daysInMonth; day++)
        {
            var date = new DateTime(_displayMonth.Year, _displayMonth.Month, day);
            DayGrid.Children.Add(CreateDayCell(date, counts.GetValueOrDefault(date)));
        }

        // 补足整行，保持网格整齐
        while (DayGrid.Children.Count % 7 != 0)
            DayGrid.Children.Add(CreateEmptyCell());
    }

    private static Border CreateEmptyCell()
        => new() { Height = 34, Margin = new Thickness(1) };

    private Button CreateDayCell(DateTime date, int count)
    {
        // 4 档热力色（从 ThemeService 取，不硬编码；主题切换后取当前主题色）
        var (bg, fg) = count switch
        {
            0 => (FromHex(_theme.Heat0Bg), FromHex(_theme.Heat0Fg)),
            <= 2 => (FromHex(_theme.Heat1Bg), FromHex(_theme.Heat1Fg)),
            <= 5 => (FromHex(_theme.Heat2Bg), FromHex(_theme.Heat2Fg)),
            _ => (FromHex(_theme.Heat3Bg), FromHex(_theme.Heat3Fg)),
        };

        var btn = new Button
        {
            Content = date.Day,
            Height = 34,
            Margin = new Thickness(1),
            FontSize = 11,
            Background = bg,
            Foreground = fg,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };

        // 当前选中日期高亮；今天加浅色描边
        if (date == _selectedDate)
        {
            btn.BorderThickness = new Thickness(2);
            btn.BorderBrush = FromHex(_theme.Accent);
        }
        else if (date == DateTime.Today)
        {
            btn.BorderThickness = new Thickness(1);
            btn.BorderBrush = FromHex(_theme.BorderColor);
        }

        btn.Click += (_, _) => SelectDate(date);
        return btn;
    }

    private void SelectDate(DateTime date)
    {
        _selectedDate = date;
        DateSelected?.Invoke(date);
        DialogResult = true; // ShowDialog 下自动关闭
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(-1);
        Render();
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(1);
        Render();
    }

    private void BtnToday_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        _selectedDate = DateTime.Today;
        Render();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private static SolidColorBrush FromHex(string hex)
        => (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
}
