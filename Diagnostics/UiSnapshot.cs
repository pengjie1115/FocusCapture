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

            // AI 模型板块（2026-09-20 新增）：Skill 分区住在这里。
            // 为什么必须单独出一张：这块有三类"静态代码看不出来"的东西 ——
            //   ① 已授权 Skill 列表是 code-behind 动态生成的控件（模板样式可能不继承）
            //   ② Skill 目录是长路径，深色底上要能看清、要会换行不撑破布局
            //   ③ 分区在板块底部，不滚到底根本看不见 —— 上一版就漏了这张图
            Capture("03b-设置-AI 模型板块（含 Skill 分区）", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 1;      // 1 = 「AI 模型」
                w.UpdateLayout();                 // 先布局，否则 ScrollToEnd 无效
                w.ContentScroller.ScrollToEnd();  // Skill 分区在板块底部
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

            // ── 悬浮球拖放保存（2026-09-16）──
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

            // 多文件：标题「N 个文件」+ 总大小（**推定规则，未与用户确认**，出图确认形态）
            Capture("23-拖放卡片（多文件）",
                () => new DropActionCard("3 个文件", "5.2 MB · 来自文件管理器",
                    showGetNote: false, getNoteAvailable: true, opacity: 1.0), outDir, log);

            // 设置「显示」板块（3 = 显示：0 热键 / 1 AI 模型 / 2 外观 / 3 显示 / 4 灵感速览）。
            // ⚠ 设计稿原写「外观」板块（PanelAppearance），与实际不符 —— 三个透明度滑块其实都在
            //   「显示」（PanelDisplay）里，拖放这三项也落在同一板块（理由见 SettingsWindow.xaml 的注释）。
            //   新增设置项属 REGRESSION 维护触发条件，这里出图核验控件没被挤出可视区、默认值正确。
            Capture("24-设置-显示板块（含拖放设置）", () =>
            {
                var w = new SettingsWindow(settings, noteService: notes);
                w.NavList.SelectedIndex = 3;
                return w;
            }, outDir, log);

            // ── 悬浮球角标（2026-09-19，两轮）──
            // 起因：用户实测「角标圆被窗口边界裁掉一块、数字在圈里偏上」（第一轮），
            //       改完又反馈「压球的面积太大、观感不好」（第二轮，窗口 48→56 把角标外移）。
            // 分工：「数字是否居中」「压球压了多少」靠 LogBadgeBounds 打的数字判读；观感靠出图判读 ——
            // 本工具的渲染不含窗口矩形的裁切，只看图会把"溢出窗口"误判成没问题（见该方法注释）。
            Capture("25-悬浮球（带角标）", () => new FloatBall(), outDir, log, afterShow: win =>
            {
                if (win is not FloatBall ball) return;
                ball.SetBadge(1, hasRead: false);   // 必须在 Show 之后调：SetBadge 内部检查 IsLoaded
                LogBadgeBounds(ball, log);
            }, scale: 6);   // 6 倍放大：角标只有 18 像素，不放大看不出数字有没有偏
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
    /// <param name="afterShow">
    /// 窗口已 Show 且跑完一次布局后的钩子。两个用途：① 补那些**必须窗口已加载才生效**的状态
    /// （如悬浮球 <c>SetBadge</c> 会检查 <c>IsLoaded</c>，在 Show 之前调用会被它自己忽略，角标根本不出现）；
    /// ② 把实测尺寸打进日志（角标边界这类"看图会误判"的量必须落成数字）。
    /// </param>
    /// <param name="scale">
    /// 出图放大倍数（默认 1）。给小控件（几十像素的悬浮球角标）出放大图用 ——
    /// WPF 按 DPI 缩放渲染，矢量与文字都是高清重绘，不是把位图拉大，所以能看到亚像素级的居中偏差。
    /// </param>
    private static void Capture(string name, Func<Window> factory, string outDir, StringBuilder log,
        Action<Window>? afterShow = null, int scale = 1)
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
            afterShow?.Invoke(win);
            win.UpdateLayout();   // afterShow 若改了状态（如角标可见性），这里再排一次，确保渲染的是新布局

            // 布局与渲染管线是异步的，必须各跑完一轮，否则可能截到空白或旧状态
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var dpi = VisualTreeHelper.GetDpi(win);
            var w = (int)Math.Ceiling(win.ActualWidth * dpi.DpiScaleX * scale);
            var h = (int)Math.Ceiling(win.ActualHeight * dpi.DpiScaleY * scale);
            if (w <= 0 || h <= 0)
            {
                log.AppendLine($"{name}: 尺寸无效 {w}x{h}（窗口未完成布局）");
                return;
            }

            // DPI 同步乘以 scale：像素数与 DPI 一起放大，WPF 才会按放大后的比例**重绘**（文字/矢量依旧清晰）
            var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX * scale, dpi.PixelsPerInchY * scale, PixelFormats.Pbgra32);
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

    /// <summary>
    /// 把悬浮球角标的**实测边界**打进日志（2026-09-19）。
    ///
    /// 为什么非得打数字、不能只看图：<see cref="Capture"/> 是用 RenderTargetBitmap 渲染**视觉树**的，
    /// 而"溢出窗口矩形"这道裁切发生在更外层的窗口边界上 —— 视觉树里根本不存在这道裁切，
    /// 所以角标即使被切掉一半，快照里也照样完整画出来。**看图会得出"没问题"的错误结论。**
    /// 判据（数字形态）：Left/Top 不得为负、Right/Bottom 不得超出窗口宽高。
    /// </summary>
    private static void LogBadgeBounds(FloatBall ball, StringBuilder log)
    {
        try
        {
            var badge = ball.Badge;
            if (badge.Visibility != Visibility.Visible)
            {
                log.AppendLine("  角标：不可见（SetBadge 未生效，或窗口尚未加载）");
                return;
            }

            ball.UpdateLayout();   // 角标刚被 SetBadge 置为可见，这里必须先重排一次，否则读到的尺寸全是 0

            var origin = badge.TransformToAncestor(ball).Transform(new Point(0, 0));
            var right = origin.X + badge.ActualWidth;
            var bottom = origin.Y + badge.ActualHeight;

            log.AppendLine($"  角标边界：Left={origin.X:0.##} Top={origin.Y:0.##} Right={right:0.##} Bottom={bottom:0.##}");
            log.AppendLine($"  窗口尺寸：{ball.ActualWidth:0.##} × {ball.ActualHeight:0.##}");
            log.AppendLine(
                origin.X >= 0 && origin.Y >= 0 && right <= ball.ActualWidth && bottom <= ball.ActualHeight
                    ? "  角标位置：完整落在窗口内（不会被窗口边界裁切）"
                    : "  角标位置：⚠ 超出窗口边界 → 会被裁切");

            var text = ball.BadgeText;
            var tp = text.TransformToAncestor(badge).Transform(new Point(0, 0));
            log.AppendLine($"  数字「{text.Text}」行框：Left={tp.X:0.##} Top={tp.Y:0.##} " +
                           $"Right={tp.X + text.ActualWidth:0.##} Bottom={tp.Y + text.ActualHeight:0.##}（字号 {text.FontSize}）");
            log.AppendLine($"  角标 {badge.ActualWidth:0.##} × {badge.ActualHeight:0.##}｜角标中心 " +
                           $"({badge.ActualWidth / 2:0.##}, {badge.ActualHeight / 2:0.##})｜行框中心 " +
                           $"({tp.X + text.ActualWidth / 2:0.##}, {tp.Y + text.ActualHeight / 2:0.##})" +
                           "（行框中心是布局居中判据；字形视觉重心还要看出图）");

            // 角标与球的重叠深度（2026-09-19 第二轮加）：这是「压球压了多少」唯一的硬证据 ——
            // 看图只能得出"压得多不多"的模糊印象，说不清 9px 还是 4px；慢层检查点也断这条。
            // 球心取窗口中心、半径取 FloatBall.xaml 里球的声明尺寸一半（球在 Grid 里居中、恒为 40×40）。
            const double ballRadius = 20;
            var bx = origin.X + badge.ActualWidth / 2;
            var by = origin.Y + badge.ActualHeight / 2;
            var dist = Math.Sqrt((bx - ball.ActualWidth / 2) * (bx - ball.ActualWidth / 2)
                               + (by - ball.ActualHeight / 2) * (by - ball.ActualHeight / 2));
            var badgeRadius = badge.ActualWidth / 2;
            var overlap = ballRadius - (dist - badgeRadius);
            log.AppendLine($"  角标圆心距球心 {dist:0.##}px（球半径 {ballRadius}，角标半径 {badgeRadius:0.##}）" +
                           $" → 与球重叠 {(overlap > 0 ? overlap : 0):0.##}px" +
                           (dist > ballRadius ? "｜圆心在球外 ✓" : "｜⚠ 圆心在球内"));
            log.AppendLine("  悬停放大 1.1 倍时球半径 22：角标圆心" +
                           (dist > 22 ? "仍在球外 ✓（放大瞬间不会陷进球体）" : "⚠ 会陷进球体"));
        }
        catch (Exception ex)
        {
            log.AppendLine("  角标边界读取失败：" + ex.Message);
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
