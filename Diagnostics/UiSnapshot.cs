using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Windows;

namespace FocusCapture.Diagnostics;

/// <summary>
/// 界面快照工具（2026-09-13 引入）：把窗口渲染成 PNG，供开发期自查 UI 问题
/// （控件溢出 / 文字对比度 / 图标对齐 / 字形残缺 / 裁切）。
///
/// 用法：<c>FocusCapture.exe --snapshot [--out &lt;目录&gt;]</c>
/// 输出：默认 <c>%TEMP%\fc-ui-snapshot\</c>，文件名形如 <c>01-灵感速览面板.png</c>，
///       同时写一份 <c>snapshot.log</c> 记录每个窗口的实际像素尺寸（尺寸异常 = 布局异常的第一信号）。
///
/// 设计约束（改动本文件前务必先读）：
/// - <b>数据隔离</b>：强制把 <see cref="FocusCapturePaths.RootOverride"/> 指向临时沙箱，
///   绝不读写用户真实的 <c>%AppData%\FocusCapture</c>（沙箱用完即删）。
/// - <b>零侵入</b>：仅当显式传入 <c>--snapshot</c> 时执行；正常启动不进入该分支，行为与改造前一致。
/// - <b>必须 Show() 才能渲染</b>：窗口统一摆到屏幕外（Left/Top = -32000），不打扰用户。
/// - <b>不透明渲染</b>：面板本身 Opacity=0.8，半透明位图不利于像素比对，快照统一置为 1.0。
/// - <b>DPI 感知</b>：按窗口实际 DPI 缩放出图，保证与屏幕上看到的像素密度一致（125% 缩放也准确）。
/// </summary>
internal static class UiSnapshot
{
    /// <summary>触发快照的命令行开关。</summary>
    public const string Flag = "--snapshot";

    private const string OutFlag = "--out";

    /// <summary>命令行是否请求了快照模式。</summary>
    public static bool IsRequested(string[] args) =>
        args.Any(a => a.Equals(Flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>执行快照：构造各窗口 → 渲染 PNG → 落盘日志 → 退出应用。</summary>
    public static void Run(string[] args)
    {
        var outDir = ResolveOutDir(args);
        Directory.CreateDirectory(outDir);

        // 隔离沙箱：所有落盘路径改道，绝不触碰真实数据
        var sandbox = Path.Combine(Path.GetTempPath(), "fc-ui-snapshot",
            "sandbox-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);
        FocusCapturePaths.RootOverride = sandbox;

        var log = new StringBuilder();
        log.AppendLine($"输出目录: {outDir}");
        log.AppendLine($"沙箱目录: {sandbox}");
        log.AppendLine();

        try
        {
            var settings = AppSettings.Load();
            settings.NotesPath = Path.Combine(sandbox, "notes");
            Directory.CreateDirectory(settings.NotesPath);
            var notes = new NoteService(settings, Path.Combine(sandbox, "deleted.json"));

            Capture("01-灵感速览面板", () => new QuickViewWindow(notes, settings), outDir, log);

            Capture("02-设置-灵感速览板块", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 4;   // 4 = 「灵感速览」板块（0 热键 / 1 AI 模型 / 2 外观 / 3 显示 / 4 灵感速览）
                return w;
            }, outDir, log);

            Capture("03-设置-热键板块", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 0;
                return w;
            }, outDir, log);

            // 其余可无副作用构造的窗口：用于横向排查同一类 UI 写法（按钮内边距 / 图标字形 / 对齐）
            Capture("04-全局查找弹窗", () => new SearchDialog(), outDir, log);
            Capture("05-待办汇总", () => new TodoSummaryWindow(notes, settings), outDir, log);
            Capture("06-每日总结", () => new DailySummaryWindow(notes, settings), outDir, log);
            Capture("07-回收站", () => new RecycleBinWindow(notes, notes.RecycleBin), outDir, log);
            Capture("08-输入框", () => new InputWindow(notes, settings), outDir, log);
            Capture("09-语音输入", () => new VoiceInputWindow(settings), outDir, log);
            Capture("10-AI 对话", () => new AIDialogWindow(notes, settings), outDir, log);

            // 标题栏全功能预览：把 13 个功能全挂上、并放宽面板宽度避免溢出，
            // 用于一次性核验所有图标字形真实存在 —— 图标字符写错一个就会渲染成空框（豆腐块），
            // 这类错误静态代码看不出来，只能靠渲染结果判定。
            Capture("11-标题栏全功能预览", () =>
            {
                settings.QuickViewToolbarLeft = new List<string>
                    { "Calendar", "SyncUpload", "SyncDownload", "Search", "Refresh", "AiAsk" };
                settings.QuickViewToolbarRight = new List<string>
                    { "Export", "GetNote", "TodoSummary", "RecycleBin", "Settings", "Import", "DailySummary" };
                settings.QuickViewWidth = 1200;
                return new QuickViewWindow(notes, settings);
            }, outDir, log);

            // 图标字符自检：字体里有 ≠ WPF 渲染得出来（缺字形会变豆腐块），挑图标代码时用它一次过筛
            Capture("12-图标字符可用性自检", () => GlyphProbe.Build(), outDir, log);

            // 全启用状态下的设置面板：验证「下拉为空」的空态提示与按钮置灰。
            // 此状态在默认布局下不会出现（功能池有剩余），必须先把 13 个全挂上（复用 11 场景改过的 settings）。
            Capture("13-设置-全启用空态", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 4;
                return w;
            }, outDir, log);

            // 带附件的 AI 对话（2026-09-14）：输入区的附件 chip 与气泡里的缩略图，
            // 只在"真有附件"时才渲染，空会话快照（场景 10）覆盖不到这两个分支。
            Capture("14-AI 对话（带附件）", () =>
            {
                var w = new AIDialogWindow(notes, settings);
                w.SeedAttachmentsForSnapshot();
                return w;
            }, outDir, log);

            // 文件与网盘板块（2026-09-16）：设置页新增的一整块，含三个按钮行与多段说明文字。
            // 8 = 「文件与网盘」（0 热键 / 1 AI 模型 / 2 外观 / 3 显示 / 4 灵感速览 / 5 输入框 /
            //                  6 云同步 / 7 待办与提醒 / 8 文件与网盘 / 9 通用）
            Capture("15-设置-文件与网盘板块", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 8;
                return w;
            }, outDir, log);

            // 已选择文件的卡片区（2026-09-16）：只在"用户真点过选择文件"时才渲染，
            // 空会话快照（场景 10）覆盖不到这个分支 —— 而它正是本次红线（AI 只能引用牌号）的界面落点。
            Capture("16-AI 对话（带已选择文件卡片）", () =>
            {
                var w = new AIDialogWindow(notes, settings);
                w.SeedHandleChipsForSnapshot();
                return w;
            }, outDir, log);

            // 文件与网盘板块的**下半部分**（2026-09-16）：该板块最长，一屏装不下，
            // 滚动到底才能看到「对话附件」与「数据目录」两组 —— 而"控件被挤出可视区"正是
            // 静态代码查不出来、必须靠出图才能发现的问题类型。
            Capture("17-设置-文件与网盘板块（下半部分）", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 8;
                w.ScrollToEndForSnapshot();
                return w;
            }, outDir, log);

            // 云文件卡片的四个动作按钮（2026-09-16）：卡片只在"AI 真把文件取回并交付"时渲染，
            // 其它场景覆盖不到。加「彻底删除」后一行从三个按钮变四个 —— 正是"被挤出可视区"的高危形态。
            Capture("18-AI 对话（带云文件卡片）", () =>
            {
                var w = new AIDialogWindow(notes, settings);
                w.SeedCloudFileCardsForSnapshot();
                return w;
            }, outDir, log);

            // ── 悬浮球拖放保存（2026-09-16，方案 docs/悬浮球拖放保存方案.md §5）──
            // 这两个浮层是**独立窗口**（球窗口的透明区穿透，画在球里收不到鼠标事件），
            // 尺寸/配色/悬停都是线框数值，必须出图对照判读：小条宽 96 竖向；卡片宽 ≤276 **贴合内容**
            // （旧版卡片右侧留一大片空白，已否）；两者均**无图标**。

            Capture("19-拖放小条（得到大脑可用）",
                () => new DropActionStrip(3, getNoteAvailable: true, opacity: 1.0), outDir, log);

            // 未配得到大脑凭证 → 该项置灰 + 悬停提示去设置
            Capture("20-拖放小条（得到大脑未配置·置灰）",
                () => new DropActionStrip(3, getNoteAvailable: false, opacity: 1.0), outDir, log);

            // 非文本类文件：不显示「发到得到大脑」行（"只对文本类显示"是用户拍板）
            Capture("21-拖放卡片（单文件·非文本类）",
                () => new DropActionCard("季度汇报-2026Q3.pptx", "1.8 MB · 来自微信",
                    showGetNote: false, getNoteAvailable: false, opacity: 1.0), outDir, log);

            // 文本类文件：多一行「发到得到大脑」。标题用微信图片的自动生成名，顺带核验命名格式。
            Capture("22-拖放卡片（单文件·文本类）",
                () => new DropActionCard("微信图片_20260916_231045.jpg", "320 KB · 来自微信",
                    showGetNote: true, getNoteAvailable: true, opacity: 1.0), outDir, log);

            // 多文件：标题「N 个文件」+ 总大小（方案 §4.3，**推定未与用户确认**，出图确认形态）
            Capture("23-拖放卡片（多文件）",
                () => new DropActionCard("3 个文件", "5.2 MB · 来自文件管理器",
                    showGetNote: false, getNoteAvailable: true, opacity: 1.0), outDir, log);

            // 设置「显示」板块（3 = 显示：0 热键 / 1 AI 模型 / 2 外观 / 3 显示 / 4 灵感速览）。
            // ⚠ 方案 §9 写的「外观」板块（PanelAppearance）与实际不符 —— 三个透明度滑块其实都在
            //   「显示」（PanelDisplay）里，拖放这三项也落在同一板块（理由见 SettingsWindow.xaml 的注释）。
            //   新增设置项属 REGRESSION 维护触发条件，这里出图核验控件没被挤出可视区、默认值正确。
            Capture("24-设置-显示板块（含拖放设置）", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 3;
                return w;
            }, outDir, log);
        }
        catch (Exception ex)
        {
            log.AppendLine("!! 快照流程异常: " + ex);
        }
        finally
        {
            FocusCapturePaths.RootOverride = null;
            try { Directory.Delete(sandbox, true); } catch { /* 沙箱删不掉不影响结果 */ }
            try { File.WriteAllText(Path.Combine(outDir, "snapshot.log"), log.ToString(), Encoding.UTF8); }
            catch { /* 日志写不出也别卡住 */ }
        }

        // 快照模式下不创建主窗口，显式退出
        Application.Current?.Shutdown();
    }

    /// <summary>渲染单个窗口为 PNG。任一环节失败只记日志，不影响其余窗口。</summary>
    private static void Capture(string name, Func<Window> factory, string outDir, StringBuilder log)
    {
        Window? win = null;
        try
        {
            win = factory();
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = -32000;          // 屏幕外，用户看不见
            win.Top = -32000;
            win.ShowInTaskbar = false;
            win.Opacity = 1.0;          // 见类注释：半透明位图不利于像素比对
            win.Show();
            win.UpdateLayout();

            // 布局与渲染管线是异步的，必须各跑完一轮，否则可能截到空白或旧状态
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var dpi = VisualTreeHelper.GetDpi(win);
            var w = (int)Math.Ceiling(win.ActualWidth * dpi.DpiScaleX);
            var h = (int)Math.Ceiling(win.ActualHeight * dpi.DpiScaleY);
            if (w <= 0 || h <= 0)
            {
                log.AppendLine($"{name}: 尺寸无效 {w}x{h}（窗口未完成布局）");
                return;
            }

            var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(win);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            var path = Path.Combine(outDir, name + ".png");
            using (var fs = File.Create(path)) enc.Save(fs);

            log.AppendLine($"{name}: {w}x{h} (逻辑 {win.ActualWidth:0}x{win.ActualHeight:0}, DPI {dpi.PixelsPerInchX:0}) -> {path}");
        }
        catch (Exception ex)
        {
            log.AppendLine($"{name}: 渲染失败 — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { win?.Close(); } catch { /* 关不掉不影响后续 */ }
        }
    }

    /// <summary>解析输出目录：<c>--out &lt;dir&gt;</c> 优先，否则 %TEMP%\fc-ui-snapshot。</summary>
    private static string ResolveOutDir(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals(OutFlag, StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
        {
            try { return Path.GetFullPath(args[i + 1]); } catch { /* 非法路径回退默认 */ }
        }
        return Path.Combine(Path.GetTempPath(), "fc-ui-snapshot");
    }
}
