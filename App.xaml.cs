namespace FocusCapture;

public partial class App : WpfApp
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局异常兜底：防止静默崩溃，确保用户能看到错误信息
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("UI", args.Exception);
            ShowCrashDialog(args.Exception);
            args.Handled = true;
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogCrash("AppDomain", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        };

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

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FocusCapture");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n");
        }
        catch { /* 写不进日志也别崩 */ }
    }

    private static void ShowCrashDialog(Exception ex)
    {
        try
        {
            MessageBox.Show(
                $"FocusCapture 启动失败：\n\n{ex.Message}\n\n详细日志已写入：\n%LocalAppData%\\FocusCapture\\crash.log",
                "FocusCapture 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { /* 弹窗也失败就放弃 */ }
    }
}
