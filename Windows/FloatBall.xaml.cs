using FocusCapture.Services;

namespace FocusCapture.Windows;

public partial class FloatBall : Window
{
    // 拖动状态
    private bool _isDragging;
    private bool _dragMoveUsed; // DragMove 走原生拖拽，MouseLeftButtonUp 时跳过点击逻辑
    private Point _dragStartPos;
    private double _dragStartLeft, _dragStartTop;
    private bool _isCollapsed;

    // 吸附阈值
    private const int SnapThreshold = 12;

    // 颜色笔刷
    private SolidColorBrush _normalBrush = new(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private SolidColorBrush _hoverBrush = new(Color.FromRgb(0x50, 0x50, 0x50));
    private SolidColorBrush _captureBrush = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private SolidColorBrush? _flashBrush;

    public event Action? InputRequested;
    public event Action? QuickViewRequested;
    public event Action? VoiceInputRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public FloatBall()
    {
        InitializeComponent();
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - 80;
        Top = wa.Bottom - 200;
    }

    public void SetOpacity(double o) => Opacity = Math.Clamp(o, 0.3, 1.0);

    public void SetCaptureActive(bool active)
    {
        _normalBrush = active
            ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))
            : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        Ball.Fill = _normalBrush;
    }
    public void ApplyPosition(double l, double t)
    {
        var s = SystemParameters.WorkArea;
        if (l < s.Left || l > s.Right - 20) l = s.Right - 80;
        if (t < s.Top || t > s.Bottom - 20) t = s.Bottom - 200;
        Left = l; Top = t;
    }

    public void FlashGreen()
    {
        if (_flashBrush != null)
        {
            _flashBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _flashBrush.Color = Colors.LimeGreen;
        }
        else
        {
            _flashBrush = new SolidColorBrush(Colors.LimeGreen);
        }

        Ball.Fill = _flashBrush;
        var ca = new ColorAnimation
        {
            From = Colors.LimeGreen,
            To = Color.FromRgb(0x3A, 0x3A, 0x3A),
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        ca.Completed += (_, _) => Ball.Fill = _normalBrush;
        _flashBrush.BeginAnimation(SolidColorBrush.ColorProperty, ca);
    }

    public (double left, double top) GetPosition() => (Left, Top);
    public bool IsCollapsed => _isCollapsed;

    // ── 位置动画 ──

    private void AnimateTo(double targetLeft, double targetTop, int durationMs = 220)
    {
        var animL = new DoubleAnimation
        {
            To = targetLeft,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var animT = new DoubleAnimation
        {
            To = targetTop,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(LeftProperty, animL);
        BeginAnimation(TopProperty, animT);
    }

    private void StopAnimations()
    {
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
    }

    // ── 拖动：用 DragMove() 做原生拖拽（系统级批量处理，流畅不卡） ──

    private void Ball_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        _dragMoveUsed = false;
        _dragStartPos = e.GetPosition(this);
        _dragStartLeft = Left;
        _dragStartTop = Top;

        StopAnimations();

        // 抓取视觉反馈
        Ball.RenderTransform = new ScaleTransform(1.08, 1.08, 20, 20);
        Ball.Fill = _hoverBrush;

        Ball.CaptureMouse();
    }

    private void Ball_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _isDragging) return;

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStartPos.X) > 3 || Math.Abs(p.Y - _dragStartPos.Y) > 3)
        {
            _isDragging = true;
            _dragMoveUsed = true;
            Ball.ReleaseMouseCapture();

            try
            {
                // DragMove 走系统原生拖拽循环，流畅度等同于拖动任何标准窗口
                DragMove();
                OnDragComplete();
            }
            catch
            {
                // 极少数情况 DragMove 会抛异常（如超快速拖动），安全回退
                _dragMoveUsed = false;
                _isDragging = false;
                Ball.RenderTransform = null;
                Ball.Fill = _normalBrush;
            }
        }
    }

    private void OnDragComplete()
    {
        Ball.RenderTransform = null;

        var s = SystemParameters.WorkArea;
        bool l = Left <= s.Left + SnapThreshold;
        bool r = Left + Width >= s.Right - SnapThreshold;
        bool u = Top <= s.Top + SnapThreshold;
        bool d = Top + Height >= s.Bottom - SnapThreshold;

        if (l || r || u || d)
            SnapToEdge(l, r, u, d);
        else
            Ball.Fill = _normalBrush;

        _isDragging = false;
    }

    private void Ball_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Ball.ReleaseMouseCapture();

        // DragMove 已处理拖动和吸附，跳过点击逻辑
        if (_dragMoveUsed)
        {
            _dragMoveUsed = false;
            return;
        }

        if (_isDragging)
        {
            // 手动拖拽回退（不应走到这里，但保留防御）
            Ball.RenderTransform = null;
            Ball.Fill = _normalBrush;
        }
        else
        {
            Ball.RenderTransform = null;
            Ball.Fill = _normalBrush;
            InputRequested?.Invoke();
        }
        _isDragging = false;
    }

    // ── 吸附到边缘 ──

    private void SnapToEdge(bool left, bool right, bool top, bool bottom)
    {
        var s = SystemParameters.WorkArea;
        _isCollapsed = true;

        BallGrid.Visibility = Visibility.Collapsed;
        CollapsedBar.Visibility = Visibility.Visible;

        double targetLeft = Left, targetTop = Top;
        int targetW = 48, targetH = 48;

        if (left)
        {
            targetW = 8; targetH = 36;
            targetLeft = s.Left;
            targetTop = Math.Clamp(Top, s.Top, s.Bottom - targetH);
        }
        else if (right)
        {
            targetW = 8; targetH = 36;
            targetLeft = s.Right - targetW;
            targetTop = Math.Clamp(Top, s.Top, s.Bottom - targetH);
        }
        else if (top)
        {
            targetW = 36; targetH = 8;
            targetTop = s.Top;
            targetLeft = Math.Clamp(Left, s.Left, s.Right - targetW);
        }
        else if (bottom)
        {
            targetW = 36; targetH = 8;
            targetTop = s.Bottom - targetH;
            targetLeft = Math.Clamp(Left, s.Left, s.Right - targetW);
        }

        Width = targetW; Height = targetH;
        AnimateTo(targetLeft, targetTop);
    }

    private void CollapsedBar_MouseEnter(object sender, MouseEventArgs e)
    {
        _isCollapsed = false;
        CollapsedBar.Visibility = Visibility.Collapsed;
        BallGrid.Visibility = Visibility.Visible;
        StopAnimations();
        Width = 48; Height = 48;
        var s = SystemParameters.WorkArea;
        if (Left < s.Left + 5) Left = s.Left + 10;
        if (Left + Width > s.Right - 5) Left = s.Right - Width - 10;
        if (Top < s.Top + 5) Top = s.Top + 10;
        if (Top + Height > s.Bottom - 5) Top = s.Bottom - Height - 10;
    }

    private void Ball_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_isDragging) return;
        Ball.Fill = _hoverBrush;
        Ball.RenderTransform = new ScaleTransform(1.1, 1.1, 20, 20);
    }

    private void Ball_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isDragging) return;
        Ball.Fill = _normalBrush;
        Ball.RenderTransform = Transform.Identity;
    }

    private void Ball_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
            Foreground = Brushes.White,
            FontSize = 13,
        };
        menu.Items.Add(CreateMenuItem("  今日笔记速览", () => QuickViewRequested?.Invoke()));
        menu.Items.Add(CreateMenuItem("  沉浸记录", () => VoiceInputRequested?.Invoke()));
        menu.Items.Add(CreateMenuItem("  设置", () => SettingsRequested?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("  退出", () => ExitRequested?.Invoke()));
        menu.IsOpen = true; e.Handled = true;
    }

    private static MenuItem CreateMenuItem(string h, Action a)
    { var item = new MenuItem { Header = h }; item.Click += (_, _) => a(); return item; }
}
