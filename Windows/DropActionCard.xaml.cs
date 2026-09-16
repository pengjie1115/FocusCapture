using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FocusCapture.Windows;

/// <summary>
/// 文件拖到悬浮球后浮出的**紧凑卡片**（方案 §4.3 / §5.2）。
///
/// 关键前提（方案 §3 第 2 条）：拖拽期间鼠标左键被 OLE 拖放循环独占，目标窗口上任何按钮、菜单都点不动。
/// 所以卡片**只能在松手之后**弹 —— 绝不要在 DragEnter/DragOver 里弹。
///
/// 关掉这张卡片的四条路径：点了某个选项 / 点了卡片外面 / 按 Esc / 兜底超时。
/// 前三条是交互设计，第四条是**防呆**：卡片是 Topmost 且没有标题栏，万一激活被别的东西抢走，
/// 没有兜底就会永久挂在屏幕最上层，那比"多活 60 秒"难收拾得多。
/// </summary>
public partial class DropActionCard : Window
{
    /// <summary>兜底自动关闭时长（秒）。只在"点了外面也关不掉"的异常情况下才会走到。</summary>
    private const int SafetyTimeoutSeconds = 60;

    /// <summary>Show 之后多久才允许「点外面关闭」。防的是激活没抢到时的假 Deactivated，导致卡片一闪就没。</summary>
    private static readonly TimeSpan DismissArmDelay = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherTimer _safety;
    private readonly DispatcherTimer _arm;
    private double _targetOpacity;
    private bool _armed;

    public event Action? AiAskRequested;
    public event Action? SaveToCloudRequested;
    public event Action? GetNoteRequested;

    /// <param name="title">卡片标题：单文件 = 真实文件名（微信图片为自动生成的 <c>微信图片_日期_时间.jpg</c>）；多文件 = 「N 个文件」。</param>
    /// <param name="subtitle">副行：<c>{大小} · 来自{来源程序}</c>。</param>
    /// <param name="showGetNote">是否显示「发到得到大脑」（**仅文本类文件**显示，用户拍板）。</param>
    /// <param name="getNoteAvailable">得到大脑凭证是否已配置；未配置 → 置灰并提示去设置。</param>
    /// <param name="opacity">方案 §9 的 DropActionOpacity。</param>
    public DropActionCard(string title, string subtitle, bool showGetNote,
        bool getNoteAvailable, double opacity)
    {
        InitializeComponent();

        _targetOpacity = Math.Clamp(opacity, 0.3, 1.0);

        TitleText.Text = title;
        SubText.Text = subtitle;

        BtnGetNote.Visibility = showGetNote ? Visibility.Visible : Visibility.Collapsed;
        if (showGetNote && !getNoteAvailable)
        {
            BtnGetNote.IsEnabled = false;
            BtnGetNote.ToolTip = "未配置得到大脑凭证，请到「设置 → 得到大脑」填写";
        }

        _safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SafetyTimeoutSeconds) };
        _safety.Tick += (_, _) => Dismiss();

        _arm = new DispatcherTimer { Interval = DismissArmDelay };
        _arm.Tick += (_, _) => { _arm.Stop(); _armed = true; };
    }

    /// <summary>贴着球浮出，并抢一次激活（激活了才能靠"点外面"关掉）。</summary>
    public void ShowNear(double ballLeft, double ballTop, double ballWidth, double ballHeight)
    {
        // 先隐形：SizeToContent 的窗口要等布局完才知道实际尺寸，定位前若已可见会先闪在错误位置一下
        Opacity = 0;
        Show();
        UpdateLayout();
        OverlayPlacement.PlaceBeside(this, ballLeft, ballTop, ballWidth, ballHeight);
        Opacity = _targetOpacity;

        Activate();
        _arm.Start();
        _safety.Start();
    }

    /// <summary>透明度设置变化时实时跟手。</summary>
    public void SetOpacity(double o)
    {
        _targetOpacity = Math.Clamp(o, 0.3, 1.0);
        if (IsVisible) Opacity = _targetOpacity;
    }

    private void Dismiss()
    {
        _safety.Stop();
        _arm.Stop();
        Close();
    }

    private void BtnAiAsk_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        AiAskRequested?.Invoke();
    }

    private void BtnSaveToCloud_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        SaveToCloudRequested?.Invoke();
    }

    private void BtnGetNote_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        GetNoteRequested?.Invoke();
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_armed) Dismiss();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Dismiss();
    }
}
