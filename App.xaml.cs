namespace FocusCapture;

public partial class App : WpfApp
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 必须先初始化 OLE/STA，否则在 WPF 线程上构造 NotifyIcon
        // 在某些 Windows 版本（特别是 Win11 24H2+）会静默失败，
        // 导致 OnSourceInitialized 中断、悬浮球和托盘都不出现。
        try
        {
            System.Windows.Forms.Application.OleRequired();
        }
        catch { /* best effort */ }

        base.OnStartup(e);
        new MainWindow().Show();
    }
}
