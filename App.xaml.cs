using FocusCapture.Diagnostics;
using FocusCapture.Services;
using FocusCapture.Services.AI;

namespace FocusCapture;

public partial class App : WpfApp
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 数据根定位（2026-09-16）：必须排在所有落盘动作之前 —— AppLog 自己也住在数据根下。
        // 读的是默认根里的指针文件；自定义根不可用时只出提示，绝不偷换回默认根（防数据分裂成两套）。
        var rootWarning = FocusCapturePaths.LoadCustomRoot();

        AppLog.Info("App", $"FocusCapture 启动（{typeof(App).Assembly.GetName().Version}）");

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

        // 界面快照模式（--snapshot）：把窗口渲染成 PNG 后退出，不创建主窗口。
        // 仅供开发期 UI 自查使用；正常启动（不带该参数）不进入此分支，行为与改造前完全一致。
        if (UiSnapshot.IsRequested(e.Args))
        {
            UiSnapshot.Run(e.Args);
            return;
        }

        // 拖放探针模式（--dragprobe）：只拉起探针球 + 诊断面板，不创建主窗口
        // （避免悬浮球/托盘/剪贴板监听干扰拖放验证）。仅供开发期验证「拖到悬浮球」交互是否成立。
        if (DragProbe.IsRequested(e.Args))
        {
            DragProbe.Run(e.Args);
            return;
        }

        new MainWindow().Show();

        // 自定义数据根不可用：主窗口起来后再提示（启动期弹窗会挡住托盘/悬浮球的初始化）
        if (rootWarning != null)
        {
            AppLog.Warn("App", "自定义数据目录不可用：" + FocusCapturePaths.Root);
            Dispatcher.BeginInvoke(new Action(() =>
                MessageBox.Show(rootWarning, "数据目录不可用", MessageBoxButton.OK, MessageBoxImage.Warning)));
        }

        // 附件孤儿清理（2026-09-14）：删掉没有任何会话引用的附件文件（会话被裁剪/删除/云端覆盖后留下的）。
        // 放后台线程 + 失败静默 —— 纯清理动作，绝不能拖慢或影响启动。
        _ = Task.Run(() =>
        {
            try
            {
                var removed = ChatAttachmentService.CleanupOrphans();
                if (removed > 0) AppLog.Info("App", $"附件孤儿清理：删除 {removed} 个无引用文件");
            }
            catch { /* best effort */ }
        });
    }

    private static void LogCrash(string source, Exception ex)
    {
        AppLog.Error("Crash", $"{source} 未处理异常", ex);
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
