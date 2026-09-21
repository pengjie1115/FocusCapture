using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FocusCapture.Services;
using FocusCapture.Services.Skills;

namespace FocusCapture.Windows;

/// <summary>
/// Skill 依赖授权窗口（2026-09-20）。
///
/// <para>
/// <b>它存在的理由：</b>阶段一交付时，Skill 需要授权却没有任何应用内入口 —— 模型只能照着
/// CLI 输出里的提示自己发挥，最后变成"请你在系统里跑这条命令"，还带着开发机上的安装路径。
/// 授权是宿主的事：这里把标准设备码流三步（申请 → 出二维码 → 领回 token）全做完，
/// 用户只做一件事 —— **扫一次码**。
/// </para>
/// <para>
/// <b>零点击完成：</b>窗口打开后自动申请授权码、自动生成二维码、自动轮询等待确认。
/// 用户扫完并在飞书里点"确认授权"之后窗口自己关闭（不需要回来点"我扫好了"）。
/// </para>
/// <para>
/// <b>线程：</b>本窗口的流程全部跑在 UI 线程上（<c>await</c> 不阻塞消息循环）——
/// 它是被 <c>UiThread.AskAsync</c> 封送进来、再以 <c>ShowDialog</c> 模态打开的。
/// </para>
/// </summary>
public partial class SkillAuthWindow : Window
{
    private readonly SkillDependency _dep;

    /// <summary>
    /// 授权协议（2026-09-21 起从依赖基类搬到实现类）。为 null = 这个依赖不需要登录，
    /// 此时本窗口无事可做 —— 如实报错，**不抛异常**（异常冒泡到全局处理器会弹模态框，把后续点击吃掉）。
    /// </summary>
    private IDepAuthFlow? Flow => _dep.Flow;

    private CancellationTokenSource? _cts;
    private DispatcherTimer? _timer;
    private DateTime _deadline;
    private bool _closed;
    private bool _cancelled;
    private string? _tempDir;

    public SkillAuthWindow(SkillDependency dep, DependencyStatus status)
    {
        InitializeComponent();
        _dep = dep;

        HeadText.Text = $"用飞书扫码授权 —— {dep.DisplayName}";
        DepText.Text = $"当前状态：{status.Detail}\n本次将申请：{dep.AuthScopeText}";

        Loaded += async (_, _) => await StartFlowAsync().ConfigureAwait(true);
        Closed += (_, _) => Cleanup();
    }

    /// <summary>申请授权码 → 出二维码 → 等用户确认。可重入（「重新生成」按钮会再走一遍）。</summary>
    private async Task StartFlowAsync()
    {
        CancelCurrentFlow();          // 重入时先把上一轮收干净（device_code 会作废，不能复用）

        _cancelled = false;
        RetryButton.Visibility = Visibility.Collapsed;
        UrlBox.Text = "";
        QrImage.Visibility = Visibility.Collapsed;
        QrPlaceholder.Visibility = Visibility.Visible;
        QrPlaceholder.Text = "正在生成二维码…";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xCF));
        StatusText.Text = "正在获取授权码…";

        var cts = new CancellationTokenSource();
        _cts = cts;

        var flow = Flow;
        if (flow == null)
        {
            ShowFailure($"{_dep.DisplayName} 不需要登录，没有可执行的授权流程。");
            return;
        }

        var (ok, message, session) = await flow.StartAuthAsync(cts.Token).ConfigureAwait(true);
        if (_cancelled || _closed) return;
        if (!ok || session == null)
        {
            ShowFailure(message.Length > 0 ? message : "无法获取授权码。");
            return;
        }

        UrlBox.Text = session.VerificationUrl;

        var png = await flow.MakeQrPngAsync(session.VerificationUrl, TempDir(), cts.Token).ConfigureAwait(true);
        if (_cancelled || _closed) return;

        if (png != null)
        {
            try
            {
                // OnLoad：把文件读进内存后立刻释放句柄，否则临时目录删不掉
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(png);
                bmp.EndInit();
                QrImage.Source = bmp;
                QrImage.Visibility = Visibility.Visible;
                QrPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Skill", $"二维码图片加载失败：{ex.Message}");
                QrPlaceholder.Text = "二维码图片无法显示\n可直接用下面的链接授权";
            }
        }
        else
        {
            // 降级不是失败：还可以走"复制链接到浏览器"。绝不假装已经出码。
            QrPlaceholder.Text = "二维码生成失败\n请点「在浏览器打开」，或复制下面的链接";
        }

        StartCountdown(session.ExpiresInSeconds);

        var (done, doneMsg) = await flow.CompleteAuthAsync(
            session.DeviceCode, session.ExpiresInSeconds * 1000, cts.Token).ConfigureAwait(true);

        if (_cancelled || _closed) return;
        StopCountdown();

        if (done)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xD1, 0x8F));
            StatusText.Text = "已授权。窗口即将关闭，你可以直接再说一次刚才那句话。";
            AppLog.Info("Skill", $"依赖授权完成：{_dep.Id}");
            await Task.Delay(700).ConfigureAwait(true);
            CloseWith(true);
            return;
        }

        ShowFailure(doneMsg);
    }

    private void ShowFailure(string message)
    {
        StopCountdown();
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x9A, 0x9A));
        StatusText.Text = message.Length > 0 ? message : "授权未完成。";
        RetryButton.Visibility = Visibility.Visible;
        QrPlaceholder.Visibility = QrImage.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void StartCountdown(int seconds)
    {
        StopCountdown();
        _deadline = DateTime.Now.AddSeconds(seconds);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            var left = _deadline - DateTime.Now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            StatusText.Text = $"等待扫码并在飞书中确认… 剩余 {left:mm\\:ss}";
        };
        _timer.Start();
        StatusText.Text = $"等待扫码并在飞书中确认… 剩余 {TimeSpan.FromSeconds(seconds):mm\\:ss}";
    }

    private void StopCountdown()
    {
        _timer?.Stop();
        _timer = null;
    }

    private string TempDir()
    {
        if (_tempDir != null && Directory.Exists(_tempDir)) return _tempDir;
        _tempDir = Path.Combine(Path.GetTempPath(), "fc-skill-auth-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        return _tempDir;
    }

    private void OnRetry(object sender, RoutedEventArgs e) => _ = StartFlowAsync();

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Skill", $"打开授权链接失败：{ex.Message}");
            StatusText.Text = "打不开浏览器 —— 请手动复制上面的链接到浏览器打开。";
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cancelled = true;
        CloseWith(false);
    }

    /// <summary>关窗幂等：自动完成与用户取消可能撞在一起，重复设置 DialogResult 会抛</summary>
    private void CloseWith(bool result)
    {
        if (_closed) return;
        _closed = true;
        CancelCurrentFlow();
        try { DialogResult = result; } catch { /* 不是模态打开时设置会抛，忽略 */ }
        Close();
    }

    private void CancelCurrentFlow()
    {
        StopCountdown();
        try { _cts?.Cancel(); } catch { /* 已释放 */ }
        _cts?.Dispose();
        _cts = null;
    }

    private void Cleanup()
    {
        _closed = true;
        _cancelled = true;
        CancelCurrentFlow();
        if (_tempDir == null) return;
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* 临时目录删不掉不影响功能 */ }
    }
}
