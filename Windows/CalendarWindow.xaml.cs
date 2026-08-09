using FocusCapture.Services;

namespace FocusCapture.Windows;

/// <summary>
/// 日历热力图弹窗：单月视图 + 4 档热力色（数据来自 MD 文件真实统计），
/// 点击日期返回选中日期（DateSelected 事件）；点击弹窗外部自动收起。
/// </summary>
public partial class CalendarWindow : Window
{
    private readonly NoteService _noteService;
    private readonly ThemeColors _theme;
    private DateTime _displayMonth;   // 当月 1 号
    private DateTime? _selectedDate;
    private bool _closed;             // 窗口已关闭标志（防止 Deactivated 重入 Close）

    /// <summary>选中日期事件（点击日期即触发）</summary>
    public event Action<DateTime>? DateSelected;

    /// <summary>本次打开的选中日期（ShowDialog 返回后读取；现已改非模态 Show，此属性仅供外部读取）</summary>
    public DateTime? SelectedDate => _selectedDate;

    public CalendarWindow(NoteService noteService, DateTime currentDate)
    {
        InitializeComponent();
        _noteService = noteService;
        _theme = new ThemeService().GetColors(); // 热力色接主题（默认 Dark，切换主题后取当前）
        _selectedDate = currentDate.Date;
        _displayMonth = new DateTime(currentDate.Year, currentDate.Month, 1);

        // 点击日历弹窗以外的任何区域 → 自动收起（模态窗口失去激活时触发）
        // 防重入：窗口关闭过程中 Deactivated 可能再次触发，_closed 标志拦截
        Closed += (_, _) => _closed = true;
        Deactivated += (_, _) => { if (IsVisible && !_closed) Close(); };

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
        Close(); // 非模态打开：选中后立即关闭（QuickViewWindow 回调负责刷新列表）
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
        // 点击"今天"= 跳转到今天：翻到当月并选中今天（触发 DateSelected + 关闭弹窗）
        _displayMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        SelectDate(DateTime.Today);
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
