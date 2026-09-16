using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FocusCapture.Windows;

/// <summary>
/// 文字拖到悬浮球后浮出的**竖向小条**（方案 §4.2 / §5.1）。
///
/// 生命周期：拖入 → 存笔记 + 球闪绿 → 弹本窗口 → 停留 DropActionStripSeconds 秒（默认 3）→ 自动消失。
/// **不点也会自己走**：笔记已经稳稳存在本地了，小条只是"要不要顺手做点别的"的出口。
///
/// 为什么是独立窗口：悬浮球窗口的透明区**穿透**（探针实测，落点圆心距恒在 19.68~20.35，
/// 窗口矩形内圆外那片区域收不到任何事件）。所以任何浮层都不能靠在球的透明区里画。
/// </summary>
public partial class DropActionStrip : Window
{
    private readonly DispatcherTimer _dismiss;
    private double _targetOpacity;

    /// <summary>点了「AI 问答」。</summary>
    public event Action? AiAskRequested;

    /// <summary>点了「得到大脑」。**这是绕过 AI 直接调适配器**（用户已拍板），别在这条链路上再套一层 AI。</summary>
    public event Action? GetNoteRequested;

    /// <param name="staySeconds">停留秒数（方案 §9 的 DropActionStripSeconds，2~10）。</param>
    /// <param name="getNoteAvailable">得到大脑凭证是否已配置；未配置 → 该项置灰并提示去设置。</param>
    /// <param name="opacity">方案 §9 的 DropActionOpacity。</param>
    public DropActionStrip(int staySeconds, bool getNoteAvailable, double opacity)
    {
        InitializeComponent();

        _targetOpacity = Math.Clamp(opacity, 0.3, 1.0);

        BtnGetNote.IsEnabled = getNoteAvailable;
        if (!getNoteAvailable)
            BtnGetNote.ToolTip = "未配置得到大脑凭证，请到「设置 → 得到大脑」填写";

        _dismiss = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(staySeconds, 2, 10)),
        };
        _dismiss.Tick += (_, _) => Dismiss();
    }

    /// <summary>贴着球浮出。球在屏幕左半 → 弹在球右侧；右半 → 弹在球左侧。</summary>
    public void ShowNear(double ballLeft, double ballTop, double ballWidth, double ballHeight)
    {
        // 先隐形：SizeToContent 的窗口要等布局完才知道实际尺寸，定位前若已可见会先闪在错误位置一下
        Opacity = 0;
        Show();
        UpdateLayout();
        OverlayPlacement.PlaceBeside(this, ballLeft, ballTop, ballWidth, ballHeight);
        Opacity = _targetOpacity;
        _dismiss.Start();
    }

    /// <summary>透明度设置变化时实时跟手（否则「拖完再拖滑块」看不到变化，会被当成功能坏了）。</summary>
    public void SetOpacity(double o)
    {
        _targetOpacity = Math.Clamp(o, 0.3, 1.0);
        if (IsVisible) Opacity = _targetOpacity;
    }

    private void Dismiss()
    {
        _dismiss.Stop();
        Close();
    }

    private void BtnAiAsk_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        AiAskRequested?.Invoke();
    }

    private void BtnGetNote_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        GetNoteRequested?.Invoke();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Dismiss();
    }
}
