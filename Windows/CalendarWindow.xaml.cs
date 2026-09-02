using System.Globalization;
using System.Windows.Input;
using FocusCapture.Services;

namespace FocusCapture.Windows;

/// <summary>
/// 日历热力图弹窗：单月视图 + 4 档热力色（数据来自 MD 文件真实统计），
/// 点击日期返回选中日期（DateSelected 事件）；点击弹窗外部自动收起。
/// 区间输入：TextBox + 📅Button（弹 CalendarWindow 复用），避免 WPF DatePicker 内部
/// DatePickerTextBox 是 internal 类无法调样式的坑。
/// </summary>
public partial class CalendarWindow : Window
{
    private readonly NoteService _noteService;
    private readonly ThemeColors _theme;
    private DateTime _displayMonth;   // 当月 1 号
    private DateTime? _selectedDate;
    private bool _closed;             // 窗口已关闭标志（防止 Deactivated 重入 Close）

    /// <summary>选中日期事件（点击日期格即触发，兼容旧接口：start=end=选中日）</summary>
    public event Action<DateTime>? DateSelected;

    /// <summary>选中区间事件（2026-08-15 新增，供 QuickViewWindow 切换 Range 模式；
    /// 当前 CalendarWindow 单日选择 + 区间输入控件统一触发 start=end / start..end 区间事件，调用方按 start==end 判定单日 vs 区间）</summary>
    public event Action<DateTime, DateTime>? DateRangeSelected;

    /// <summary>本次打开的选中日期（ShowDialog 返回后读取；现已改非模态 Show，此属性仅供外部读取）</summary>
    public DateTime? SelectedDate => _selectedDate;

    public CalendarWindow(NoteService noteService, DateTime currentDate)
    {
        InitializeComponent();
        _noteService = noteService;
        _theme = new ThemeService().GetColors(); // 热力色接主题（默认 Dark，切换主题后取当前）
        _selectedDate = currentDate.Date;
        _displayMonth = new DateTime(currentDate.Year, currentDate.Month, 1);

        // 初始化区间输入（默认=选中日，即单日模式）
        StartInput.Text = currentDate.Date.ToString("yyyy-MM-dd");
        EndInput.Text = currentDate.Date.ToString("yyyy-MM-dd");

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
        var todoDates = _noteService.LoadTodoDueDates(_displayMonth.Year, _displayMonth.Month);
        var daysInMonth = DateTime.DaysInMonth(_displayMonth.Year, _displayMonth.Month);

        // 周日起始对齐
        var first = new DateTime(_displayMonth.Year, _displayMonth.Month, 1);
        for (var i = 0; i < (int)first.DayOfWeek; i++)
            DayGrid.Children.Add(CreateEmptyCell());

        for (var day = 1; day <= daysInMonth; day++)
        {
            var date = new DateTime(_displayMonth.Year, _displayMonth.Month, day);
            // v3.7：未来且有未办待办的日期加绿色角标（一眼看出哪天有事要做）
            var hasTodo = date > DateTime.Today && todoDates.Contains(date);
            DayGrid.Children.Add(CreateDayCell(date, counts.GetValueOrDefault(date), hasTodo));
        }

        // 补足整行，保持网格整齐
        while (DayGrid.Children.Count % 7 != 0)
            DayGrid.Children.Add(CreateEmptyCell());
    }

    private static Border CreateEmptyCell()
        => new() { Height = 34, Margin = new Thickness(1) };

    private Button CreateDayCell(DateTime date, int count, bool hasTodo = false)
    {
        // 4 档热力色（从 ThemeService 取，不硬编码；主题切换后取当前主题色）
        var (bg, fg) = count switch
        {
            0 => (FromHex(_theme.Heat0Bg), FromHex(_theme.Heat0Fg)),
            <= 2 => (FromHex(_theme.Heat1Bg), FromHex(_theme.Heat1Fg)),
            <= 5 => (FromHex(_theme.Heat2Bg), FromHex(_theme.Heat2Fg)),
            _ => (FromHex(_theme.Heat3Bg), FromHex(_theme.Heat3Fg)),
        };

        // v3.7：未来有待办 → 右上角绿色圆点角标（Grid 承载数字+圆点，不干扰热力色与描边）
        object content = date.Day;
        if (hasTodo)
        {
            var grid = new System.Windows.Controls.Grid();
            var num = new System.Windows.Controls.TextBlock
            {
                Text = date.Day.ToString(),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 3, 0)
            };
            grid.Children.Add(num);
            grid.Children.Add(dot);
            content = grid;
        }

        var btn = new Button
        {
            Content = content,
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
        DateRangeSelected?.Invoke(date, date);   // 2026-08-15 新增：单日选=区间=同一天
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

    // ── 2026-08-15：区间输入处理（TextBox + 📅Button） ──

    /// <summary>解析 TextBox 内容为 DateTime；空 / 格式错则返回 null。</summary>
    private static DateTime? ParseDateInput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.Date;
        return null;
    }

    private void EmitRangeAndClose()
    {
        var start = ParseDateInput(StartInput.Text) ?? _selectedDate ?? DateTime.Today;
        var end = ParseDateInput(EndInput.Text) ?? _selectedDate ?? DateTime.Today;
        if (start > end) (start, end) = (end, start);   // 防御性：自动交换
        DateRangeSelected?.Invoke(start, end);
        // 注意：SelectDate / BtnClearRange 路径会在内部 Close；这里用户改完 TextBox 失焦只触发区间事件，由 Deactivated 自动 Close 兜底
    }

    private void StartInput_LostFocus(object sender, RoutedEventArgs e)
        => EmitRangeAndClose();

    private void EndInput_LostFocus(object sender, RoutedEventArgs e)
        => EmitRangeAndClose();

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            EmitRangeAndClose();
        }
    }

    private void BtnStartPick_Click(object sender, RoutedEventArgs e)
    {
        var initial = ParseDateInput(StartInput.Text) ?? _selectedDate ?? DateTime.Today;
        // 2026-08-15 修复：迷你日历 Popup（复用热力渲染），替代弹出整个 CalendarWindow 的笨重方案。
        // 单选即关：点一个日期格 → 回填 StartInput → emit 区间 → Popup 自动关闭。
        var picker = new MiniCalendarPicker(_noteService);
        picker.DatePicked += date =>
        {
            StartInput.Text = date.ToString("yyyy-MM-dd");
            EmitRangeAndClose();
        };
        picker.Show(BtnStartPick, initial);
    }

    private void BtnEndPick_Click(object sender, RoutedEventArgs e)
    {
        var initial = ParseDateInput(EndInput.Text) ?? _selectedDate ?? DateTime.Today;
        var picker = new MiniCalendarPicker(_noteService);
        picker.DatePicked += date =>
        {
            EndInput.Text = date.ToString("yyyy-MM-dd");
            EmitRangeAndClose();
        };
        picker.Show(BtnEndPick, initial);
    }

    private void BtnClearRange_Click(object sender, RoutedEventArgs e)
    {
        // 重置为今天 + 触发单日 DateRangeSelected
        var today = DateTime.Today;
        StartInput.Text = today.ToString("yyyy-MM-dd");
        EndInput.Text = today.ToString("yyyy-MM-dd");
        _selectedDate = today;
        _displayMonth = new DateTime(today.Year, today.Month, 1);
        Render();
        DateRangeSelected?.Invoke(today, today);
        Close();
    }

    private static SolidColorBrush FromHex(string hex)
        => (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
}