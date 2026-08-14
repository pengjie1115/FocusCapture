namespace FocusCapture;

public partial class App : WpfApp
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局异常兜底：防止静默崩溃，确保用户能看到错误信息。
        // 2026-08-13 审查修正：可恢复的 UI 异常（如绑定错误）不再强制 Shutdown(1)——
        // 记录日志 + 弹窗提示 + Handled 继续运行，避免"报错→点确定→闪退"（回收站窗口教训，见 QUEST-5 §2 WPF 绑定铁律）。
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("UI", args.Exception);
            args.Handled = true;
            try
            {
                MessageBox.Show(
                    $"FocusCapture 遇到一个界面错误：\n\n{args.Exception.Message}\n\n" +
                    "详细日志已写入：\n%LocalAppData%\\FocusCapture\\crash.log\n\n程序将继续运行。",
                    "FocusCapture 提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { /* 弹窗也失败就放弃 */ }
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
}
