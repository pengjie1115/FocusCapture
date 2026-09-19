using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FocusCapture;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;
using FocusCapture.Services.Agent;
using FocusCapture.Services.Baidu;
using FocusCapture.Services.Files;
using FocusCapture.Services.Sync;

/// <summary>
/// FocusCapture 同步验收测试（单机双设备模拟，验收 B/C/D/E/F）。
/// - 本地 WebDAV 桩（HttpListener 实现 PROPFIND/PUT/GET/DELETE/MKCOL）替代真实坚果云；
/// - 两台设备 A/B：独立 NotesPath + 独立 AppSettings（内存）+ 同一桩地址；
/// - 测试前备份/恢复真实 settings.json（SyncEngine 内部会调 AppSettings.Save）。
/// </summary>
internal static class Program
{
    private static int _failed;
    private static int _passed;
    private static string _sandbox = "";

    /// <summary>
    /// 逐组耗时记录（2026-09-17 新增）。慢层此前没有任何分组耗时数据，优化只能靠猜；
    /// 这里只做记录，不改变任何检查点行为，也不参与退出码。
    /// </summary>
    private static readonly List<(string Name, int Checks, long Ms)> _timings = new();

    /// <summary>进程级墙钟，用于对照「各组耗时之和」与「真实总耗时」（差值是沙箱创建等组外开销）。</summary>
    private static readonly Stopwatch _wall = Stopwatch.StartNew();

    private static void Check(bool cond, string name, string? detail = null)
    {
        Console.WriteLine((cond ? "  PASS  " : "  FAIL  ") + name);
        if (cond) _passed++;
        if (!cond)
        {
            _failed++;
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine("        说明：" + detail);
        }
    }

    private static async Task<int> Main()
    {
        Console.WriteLine("=== FocusCapture 同步检查点 ===");

        // ── 数据隔离（2026-09-11）──
        // 全程改道到临时沙箱，绝不触碰用户真实 %AppData%\FocusCapture
        //（settings.json / deleted.json / chat_history / logs 等随之全部改道）。
        // 取代旧版「备份-恢复真实 settings.json」策略 —— 旧策略若测试中途失败会留下脏数据。
        var sandbox = Path.Combine(Path.GetTempPath(), "fc-sync-sandbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        FocusCapturePaths.RootOverride = sandbox;
        _sandbox = sandbox;
        Console.WriteLine($"数据沙箱（测试结束后可手动删除）：{sandbox}");
        Console.WriteLine();

        try
        {
            // 每组外面包一层计时（Run / RunAsync）。组名与 REGRESSION.md 触发表保持一致，
            // 便于「改哪块 → 该跑哪组」时对照输出。（2026-09-17）
            Run("单测", TestUnit);
            Run("AI 附件", TestChatAttachments);           // 图片/文档/会话引用/孤儿清理（2026-09-14）
            Run("文件仓库", TestFileRepository);           // 句柄红线 / 合并规则 / 淘汰保护 / 数据目录校验（2026-09-16）
            Run("拖放保存", TestDragDropSave);             // 判定顺序 / 取值口径 / 命名规则 / 零副作用（2026-09-16）
            Run("Agent 工具", TestAgentTools);             // 原地改行 / 回收站兜底 / 表格解析 / PDF 边界 / 时间上下文（2026-09-17）
            Run("应用图标", TestAppIcon);                  // 图标两处同源 / 恢复默认回落 / 角标裁切与居中（2026-09-19）
            await RunAsync("附件到期清理", TestAttachmentCleanup);    // 到期清理 / 彻底删除 / 待清理标注 / 退避（2026-09-16 重构）
            await RunAsync("桶拆分", TestBucketSplitting);
            await RunAsync("首配目录缺失", TestFirstSyncDirMissing);  // PROPFIND 404 → 自动 MKCOL → 重试
            await RunAsync("双设备模拟", TestDualDevice);   // 验收 C（含删除流程）+ D + 自愈 E
            await RunAsync("行身份", TestLineIdentityAsync);      // 未到期待办定位/删除/跨端一致（2026-09-12）
            await RunAsync("断网/重试", TestNetworkFailure);      // 验收 C-6/C-7
            await RunAsync("游标保护", TestCursorProtectionAsync); // 解密失败时游标不该推进（2026-09-11）
        }
        finally
        {
            FocusCapturePaths.RootOverride = null;   // 还原默认，避免影响进程退出前的其他逻辑
            // 全绿则清理沙箱；有失败则保留现场供排查
            if (_failed == 0) { try { Directory.Delete(sandbox, true); } catch { } }
            else Console.WriteLine($"\n[有失败] 沙箱已保留供排查：{sandbox}");
            // 放在 finally 里：某个组中途抛异常时，已跑完的那几组耗时照样能打出来
            PrintTimingSummary();
        }
        Console.WriteLine(_failed == 0 ? "\n===== ALL TESTS PASSED =====" : $"\n===== {_failed} TEST(S) FAILED =====");
        return _failed == 0 ? 0 : 1;
    }

    // ══════════════════ 应用图标与悬浮球角标（2026-09-19） ══════════════════
    //
    // 为什么单独起一个 STA 线程：本组要构造真实的 WPF 窗口对象（Window.Icon 是 WPF 属性、
    // 悬浮球角标是 XAML 布局），而 WPF 要求 STA 线程；慢层主线程是 async（MTA），
    // 直接在它上面 new Window() 会抛 "The calling thread must be STA"。
    // 数据隔离沿用慢层沙箱：临时图标文件都写在 _sandbox（它已是 FocusCapturePaths.RootOverride）。

    private static void TestAppIcon()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { TestAppIconCore(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Exception("应用图标组在 STA 线程里失败：" + failure.Message, failure);
    }

    private static void TestAppIconCore()
    {
        var pngPath = Path.Combine(_sandbox, "probe-icon.png");
        WriteProbePng(pngPath);
        var brokenPath = Path.Combine(_sandbox, "broken-icon.png");
        File.WriteAllText(brokenPath, "这不是图片，只是名字叫 png");
        var missingPath = Path.Combine(_sandbox, "never-created.png");

        // ── 1. 图标源解析：任何一种坏输入都必须安静回落到默认 ──
        Check(AppIconService.Reload("") == false, "自定义图标路径为空 → 按默认处理（返回 false）");
        Check(AppIconService.HasCustomIcon == false, "路径为空时不得留着上一次的图标");
        Check(AppIconService.Reload(null) == false, "路径为 null → 不得抛异常，按默认处理");
        Check(AppIconService.Reload(missingPath) == false, "图标文件不存在 → 按默认处理（用户把图删了也不该出错）");
        Check(AppIconService.Reload(brokenPath) == false, "图标文件损坏 → 按默认处理且绝不抛（抛出去就是启动失败）");
        Check(AppIconService.Reload(pngPath), "指向真实 png → 必须载入成功");
        Check(AppIconService.HasCustomIcon, "载入成功后必须记录为「有自定义图标」");

        // ── 2. 套用到窗口：有自定义就设，恢复默认要能清回去 ──
        var w1 = new System.Windows.Window();
        AppIconService.Apply(w1);
        Check(w1.Icon != null, "有自定义图标时，窗口图标必须被设置（任务栏 / Alt+Tab 才会跟着变）");

        AppIconService.Reload("");
        AppIconService.Apply(w1);
        Check(w1.Icon == null, "点「恢复默认图标」后，已打开窗口必须立刻清回默认（否则得重启才同步）");

        var w2 = new System.Windows.Window();
        AppIconService.Apply(w2);
        Check(w2.Icon == null, "从没设过图标的窗口一律不碰（默认外观必须与改造前完全一致）");

        // ── 3. 托盘图标构造：坏图不能让托盘消失 ──
        using (var ok = AppIconService.BuildTrayIcon(pngPath, System.Drawing.Color.Gray))
            Check(ok != null, "托盘图标构造：有自定义图时返回图标");
        using (var fallback = AppIconService.BuildTrayIcon(brokenPath, System.Drawing.Color.Gray))
            Check(fallback != null, "托盘图标构造：图损坏时回退方块图标，而不是抛异常");

        // ── 4. 悬浮球角标（用户实测：圆被窗口切一块 + 数字不居中）──
        var ball = new FocusCapture.Windows.FloatBall
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -32000, Top = -32000,   // 屏幕外，别在用户桌面上闪一个球
            ShowInTaskbar = false
        };
        ball.Show();
        ball.SetBadge(1, hasRead: false);
        ball.UpdateLayout();

        var badge = ball.Badge;
        Check(badge.Visibility == System.Windows.Visibility.Visible,
            "角标在有未办待办时必须可见（先确认取到的是真角标，否则下面两条等于没测）");
        if (badge.Visibility == System.Windows.Visibility.Visible)
        {
            var origin = badge.TransformToAncestor(ball).Transform(new System.Windows.Point(0, 0));
            var right = origin.X + badge.ActualWidth;
            var bottom = origin.Y + badge.ActualHeight;
            Check(origin.X >= 0 && origin.Y >= 0 && right <= ball.ActualWidth && bottom <= ball.ActualHeight,
                "角标必须完整落在窗口内（超出窗口边界的部分根本不渲染，圆会被切掉一块）",
                $"实际：角标 [{origin.X:0.##},{origin.Y:0.##}]-[{right:0.##},{bottom:0.##}]，窗口 {ball.ActualWidth:0.##}×{ball.ActualHeight:0.##}");

            var text = ball.BadgeText;
            var tp = text.TransformToAncestor(badge).Transform(new System.Windows.Point(0, 0));
            var dx = Math.Abs(tp.X + text.ActualWidth / 2 - badge.ActualWidth / 2);
            var dy = Math.Abs(tp.Y + text.ActualHeight / 2 - badge.ActualHeight / 2);
            Check(dx <= 0.5 && dy <= 0.5,
                "数字必须居中于角标（文本行框中心与角标中心的偏差不得超过半像素）",
                $"实际偏差：水平 {dx:0.##}px、垂直 {dy:0.##}px");

            ball.SetBadge(12, hasRead: false);
            ball.UpdateLayout();
            Check(text.ActualWidth <= badge.ActualWidth - 2,
                "两位数时数字不得被角标边框挤住（角标靠 MinWidth 撑成胶囊形）",
                $"实际：文本宽 {text.ActualWidth:0.##}px，角标宽 {badge.ActualWidth:0.##}px");
        }
        ball.Close();

        try { File.Delete(pngPath); File.Delete(brokenPath); } catch { /* 沙箱整体也会删，这里只是不留半成品 */ }
    }

    /// <summary>写一张 32×32 的真 PNG（用 WPF 自己编码，避免往仓库里塞二进制夹具）。</summary>
    private static void WriteProbePng(string path)
    {
        var bmp = new System.Windows.Media.Imaging.WriteableBitmap(
            32, 32, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null);
        var pixels = new byte[32 * 32 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x50; pixels[i + 1] = 0xAF; pixels[i + 2] = 0x4C; pixels[i + 3] = 0xFF;
        }
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, 32, 32), pixels, 32 * 4, 0);

        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ══════════════════ 分组计时（2026-09-17） ══════════════════
    //
    // 目的：慢层此前只有「全跑一次多久」这一个数字，30 多秒具体花在哪一组全靠猜，
    // 于是「按需只跑相关组」「把无依赖的组搬回快层」这类优化没法算收益。
    // 这里只做测量：不包住任何断言、不改任何检查点行为、不参与退出码。

    private static void Run(string name, Action body)
    {
        var before = _passed;
        var sw = Stopwatch.StartNew();
        body();
        sw.Stop();
        Record(name, before, sw.ElapsedMilliseconds);
    }

    private static async Task RunAsync(string name, Func<Task> body)
    {
        var before = _passed;
        var sw = Stopwatch.StartNew();
        await body();
        sw.Stop();
        Record(name, before, sw.ElapsedMilliseconds);
    }

    private static void Record(string name, int passedBefore, long ms)
    {
        var checks = _passed - passedBefore;
        _timings.Add((name, checks, ms));
        Console.WriteLine($"  [耗时] {name} | {checks} 条 | {ms} ms");
    }

    /// <summary>
    /// 逐组耗时汇总，按耗时降序（一眼看出瓶颈在哪组）。
    /// 合计 = 各组之和；与墙钟的差值是组外开销（进程启动、沙箱创建等）。
    /// </summary>
    private static void PrintTimingSummary()
    {
        if (_timings.Count == 0) return;

        var sumMs = _timings.Sum(t => t.Ms);
        var sumChecks = _timings.Sum(t => t.Checks);

        Console.WriteLine();
        Console.WriteLine($"=== 各组耗时（按耗时降序）| 合计 {sumMs} ms / {sumChecks} 条 | 墙钟 {_wall.ElapsedMilliseconds} ms ===");
        foreach (var t in _timings.OrderByDescending(t => t.Ms))
        {
            var pct = sumMs > 0 ? t.Ms * 100.0 / sumMs : 0;
            var per = t.Checks > 0 ? (double)t.Ms / t.Checks : 0;
            Console.WriteLine($"  {t.Name} | {t.Checks} 条 | {t.Ms} ms | 占 {pct:N1}% | 均 {per:N1} ms/条");
        }
        Console.WriteLine($"  [说明] 合计 {sumMs} ms 是各组之和；墙钟 {_wall.ElapsedMilliseconds} ms 含进程启动与沙箱创建。");
        Console.WriteLine();
    }

    // ══════════════════ 悬浮球拖放保存（2026-09-16） ══════════════════
    //
    // 本组守护的是"靠真机事故换来的规则"——
    // 这些规则看代码都"很合理"，随手改一下也很像优化，改了却只在真实拖放场景下才炸：
    //   ① 判定顺序：FileDrop 必须优先于文字（微信拖 xlsx 时 Text 里装的是**文件名**，
    //      顺序一反就会把 JD-260911.xlsx 当成正文存成一条笔记）
    //   ② 取值口径：只认 UnicodeText（Chromium 的 CF_TEXT 中文是乱码 搴旂敤鍑瘉）
    //   ③ 命名规则：微信图片的哈希名必须换成"微信图片_日期_时间"
    //   ④ 零副作用：判定过程既不改源文件，也不凭空落地任何东西
    //
    // 边界（诚实标注）：真实的 OLE 拖放（鼠标拖起来、松手）**本组覆盖不到** ——
    // 那需要在真实宿主里用真人手拖，属于人工验收范围。

    private static void TestDragDropSave()
    {
        Console.WriteLine("[拖放保存] 判定顺序 / 取值口径 / 命名规则 / 零副作用");

        var dir = Path.Combine(_sandbox, "dragdrop");
        Directory.CreateDirectory(dir);

        // ── ① 判定顺序：FileDrop 优先于文字 ──

        var xlsx = Path.Combine(dir, "JD-260911.xlsx");
        File.WriteAllText(xlsx, "fake-xlsx");

        var both = new System.Windows.DataObject();
        both.SetData(System.Windows.DataFormats.FileDrop, new[] { xlsx });
        // 微信拖 xlsx 的真实形态：UnicodeText 里装的是**文件名**
        both.SetData(System.Windows.DataFormats.UnicodeText, "JD-260911.xlsx");

        var p1 = DragDropSaveService.Parse(both);
        Check(p1.Kind == DragPayloadKind.Files,
              "同时有文件与文字时必须按【文件】处理（否则文件名会被当成正文存成一条笔记）",
              $"实际判定={p1.Kind}");
        Check(p1.Paths.Count == 1 && p1.Paths[0] == xlsx,
              "文件路径必须原样带出（供后续存网盘/问答使用）");

        // FileDrop 存在但路径全为空 → 按"没有文件"处理，退回文字
        var emptyDrop = new System.Windows.DataObject();
        emptyDrop.SetData(System.Windows.DataFormats.FileDrop, Array.Empty<string>());
        emptyDrop.SetData(System.Windows.DataFormats.UnicodeText, "一段普通文字");
        Check(DragDropSaveService.Parse(emptyDrop).Kind == DragPayloadKind.Text,
              "FileDrop 存在但没有有效路径时必须退回按【文字】处理（不能判为无内容）");

        // ── ② 取值口径：只认 UnicodeText ──

        var textBoth = new System.Windows.DataObject();
        // 模拟 Chromium：Text（ANSI）是坏的，UnicodeText 是对的
        textBoth.SetData(System.Windows.DataFormats.Text, "搴旂敤鍑瘉");
        textBoth.SetData(System.Windows.DataFormats.UnicodeText, "应用凭证");

        var p2 = DragDropSaveService.Parse(textBoth);
        Check(p2.Kind == DragPayloadKind.Text && p2.Text == "应用凭证",
              "文字必须从 UnicodeText 取，绝不从 Text 取（浏览器给的 CF_TEXT 中文是乱码）",
              $"实际取到「{p2.Text}」");

        // 只有 ANSI 版文字（源程序只给了 CF_TEXT）：
        // ⚠ 这里**不能**断言"判为读不到" —— WPF 的 DataObject.GetDataPresent 默认 autoConvert=true，
        //   会把 CF_TEXT 按系统代码页自动转成 CF_UNICODETEXT 再交出来（由 Windows 做转换，不是我们自己猜编码）。
        //   这是**正确且有用**的行为：只提供 CF_TEXT 的程序照样能被拖进来。
        //   真正要守的是上面那条 —— 两者都在、且内容不一致时，必须取 UnicodeText 原值，不走转换。
        //   （本检查点最初写成了"必须判为读不到"，属检查点构造错误，已按实际语义修正，未动产品代码。）
        var onlyAnsi = new System.Windows.DataObject();
        onlyAnsi.SetData(System.Windows.DataFormats.Text, "只有ANSI的程序");
        Check(DragDropSaveService.Parse(onlyAnsi).Kind == DragPayloadKind.Text,
              "只提供 ANSI 版文字的程序必须照样能拖进来（不许因为缺少 UnicodeText 就判为无内容）");

        // 什么都没有
        Check(DragDropSaveService.Parse(new System.Windows.DataObject()).Kind == DragPayloadKind.None,
              "空数据对象必须判定为无内容");
        Check(DragDropSaveService.Parse(null).Kind == DragPayloadKind.None,
              "取不到数据对象时不许抛异常（源程序退出等边缘情况）");

        // 只有空白字符的文字 = 没有内容
        var blank = new System.Windows.DataObject();
        blank.SetData(System.Windows.DataFormats.UnicodeText, "   \r\n  ");
        Check(DragDropSaveService.Parse(blank).Kind == DragPayloadKind.None,
              "纯空白文字必须判定为无内容（否则会存出一条空笔记）");

        // ── ③ 命名规则：微信图片的哈希名 ──

        Check(DragDropSaveService.IsHashLikeName(new string('a', 32)),
              "32 位十六进制名必须被识别为哈希名（微信图片拿不到原始名）");
        Check(!DragDropSaveService.IsHashLikeName(new string('a', 30)),
              "30 位不许当成哈希名（长度不够，可能是正常名字）");
        Check(!DragDropSaveService.IsHashLikeName("697a54b2c3d4e5f6a7b8c9d0e1f2g3h4"),
              "含非十六进制字符不许当成哈希名");
        Check(!DragDropSaveService.IsHashLikeName("季度汇报-2026Q3"),
              "正常中文名不许当成哈希名");

        var hashJpg = Path.Combine(dir, "697a54b2c3d4e5f6a7b8c9d0e1f2a3b4.jpg");
        var renamed = DragDropSaveService.BuildDisplayName(hashJpg);
        Check(renamed.StartsWith("微信图片_", StringComparison.Ordinal)
              && renamed.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase),
              "微信图片的哈希名必须自动换成「微信图片_日期_时间.jpg」", $"实际「{renamed}」");

        Check(DragDropSaveService.BuildDisplayName(xlsx) == "JD-260911.xlsx",
              "正常来源必须原样保留真实文件名");

        // ── ④ 来源程序映射 ──

        Check(DragDropSaveService.MapProcessName("Weixin") == "微信", "Weixin.exe → 微信");
        Check(DragDropSaveService.MapProcessName("Tabbit Browser") == "浏览器", "Tabbit Browser → 浏览器");
        Check(DragDropSaveService.MapProcessName("chrome") == "浏览器", "chrome → 浏览器");
        Check(DragDropSaveService.MapProcessName("explorer") == "文件管理器", "explorer → 文件管理器");
        Check(DragDropSaveService.MapProcessName("wps") == "WPS", "wps → WPS");
        Check(DragDropSaveService.MapProcessName("SomeApp.exe") == "SomeApp",
              "表外进程名必须去掉 .exe 原样显示");
        Check(DragDropSaveService.MapProcessName("") == DragDropSaveService.FallbackSourceApp,
              "识别不出来源时必须兜底为「拖动」（不能留空把界面撑坏）");

        // ── ⑤ 得到大脑标题策略（推定规则）──

        Check(DragDropSaveService.MakeGetNoteTitle("第一行\n第二行") == "第一行",
              "标题取正文首行");
        Check(DragDropSaveService.MakeGetNoteTitle(new string('长', 50)).Length == 30,
              "单行超过 30 字必须截到 30 字", 
              $"实际长度 {DragDropSaveService.MakeGetNoteTitle(new string('长', 50)).Length}");
        Check(DragDropSaveService.MakeGetNoteTitle("\n\n   \n真正的首行") == "真正的首行",
              "首行为空时必须往下找第一个非空行（空标题建不出笔记）");
        Check(DragDropSaveService.MakeGetNoteTitle("") == "", "空内容必须返回空标题（由调用方兜底）");

        // ── ⑥ 文本类判定（「发到得到大脑」只对文本类显示）──

        Check(DragDropSaveService.IsTextFile("a.md") && DragDropSaveService.IsTextFile("b.TXT")
              && DragDropSaveService.IsTextFile("c.json") && DragDropSaveService.IsTextFile("d.cs"),
              "md/txt/json/代码必须判为文本类");
        Check(!DragDropSaveService.IsTextFile("a.png") && !DragDropSaveService.IsTextFile("b.docx")
              && !DragDropSaveService.IsTextFile("c.xlsx") && !DragDropSaveService.IsTextFile("d.zip"),
              "图片/docx/xlsx/压缩包不许判为文本类（二进制读出来是乱码）");
        Check(DragDropSaveService.HasAnyTextFile(new[] { "a.png", "b.md" }),
              "一批文件里只要有一个文本类，就必须显示「发到得到大脑」");
        Check(!DragDropSaveService.HasAnyTextFile(new[] { "a.png", "b.zip" }),
              "全是非文本类时必须隐藏「发到得到大脑」");

        // ── ⑦ 严格 UTF-8 读取（不猜编码）──

        var utf8File = Path.Combine(dir, "utf8.txt");
        File.WriteAllText(utf8File, "中文内容 OK", new UTF8Encoding(false));
        var (t1, e1) = DragDropSaveService.ReadTextFileStrict(utf8File);
        Check(e1 == null && t1 == "中文内容 OK", "UTF-8 文本必须原样读出", $"错误={e1}");

        var bomFile = Path.Combine(dir, "bom.txt");
        File.WriteAllBytes(bomFile, new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(new UTF8Encoding(false).GetBytes("带BOM")).ToArray());
        var (t2, _) = DragDropSaveService.ReadTextFileStrict(bomFile);
        Check(t2 == "带BOM", "BOM 必须被剥离（否则首行会多出看不见的字符）", $"实际「{t2}」");

        var gbkFile = Path.Combine(dir, "gbk.txt");
        File.WriteAllBytes(gbkFile, new byte[] { 0xB0, 0xA1, 0xB2, 0xE2 });   // GBK「测试」，非法 UTF-8
        var (t3, e3) = DragDropSaveService.ReadTextFileStrict(gbkFile);
        Check(e3 != null && t3.Length == 0,
              "非 UTF-8 文本必须明确报错，不许猜编码后把乱码传上云端", $"错误={e3}");

        var longFile = Path.Combine(dir, "long.txt");
        File.WriteAllText(longFile, new string('x', DragDropSaveService.MaxTextChars + 500));
        var (t4, _) = DragDropSaveService.ReadTextFileStrict(longFile);
        Check(t4.Length <= DragDropSaveService.MaxTextChars + 40 && t4.Contains("已截断"),
              "超长文本必须截断并留下可见说明（防一次拖入把几十 MB 日志塞进云端笔记）");

        // ── ⑧ 卡片头部（单文件 / 多文件）──

        var (title1, sub1) = DragDropSaveService.BuildCardHeader(new[] { xlsx }, "微信");
        Check(title1 == "JD-260911.xlsx", "单文件卡片标题必须是真实文件名", $"实际「{title1}」");
        Check(sub1.Contains("来自微信") && sub1.Contains("·"),
              "副行必须是「大小 · 来自来源」（不是旧版的「已复制到本机」）", $"实际「{sub1}」");

        var (title2, sub2) = DragDropSaveService.BuildCardHeader(new[] { xlsx, utf8File }, "文件管理器");
        Check(title2 == "2 个文件", "多文件标题必须是「N 个文件」（推定规则）", $"实际「{title2}」");

        // 总大小用**已知字节数**的两个文件来验，别拿随手造的小文件去 Contains("2 ") 这种模糊匹配——
        // 24 字节的文件会被格式化成「24 字节」，模糊匹配必然误红（本检查点第一版就栽在这）。
        var sizeA = Path.Combine(dir, "size-a.bin");
        var sizeB = Path.Combine(dir, "size-b.bin");
        File.WriteAllBytes(sizeA, new byte[1024]);        // 1 KB
        File.WriteAllBytes(sizeB, new byte[2048]);        // 2 KB
        var (title3, sub3) = DragDropSaveService.BuildCardHeader(new[] { sizeA, sizeB }, "文件管理器");
        Check(title3 == "2 个文件" && sub3 == "3 KB · 来自文件管理器",
              "多文件副行必须是总大小 + 来源（1024+2048 字节 → 3 KB）",
              $"实际标题「{title3}」副行「{sub3}」");

        // ── ⑨ 大小格式化 ──

        Check(DragDropSaveService.FormatSize(512) == "512 字节", "小于 1KB 显示字节");
        Check(DragDropSaveService.FormatSize(1024) == "1 KB", "1024 字节 = 1 KB");
        Check(DragDropSaveService.FormatSize(1024 * 1024) == "1 MB", "1MB 必须显示为 MB 而不是 1024 KB");

        // ── ⑩ 文件已不在（微信 temp 会被清）──

        var ghost = Path.Combine(dir, "已被微信清掉.jpg");
        var existing = DragDropSaveService.ExistingFiles(new[] { xlsx, ghost });
        Check(existing.Count == 1 && existing[0] == xlsx,
              "已不存在的文件必须被剔除（否则后续动作会对着空气执行）");

        var msg = DragDropSaveService.BuildMissingFileMessage(new[] { ghost });
        Check(msg.Contains("已被微信清掉.jpg") && msg.Length > 0,
              "「文件已不在」的提示必须点名是哪个文件");
        Check(msg.Contains("重新拖入"),
              "提示里必须给出下一步动作（否则用户只能干瞪眼）");

        // ── ⑪ 零副作用（机器可验部分）──

        var beforeFiles = Directory.GetFiles(dir).OrderBy(x => x).ToArray();
        var beforeContent = File.ReadAllText(utf8File);
        var beforeLen = new FileInfo(xlsx).Length;

        var probe = new System.Windows.DataObject();
        probe.SetData(System.Windows.DataFormats.FileDrop, new[] { xlsx, utf8File });
        var parsed = DragDropSaveService.Parse(probe);
        _ = DragDropSaveService.BuildCardHeader(parsed.Paths, parsed.SourceApp);
        _ = DragDropSaveService.ExistingFiles(parsed.Paths);

        var afterFiles = Directory.GetFiles(dir).OrderBy(x => x).ToArray();

        Check(File.Exists(xlsx) && File.Exists(utf8File),
              "判定过程绝不许移动或删除源文件（我们只读取数据，不实现移动语义）");
        Check(new FileInfo(xlsx).Length == beforeLen && File.ReadAllText(utf8File) == beforeContent,
              "判定过程绝不许改动源文件内容");
        Check(afterFiles.Length == beforeFiles.Length,
              "判定过程不许凭空落地任何文件（拖入=什么都不做的底线）",
              $"之前 {beforeFiles.Length} 个，之后 {afterFiles.Length} 个");
    }

    // ══════════════════ 网盘文件仓库（2026-09-16） ══════════════════
    //
    // 本组守护三件最容易出大事的东西：
    //   ① 红线：AI 不能凭空构造牌号去读没被用户选过的文件
    //   ② 合并：多设备合并规则（元数据近似只增不改，所以规则能这么简单）
    //   ③ 保护：还没传上去的文件绝不能被本地淘汰清掉 —— 那是真丢数据
    // 全程跑在主流程已建好的临时沙箱里（RootOverride），绝不触碰真实数据目录。

    // ══════════════════ Agent 工具（2026-09-17） ══════════════════
    //
    // 本组守护的是三件"看代码看不出来、但用户会直接受害"的事：
    //   ① 红线：文档类工具的来源只能是「用户签发的牌号 handle」或「本地元数据编号 file_id」——
    //      模型编不出来，所以它读不到没被授权过的本机文件
    //   ② 原地改行必须留后路：AI 把笔记改错了，旧内容得能从回收站捞回来
    //   ③ 解析正确性：xlsx 的**日期是序列号**（44927 而不是 2023-01-01）、文本在共享字符串表里 ——
    //      这两处弄错不会报错，只会给出一堆错数字，用户根本看不出来
    //
    // 边界（诚实标注）：本组直接调工具类的 ExecuteAsync，**不经过真实模型**；
    // "模型会不会挑对工具、会不会谎报" 要靠真实对话验收（见 REGRESSION 里的人工清单）。

    private static void TestAgentTools()
    {
        Console.WriteLine("[Agent 工具] 原地改行 / 回收站兜底 / 表格解析 / PDF 边界 / 时间上下文");

        var root = Path.Combine(_sandbox, "agent-tools");
        var notesDir = Path.Combine(root, "notes");
        Directory.CreateDirectory(notesDir);

        var settings = new AppSettings
        {
            NotesPath = notesDir,
            ExportFolderPath = Path.Combine(root, "export"),
        };
        var notes = new NoteService(settings, Path.Combine(root, "deleted.json"));

        // ── ① 时间上下文：模型必须拿到"今天几号" ──

        var fixedNow = new DateTime(2026, 9, 17, 14, 30, 0);
        Check(PromptBuilder.DescribeNow(fixedNow) == "2026-09-17 14:30 星期四",
              "当前时间描述：日期格式与星期必须正确（2026-09-17 是星期四）",
              PromptBuilder.DescribeNow(fixedNow));

        var parseMessages = PromptBuilder.BuildTimeParseMessages("明天中午12点提醒我", fixedNow);
        Check(parseMessages[0].Content != null && parseMessages[0].Content!.Contains("2026-09-17"),
              "时间解析提示词必须带上当前日期（少了基准点，'明天'只能靠猜 → 提醒时间会错）");

        // ── ② 原地改行 + 回收站兜底：笔记 ──

        var note = notes.SaveNote("原始内容ABC");
        Check(note != null, "准备：写入一条笔记");

        var noteUpdated = notes.UpdateNote(note!, "改后内容XYZ");
        var mdAfterNote = ReadAllMd(notesDir);
        Check(noteUpdated && mdAfterNote.Contains("改后内容XYZ") && !mdAfterNote.Contains("原始内容ABC"),
              "笔记原地改：新内容进文件、旧内容从文件里消失（用户要的'直接改'）");

        Check(notes.RecycleBin.List().Any(x => x.Entry.Lines.Any(l => l.Contains("原始内容ABC"))),
              "改笔记时旧内容必须先进回收站（AI 改错要能捞回来）—— 这是用户拍板要的兜底");

        // ── ③ 原地改行 + 回收站兜底：待办（2026-09-17 起也补上兜底） ──

        var todo = notes.SaveNote("待办原内容", "AI 对话", NoteType.Todo);
        Check(todo != null && todo!.Type == NoteType.Todo, "准备：写入一条待办");

        var todoUpdated = notes.UpdateTodo(todo!, newContent: "待办新内容");
        var mdAfterTodo = ReadAllMd(notesDir);
        Check(todoUpdated && mdAfterTodo.Contains("待办新内容") && !mdAfterTodo.Contains("待办原内容"),
              "待办原地改：旧内容从文件消失、新内容写入");

        Check(notes.RecycleBin.List().Any(x => x.Entry.Lines.Any(l => l.Contains("待办原内容"))),
              "改待办时旧内容也要进回收站（此前是直接消失，改造后与笔记行为一致）");

        // ── ④ 删除 → 列出 → 恢复 闭环 ──

        var toDelete = notes.SaveNote("会被删掉的笔记");
        Check(toDelete != null && notes.DeleteNote(toDelete!), "准备：删除一条笔记（进回收站）");

        var listTool = new ListRecycleBinTool(notes);
        var listOut = listTool.ExecuteAsync("{}", CancellationToken.None).GetAwaiter().GetResult();
        Check(listOut.Contains("会被删掉的笔记"), "list_recycle_bin 能列出被删条目（AI 这才看得见回收站）");

        var target = notes.RecycleBin.List().First(x => x.Entry.Preview.Contains("会被删掉的笔记"));
        var restoreTool = new RestoreDeletedTool(notes);
        var deletedAtArg = target.Entry.DeletedAt.ToString("yyyy-MM-dd HH:mm");

        // 同一分钟内改了两条 + 删了一条 → 三条回收站记录的删除时间（分钟精度）完全一样。
        // 这是真实使用中最容易撞上的情形：不带关键词就恢复 = 让 AI 自己挑一条，必须要求消歧。
        var ambiguous = restoreTool.ExecuteAsync(
            "{\"deleted_at\":\"" + deletedAtArg + "\"}", CancellationToken.None).GetAwaiter().GetResult();
        Check(ambiguous.Contains("content_hint"),
              "同一分钟有多条回收站记录时必须要求消歧，绝不能自己猜一条恢复", ambiguous);

        var restoreOut = restoreTool.ExecuteAsync(
            "{\"deleted_at\":\"" + deletedAtArg + "\",\"content_hint\":\"会被删掉\"}",
            CancellationToken.None).GetAwaiter().GetResult();

        Check(restoreOut.Contains("已从回收站恢复") && ReadAllMd(notesDir).Contains("会被删掉的笔记"),
              "restore_deleted 带消歧关键词后能把条目恢复回笔记文件", restoreOut);

        var restoreAgain = restoreTool.ExecuteAsync(
            "{\"deleted_at\":\"2000-01-01 00:00\"}", CancellationToken.None).GetAwaiter().GetResult();
        Check(restoreAgain.StartsWith("错误："), "恢复不存在的删除时间必须报错，不能假装成功");

        // ── ⑤ 按日期查询 ──

        var dateTool = new ListNotesByDateTool(notes);
        var todayOut = dateTool.ExecuteAsync("{}", CancellationToken.None).GetAwaiter().GetResult();
        Check(todayOut.Contains("改后内容XYZ") && todayOut.Contains("待办新内容") && todayOut.Contains("会被删掉的笔记"),
              "list_notes_by_date 默认返回今天的全部条目（含刚恢复的那条）");

        var oldDateOut = dateTool.ExecuteAsync("{\"date\":\"2000-01-01\"}", CancellationToken.None).GetAwaiter().GetResult();
        Check(oldDateOut.Contains("没有记录"), "查一个没有记录的日期要如实说没有，不能编");

        // ── ⑥ 统计：已知输入 → 精确期望 ──
        // 本沙箱到这一步为止，今天共有 3 条（笔记 1 + 待办 1 + 恢复回来的笔记 1）
        // 注意口径：待办按 TodoDisplayTime 归类，这条待办没有提醒时间 → 归到创建当天

        var statsTool = new NoteStatsTool(notes);
        var statsOut = statsTool.ExecuteAsync("{}", CancellationToken.None).GetAwaiter().GetResult();
        Check(statsOut.Contains($"共记录 3 条"), "note_stats 的条数必须与已知输入精确一致（本沙箱今天 3 条）",
              statsOut.Replace("\n", " / "));
        Check(statsOut.Contains("未完成 1 条"), "note_stats 的待办计数：本沙箱未完成待办 1 条");

        // ── ⑦ 导出：落到设置的导出文件夹，不上网盘 ──

        var exportTool = new ExportNotesTool(notes, settings);
        var exportOut = exportTool.ExecuteAsync("{}", CancellationToken.None).GetAwaiter().GetResult();
        var exported = Directory.Exists(settings.ExportFolderPath)
            ? Directory.GetFiles(settings.ExportFolderPath)
            : Array.Empty<string>();

        Check(exportOut.Contains("已导出 3 条") && exported.Length == 1,
              "export_notes 导出条数正确，且文件落在设置的导出文件夹",
              exportOut);
        Check(exported.Length == 1 && File.ReadAllText(exported[0]).Contains("改后内容XYZ"),
              "导出文件里确实含有笔记正文（不是空文件）");
        Check(exportOut.Contains("未上传网盘"), "导出结果必须说清'没上网盘'（用户明确要求导出不上云）");

        // ── ⑦-2 精选导出：用户反馈「只能导整天，包容度不够」→ 必须能只导指定的几条 ──
        // 原来的死结：用户说「我只要那两条」，工具只能整段导（夹带当天其它内容）或宣告做不到。
        //
        // 沙箱现状恰好是最难的情形：今天这几条**都落在同一分钟**（脚本连续写入）。
        // 这不是巧合而是真实约束 —— 笔记落盘时间只到分钟（NoteEntry.ToMarkdownLine 用 yyyy-MM-dd HH:mm），
        // 同一分钟记的多条在时间戳上完全一样。所以本组守护两件事：
        //   ① 撞车时必须报错 + 给出可执行出路（补 hint），绝不自己猜一条
        //   ② 带 hint 后必须能精确导指定的条目、不夹带当天其它内容

        var allEntries = notes.LoadAllEntries();
        var pickNote = allEntries.First(e => e.Content.Contains("改后内容XYZ"));
        var minute = pickNote.Timestamp.ToString("yyyy-MM-dd HH:mm");

        var filesBeforePick = Directory.GetFiles(settings.ExportFolderPath).Length;
        var ambiguousOut = exportTool.ExecuteAsync(
            "{\"ref_times\":[\"" + minute + "\"]}", CancellationToken.None).GetAwaiter().GetResult();
        Check(ambiguousOut.StartsWith("错误：") && ambiguousOut.Contains("hint")
              && Directory.GetFiles(settings.ExportFolderPath).Length == filesBeforePick,
              "同一分钟有多条时，纯时间戳必须报错不猜，且给出可执行出路（补 hint）",
              ambiguousOut.Replace("\n", " / "));

        var pickOut = exportTool.ExecuteAsync(
            "{\"ref_times\":[{\"time\":\"" + minute + "\",\"hint\":\"改后内容\"}," +
            "{\"time\":\"" + minute + "\",\"hint\":\"待办新内容\"}]}",
            CancellationToken.None).GetAwaiter().GetResult();
        Check(pickOut.Contains("已导出 2 条") && Directory.GetFiles(settings.ExportFolderPath).Length == filesBeforePick + 1,
              "ref_times 带 hint 的精选：只导指定的 2 条（用户说的'我只要这两条'）", pickOut);

        var pickFiles = Directory.GetFiles(settings.ExportFolderPath, "*精选*");
        var pickText = pickFiles.Length == 1 ? File.ReadAllText(pickFiles[0]) : "";
        Check(pickText.Contains("改后内容XYZ") && pickText.Contains("待办新内容") && !pickText.Contains("会被删掉的笔记"),
              "精选导出的文件里**只有那 2 条，不夹带当天其它内容** —— 这正是原来做不到的事");

        // 找不到的时间戳：必须报错且一个文件都不写（不能让用户拿到半份自己去核）
        var filesBeforeMiss = Directory.GetFiles(settings.ExportFolderPath).Length;
        var pickMissing = exportTool.ExecuteAsync(
            "{\"ref_times\":[\"2000-01-01 00:00\"]}", CancellationToken.None).GetAwaiter().GetResult();
        Check(pickMissing.StartsWith("错误：") && Directory.GetFiles(settings.ExportFolderPath).Length == filesBeforeMiss,
              "ref_times 找不到条目时必须报错且不写文件，不能导半份更不能假装成功", pickMissing);

        // 兜底：模型偶尔给逗号分隔的复合字符串，只认 JSON 数组必然踩空
        var commaOut = exportTool.ExecuteAsync(
            "{\"ref_times\":\"2000-01-01 00:00,2000-01-02 00:00\"}", CancellationToken.None).GetAwaiter().GetResult();
        Check(commaOut.Contains("2000-01-01 00:00 找不到") && commaOut.Contains("2000-01-02 00:00 找不到"),
              "ref_times 传逗号分隔字符串也要能解析（不能只认 JSON 数组）", commaOut.Replace("\n", " / "));

        // query：按关键词只导命中的条目
        var queryOut = exportTool.ExecuteAsync("{\"query\":\"改后内容\"}", CancellationToken.None).GetAwaiter().GetResult();
        Check(queryOut.Contains("已导出 1 条"), "export_notes 的 query：按关键词只导命中的那 1 条", queryOut);

        var queryScoped = exportTool.ExecuteAsync(
            "{\"query\":\"改后内容\",\"date\":\"2000-01-01\"}", CancellationToken.None).GetAwaiter().GetResult();
        Check(queryScoped.Contains("没有找到"), "query 配日期时只在该日期内找，别的日子的命中不能算进来");

        var queryMiss = exportTool.ExecuteAsync(
            "{\"query\":\"绝对不存在的词zzz\"}", CancellationToken.None).GetAwaiter().GetResult();
        Check(queryMiss.Contains("没有找到"), "query 无命中要如实说没有，不能导出一份空文件");

        // ── ⑧ 表格解析：共享字符串 / 日期序列号 / 稀疏列 ──

        var xlsxPath = Path.Combine(root, "测试表.xlsx");
        BuildTestXlsx(xlsxPath);

        var sheets = XlsxTextExtractor.Read(xlsxPath);
        Check(sheets.Count == 2, "xlsx 能识别出 2 个工作表（多 sheet）", $"实际 {sheets.Count}");
        Check(sheets[0].Name == "表一" && sheets[1].Name == "表二", "工作表名称取自 workbook.xml，不是文件名");
        Check(sheets[0].RowCount == 3, "行数正确（3 行）");

        var row2 = sheets[0].Rows[1];
        Check(row2.Count >= 3 && row2[0] == "张三" && row2[1] == "1234.5",
              "共享字符串列 + 数字列解析正确（文本单元格存的是索引，不查表就是一堆数字）",
              string.Join(" | ", row2));
        Check(row2.Count >= 3 && row2[2] == "2023-01-01",
              "日期列必须按序列号还原成日期（44927 → 2023-01-01）—— 直接输出数字等于给用户错数据",
              row2.Count >= 3 ? row2[2] : "(缺列)");

        var row3 = sheets[0].Rows[2];
        Check(row3.Count >= 3 && row3[0] == "李四" && row3[1] == "" && row3[2] == "7",
              "稀疏行按列号落位（B 列空缺不能把 C 列顶到 B 列去）",
              string.Join(" | ", row3));

        // ── ⑨ 表格工具：走牌号读取 ──

        FileHandleStore.Clear();
        var xlsxHandle = FileHandleStore.Register(xlsxPath);
        var sheetTool = new ReadSpreadsheetTool();
        var sheetOut = sheetTool.ExecuteAsync(
            "{\"handle\":\"" + xlsxHandle.Id + "\"}", CancellationToken.None).GetAwaiter().GetResult();

        Check(sheetOut.Contains("表一") && sheetOut.Contains("2023-01-01") && sheetOut.Contains("1234.5"),
              "read_spreadsheet：表名 / 日期 / 数字都要正确出现在输出里",
              sheetOut.Replace("\n", " / "));
        Check(sheetOut.Contains("共 3 行"), "read_spreadsheet 报出总行数（让模型知道有没有被截断）");

        var badSheetOut = sheetTool.ExecuteAsync(
            "{\"handle\":\"" + xlsxHandle.Id + "\",\"sheet\":\"不存在的表\"}", CancellationToken.None).GetAwaiter().GetResult();
        Check(badSheetOut.StartsWith("错误：") && badSheetOut.Contains("表二"),
              "指定不存在的工作表要报错并列出可选项，不能悄悄换一个表读");

        // ── ⑩ PDF：能读文字层，扫描件如实说读不了 ──

        var textPdf = Path.Combine(root, "有文字.pdf");
        BuildTestPdf(textPdf, "Hello PdfProbe extraction works");

        var pdfTool = new ReadPdfTool();
        var textPdfHandle = FileHandleStore.Register(textPdf);
        var pdfOut = pdfTool.ExecuteAsync(
            "{\"handle\":\"" + textPdfHandle.Id + "\"}", CancellationToken.None).GetAwaiter().GetResult();
        Check(pdfOut.Contains("Hello PdfProbe extraction works"),
              "read_pdf 能从有文字层的 PDF 里读出文字", pdfOut);

        var scanPdf = Path.Combine(root, "扫描件样式.pdf");
        BuildTestPdf(scanPdf, null);   // 页面里没有文字（模拟扫描件）
        var scanHandle = FileHandleStore.Register(scanPdf);
        var scanOut = pdfTool.ExecuteAsync(
            "{\"handle\":\"" + scanHandle.Id + "\"}", CancellationToken.None).GetAwaiter().GetResult();
        Check(scanOut.StartsWith("错误：") && scanOut.Contains("扫描件"),
              "**扫描件/图片版 PDF 必须如实说读不了**，绝不能返回空内容让模型自己去编",
              scanOut);

        // ── ⑪ 红线：文档来源不可伪造 ──

        var fakeHandle = pdfTool.ExecuteAsync("{\"handle\":\"file:20200101-99\"}", CancellationToken.None)
            .GetAwaiter().GetResult();
        Check(fakeHandle.StartsWith("错误："),
              "凭空编造的牌号必须被拒绝（AI 读不到没被用户选过的本机文件）", fakeHandle);

        var pathAsHandle = pdfTool.ExecuteAsync("{\"handle\":\"C:\\\\Windows\\\\win.ini\"}", CancellationToken.None)
            .GetAwaiter().GetResult();
        Check(pathAsHandle.StartsWith("错误："),
              "把本机路径当牌号传进来必须被拒绝（路径不是牌号）", pathAsHandle);

        var noSource = pdfTool.ExecuteAsync("{}", CancellationToken.None).GetAwaiter().GetResult();
        Check(noSource.Contains("handle") && noSource.Contains("file_id"),
              "既没给 handle 也没给 file_id 时，要给出可执行的指引（让用户去点『选择文件』）", noSource);

        FileHandleStore.Clear();
    }

    private static string ReadAllMd(string dir) =>
        Directory.Exists(dir)
            ? string.Join("\n", Directory.GetFiles(dir, "*.md").Select(File.ReadAllText))
            : "";

    /// <summary>手工构造一个最小 xlsx（含 2 个工作表 / 共享字符串 / 日期样式 / 稀疏行），用于验证解析正确性</summary>
    private static void BuildTestXlsx(string path)
    {
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        void Add(string name, string xml)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            var bytes = new UTF8Encoding(false).GetBytes(xml);
            stream.Write(bytes, 0, bytes.Length);
        }

        Add("[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>");

        Add("xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"" + ns + "\" xmlns:r=\"" + relNs + "\"><sheets>" +
            "<sheet name=\"表一\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"表二\" sheetId=\"2\" r:id=\"rId2\"/>" +
            "</sheets></workbook>");

        Add("xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"" + relNs + "/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"" + relNs + "/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
            "<Relationship Id=\"rId3\" Type=\"" + relNs + "/sharedStrings\" Target=\"sharedStrings.xml\"/>" +
            "</Relationships>");

        // 文本单元格只存索引，必须靠这张表还原 —— 索引错位就是全表内容错
        Add("xl/sharedStrings.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><sst xmlns=\"" + ns + "\" count=\"5\" uniqueCount=\"5\">" +
            "<si><t>姓名</t></si><si><t>金额</t></si><si><t>日期</t></si><si><t>张三</t></si><si><t>李四</t></si></sst>");

        // cellXfs[1] 用内置日期格式 14（mm-dd-yy）→ 该样式的数字单元格必须当日期解释
        Add("xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><styleSheet xmlns=\"" + ns + "\"><cellXfs count=\"2\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "</cellXfs></styleSheet>");

        Add("xl/worksheets/sheet1.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"" + ns + "\"><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>3</v></c><c r=\"B2\"><v>1234.5</v></c><c r=\"C2\" s=\"1\"><v>44927</v></c></row>" +
            "<row r=\"3\"><c r=\"A3\" t=\"s\"><v>4</v></c><c r=\"C3\"><v>7</v></c></row>" +
            "</sheetData></worksheet>");

        Add("xl/worksheets/sheet2.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"" + ns + "\"><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>第二表</t></is></c></row>" +
            "</sheetData></worksheet>");
    }

    /// <summary>手工构造最小 PDF（含 xref）；text 传 null = 页面里没有任何文字（模拟扫描件）</summary>
    private static void BuildTestPdf(string path, string? text)
    {
        var content = text == null ? "" : "BT /F1 24 Tf 72 700 Td (" + text + ") Tj ET";
        var bodies = new[]
        {
            "<</Type /Catalog /Pages 2 0 R>>",
            "<</Type /Pages /Kids [3 0 R] /Count 1>>",
            "<</Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources <</Font <</F1 5 0 R>>>>>>",
            "<</Length " + content.Length + ">>\nstream\n" + content + "\nendstream",
            "<</Type /Font /Subtype /Type1 /BaseFont /Helvetica>>",
        };

        using var ms = new MemoryStream();
        void Write(string s)
        {
            var bytes = Encoding.Latin1.GetBytes(s);
            ms.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.4\n");
        var offsets = new long[bodies.Length + 1];
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = ms.Position;
            Write((i + 1) + " 0 obj\n" + bodies[i] + "\nendobj\n");
        }

        var xrefPos = ms.Position;
        Write("xref\n0 " + (bodies.Length + 1) + "\n");
        Write("0000000000 65535 f \n");
        for (var i = 1; i <= bodies.Length; i++)
            Write(offsets[i].ToString("D10") + " 00000 n \n");
        Write("trailer\n<</Size " + (bodies.Length + 1) + " /Root 1 0 R>>\nstartxref\n" + xrefPos + "\n%%EOF\n");

        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void TestFileRepository()
    {
        Console.WriteLine("[文件仓库] 句柄红线 / 合并规则 / 淘汰保护 / 数据目录校验");

        FileMetadata MakeMeta(string id, string name, DateTime created, DateTime updated, bool deleted = false) => new()
        {
            Id = id, Name = name, NetPath = "/apps/FocusCapture/files/" + name, Size = 100, Md5 = "md5-" + id,
            Type = FileTypes.Upload, CreatedAt = created, UpdatedAt = updated, Deleted = deleted,
        };

        // ── ① 红线：牌号只能由用户选择产生，模型编不出来 ──
        FileHandleStore.Clear();

        Check(!FileHandleStore.TryResolve("file:20260101-99", out _, out _),
              "凭空编造的牌号必须解析失败（AI 无法借此读到没被选过的文件）");

        Check(!FileHandleStore.TryResolve(@"C:\Windows\win.ini", out _, out _),
              "把本机路径直接当牌号传进来必须被拒绝（路径不是牌号）");

        Check(!FileHandleStore.TryResolve("", out _, out _),
              "空牌号必须被拒绝并给出「请让用户点选择文件」的指引");

        var sampleFile = Path.Combine(_sandbox, "用户选过的文件.txt");
        File.WriteAllText(sampleFile, "hello");
        var handle = FileHandleStore.Register(sampleFile);

        Check(handle.Id.StartsWith("file:", StringComparison.Ordinal) && handle.Id.Contains('-'),
              "牌号格式必须是 file:yyyyMMdd-N", $"实际「{handle.Id}」");

        Check(FileHandleStore.TryResolve(handle.Id, out var resolved, out _) && resolved == sampleFile,
              "用户注册过的牌号必须能解析回原路径");

        FileHandleStore.Remove(handle.Id);
        Check(!FileHandleStore.TryResolve(handle.Id, out _, out _),
              "被移除的牌号必须立即失效");

        // ── ② 多设备合并规则 ──
        var t0 = new DateTime(2026, 9, 16, 10, 0, 0);

        var oldVersion = MakeMeta("id-a", "旧名字", t0.AddDays(-1), t0);
        var newVersion = MakeMeta("id-a", "新名字", t0.AddDays(-1), t0.AddHours(1));
        var m1 = FileStoreSync.Merge(new[] { oldVersion }, new[] { newVersion });
        Check(m1.Count == 1 && m1[0].Name == "新名字",
              "同一 id 两侧都有时取 UpdatedAt 较新的那条（重命名/改标签场景）");

        var m2 = FileStoreSync.Merge(new[] { oldVersion }, new[] { MakeMeta("id-b", "另一台加的", t0, t0) });
        Check(m2.Count == 2, "两台设备各加新记录 → 按 id 求并集，不产生冲突");

        var liveAfterTagEdit = MakeMeta("id-c", "只是改了标签", t0.AddDays(-1), t0);
        var tombstone = MakeMeta("id-c", "被删了", t0.AddDays(-1), t0.AddMinutes(30), deleted: true);
        var m3 = FileStoreSync.Merge(new[] { liveAfterTagEdit }, new[] { tombstone });
        Check(m3[0].Deleted, "一边删、一边只是小改动 → 必须以删除墓碑为准（防止被小改动复活）");

        var reuploaded = MakeMeta("id-c", "删除后重新上传", t0.AddHours(2), t0.AddHours(2));
        var m4 = FileStoreSync.Merge(new[] { tombstone }, new[] { reuploaded });
        Check(!m4[0].Deleted, "删除之后重新登记的文件必须能覆盖旧墓碑（否则重传永远不生效）");

        // ── ③ 淘汰保护：未上传完成的文件是禁区 ──
        var pendingFile = Path.Combine(_sandbox, "还没传上去的文件.txt");
        File.WriteAllText(pendingFile, new string('x', 4096));
        var (pendingMeta, pendingError) = FileRepository.RegisterLocalFile(pendingFile, FileTypes.Upload);
        Check(pendingMeta != null, "登记本地文件必须成功", pendingError ?? "");

        if (pendingMeta != null)
        {
            Check(FileRepository.PendingUploadIds().Contains(pendingMeta.Id),
                  "刚登记的本地文件必须进入待上传队列");

            CacheEvictor.Enabled = true;
            CacheEvictor.MaxBytes = 1;      // 强制「超容量」
            CacheEvictor.IdleDays = 1;      // 且「已超期」

            var protectReport = CacheEvictor.Run(force: true);
            Check(protectReport.Removed == 0,
                  "尚未上传完成的文件绝不能被淘汰（云端没有副本，删掉就是真丢数据）",
                  $"却清理了 {protectReport.Removed} 个");

            // 标为已上传 → 才允许释放
            FileRepository.RecordUploadResult(pendingMeta.Id, true, null);
            var storedPath = FileRepository.FindCache(pendingMeta.Id)?.LocalPath ?? "";

            var evictReport = CacheEvictor.Run(force: true);
            Check(evictReport.Removed == 1, "已上传完成的文件在超限时必须被释放", $"实际 {evictReport.Removed} 个");
            Check(!File.Exists(storedPath), "淘汰必须真的删掉本地副本文件");
            Check(FileRepository.FindMetadata(pendingMeta.Id) != null,
                  "淘汰只删本地副本，元数据必须保留（云端仍然是权威副本）");

            // 还原默认，别影响后面可能用到淘汰器的场景
            CacheEvictor.MaxBytes = CacheEvictor.DefaultMaxBytes;
            CacheEvictor.IdleDays = CacheEvictor.DefaultIdleDays;
        }

        // ── ④ 数据目录校验 ──
        Check(!RootMigrationService.ValidateTarget(@"D:\", _sandbox).Ok,
              "盘符根目录不能被选作数据目录");

        Check(!RootMigrationService.ValidateTarget(@"\\server\share", _sandbox).Ok,
              "网络路径不能被选作数据目录");

        Check(!RootMigrationService.ValidateTarget(Path.Combine(_sandbox, "sub"), _sandbox).Ok,
              "当前数据目录的子目录必须被拒绝（否则复制会自我递归）");

        var parentDir = Directory.GetParent(_sandbox)?.FullName ?? _sandbox;
        Check(!RootMigrationService.ValidateTarget(parentDir, _sandbox).Ok,
              "当前数据目录的上级目录必须被拒绝（同样会自我递归）");

        // 注意：合法目标不能放在数据根**内部**（那是上一条刚验过的拒绝场景）。
        // 用沙箱之外的一个全新临时目录 —— 校验过程会创建目录并写探针文件，跑完清掉。
        var outsideBase = Path.Combine(Path.GetTempPath(), "fc-val-" + Guid.NewGuid().ToString("N"));
        var goodTarget = Path.Combine(outsideBase, "新位置");
        try
        {
            var goodValidation = RootMigrationService.ValidateTarget(goodTarget, _sandbox);
            Check(goodValidation.Ok && goodValidation.State == RootTargetState.NotExist,
                  "合法的全新目录必须通过校验并识别为「不存在」", goodValidation.Message);
        }
        finally
        {
            try { Directory.Delete(outsideBase, true); } catch { /* 清理失败不影响结论 */ }
        }

        // ── ⑤ 网盘沙箱守卫（2026-09-16 事故回归，必须守住） ──
        // 事故原貌：守卫一律要求 "/apps/" 前缀，于是「列 /apps」这种合法只读请求也被拒；
        // 而建目录时逐级探测父目录**必然要列一次 /apps** → 测试连接报错、
        // 每个文件 78 毫秒内失败 5 次、网盘永远是空的。下面几条把这个边界钉死。
        Check(ThrowsSandbox(() => BaiduNetdiskClient.EnsureSandboxed(null!)),
              "空路径必须被拒绝");
        Check(ThrowsSandbox(() => BaiduNetdiskClient.EnsureSandboxed("/")),
              "网盘根目录必须被拒绝");
        Check(ThrowsSandbox(() => BaiduNetdiskClient.EnsureSandboxed("/我的文档/a.md")),
              "沙箱以外的路径必须被拒绝（这是「AI 只能碰用户给的文件」的最后一道兜底）");
        Check(ThrowsSandbox(() => BaiduNetdiskClient.EnsureSandboxed("/appsFake/a.md")),
              "前缀相似但不是沙箱的路径必须被拒绝（/appsFake 不是 /apps）");
        Check(ThrowsSandbox(() => BaiduNetdiskClient.EnsureSandboxed("/apps/../etc/passwd")),
              "含 .. 的路径必须被拒绝");

        Check(!ThrowsSandbox(() => BaiduNetdiskClient.EnsureSandboxed("/apps/FocusCapture")),
              "应用目录本身必须放行");
        Check(!ThrowsSandbox(() => BaiduNetdiskClient.EnsureSandboxed("/apps/FocusCapture/files/a.md")),
              "应用目录下的文件必须放行");

        Check(ThrowsSandbox(() => BaiduNetdiskClient.EnsureSandboxed("/apps")),
              "默认语义（写操作）不许把 /apps 本身当目标路径");
        Check(!ThrowsSandbox(() => BaiduNetdiskClient.EnsureSandboxed("/apps", allowSandboxRoot: true)),
              "列目录场景必须放行 /apps 本身（本次事故的正面回归：它一挂，建目录与自检整条挂掉）");

        Check(BaiduNetdiskClient.NormalizeNetRoot("/我的文档") == BaiduNetdiskClient.DefaultNetRoot,
              "越界的网盘目录配置必须回退默认值，不能带进运行时");
        Check(BaiduNetdiskClient.NormalizeNetRoot("apps/FocusCapture") == "/apps/FocusCapture",
              "缺前导斜杠的网盘目录配置必须被纠正");

        // ── ⑥ precreate 响应解析：读不懂也不许抛，且方向不许反（2026-09-16 两次事故的正面回归） ──
        // 事故一：响应里的 block_list 是「**待上传**分片序号」数字数组，代码当 md5 字符串读 →
        //         抛 InvalidOperationException，每个文件都传不上去，而报错里没有"百度"二字。
        // 事故二：拿 return_type 当秒传判据 → 每个文件（含全新文件）都跳过全部分片上传 →
        //         create 报 31500（uploadid 无效或上传未完成），网盘恒空。
        // 这组的契约：① 任何形状都不抛异常；② 只有「字段存在且为空数组」才允许判为"无需上传"；
        //             ③ 字段缺失/读不懂一律 HasField=false，调用方据此按「全部上传」处理（方向不能反）。
        static (bool HasField, HashSet<int> Slices) ParseSlices(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return BaiduNetdiskClient.ParseMissingSlices(doc.RootElement);
        }

        var realShape = ParseSlices("""{"errno":0,"return_type":1,"block_list":[0,1],"uploadid":"x"}""");
        Check(realShape.HasField && SetsEqual(realShape.Slices, 0, 1),
              "数字数组（服务端真实口径）必须解析成「待上传分片」序号，绝不抛异常");

        var emptyList = ParseSlices("""{"errno":0,"block_list":[],"uploadid":"x"}""");
        Check(emptyList.HasField && emptyList.Slices.Count == 0,
              "空数组 = 服务端显式宣告无需上传（唯一允许判为秒传的形状）");

        var missingField = ParseSlices("""{"errno":0,"uploadid":"x"}""");
        Check(!missingField.HasField && missingField.Slices.Count == 0,
              "字段缺失必须报 HasField=false —— 调用方据此按「全部上传」处理（不知道缺哪些片就只能全传）");

        Check(!ParseSlices("""{"errno":0,"block_list":"1,2"}""").HasField,
              "字段类型完全不对时必须报 HasField=false，不许抛异常");

        Check(ParseSlices("""{"errno":0,"block_list":[null,true,{"a":1},2]}""").Slices.Count == 1,
              "数组里混进无法识别的元素时必须忽略该元素、其余照常，不许整体失败");

        Check(SetsEqual(ParseSlices("""{"errno":0,"block_list":["3","1"]}""").Slices, 1, 3),
              "字符串形式的序号也要认（服务端改类型时不至于再瘫一次）");

        Check(ParseSlices("""{"errno":0,"block_list":[-1,0]}""").Slices.Count == 1,
              "非法序号（负数）必须被忽略，只保留合法值");

        // 事故二的完整形状：return_type=1 + 分片列表非空。判据只能落在"列表是否为空"上，
        // return_type 只配当诊断信息 —— 它从来不是秒传信号（本站没传 content_md5/slice_md5，
        // 服务端压根无从判断"内容已存在"）。
        var returnTypeTrap = ParseSlices("""{"errno":0,"return_type":1,"block_list":[0,1,2,3],"uploadid":"x"}""");
        Check(returnTypeTrap.HasField && returnTypeTrap.Slices.Count == 4,
              "return_type=1 且分片列表非空时，判据必须落在「需要上传」（曾误判成秒传 → 所有文件 31500）");

        // 分片上传域名（locateupload）—— 2026-09-16 第四次事故：域名写死 pan.baidu.com → 分片全部 403。
        // 官方要求"传数据前先要域名"，挑错域名在真机上只表现为 403，从现象反查很远，所以在这里钉死。
        static string? PickHost(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return BaiduNetdiskClient.PickUploadHost(doc.RootElement);
        }

        Check(PickHost("""{"error_code":0,"servers":["https://c3.pcs.baidu.com","https://d.pcs.baidu.com"]}""")
                  == "https://c3.pcs.baidu.com",
              "必须取 servers 里第一个 https 域名（官方说按就近与速度排序，第一个是推荐值）");

        Check(PickHost("""{"error_code":0,"servers":["http://a.pcs.baidu.com","https://b.pcs.baidu.com"]}""")
                  == "https://b.pcs.baidu.com",
              "只接受 https 域名（官方明确说用 servers 里 https 协议的任意一个）");

        Check(PickHost("""{"error_code":0,"servers":[null,123,{"a":1}]}""") == null,
              "servers 里没有字符串元素时必须返回 null（调用方退兜底域名），不许抛异常");

        Check(PickHost("""{"error_code":0}""") == null && PickHost("""{"error_code":0,"servers":"x"}""") == null,
              "字段缺失或类型不对时必须返回 null，不许抛异常");

        Check(PickHost("""{"error_code":0,"servers":["https://c3.pcs.baidu.com/"]}""") == "https://c3.pcs.baidu.com",
              "域名末尾的斜杠必须被吃掉（否则拼出来是双斜杠的 URL）");

        Check(PickHost("""{"error_code":0,"servers":["c3.pcs.baidu.com"]}""") == "https://c3.pcs.baidu.com",
              "裸域名必须自动补 https://（服务端两种写法都见过）");

        // 真实响应样本（2026-09-16 用本机令牌实测拿到，见 REGRESSION B-14）：
        // servers 的元素是**对象** {"server":"url"}，不是字符串。
        // 早先只认字符串元素 → 每次都挑不到域名 → 只能退兜底，日志里刷"没有可用的 https 域名"。
        var realLocate = """{"error_code":0,"expire":60,"host":"c.pcs.baidu.com","prov":"chongqing","isp":"cnc","servers":[{"server":"https://c5.pcs.baidu.com"},{"server":"https://c6.pcs.baidu.com"},{"server":"http://c5.pcs.baidu.com"}]}""";
        Check(PickHost(realLocate) == "https://c5.pcs.baidu.com",
              "真实响应（servers 是对象数组）必须挑出第一个 https 域名 —— 只认字符串就会挑不到");

        Check(PickHost("""{"error_code":0,"servers":[{"server":"http://c5.pcs.baidu.com"},{"server":"https://c6.pcs.baidu.com"}]}""")
                  == "https://c6.pcs.baidu.com",
              "对象数组里 https 排在 http 后面时，仍必须跳到 https（token 在 query 上，不能走明文）");

        Check(PickHost("""{"error_code":0,"host":"c3.pcs.baidu.com"}""") == "https://c3.pcs.baidu.com",
              "没有 servers 时必须能读 host 字段（响应结构不止一种，别只认一种）");

        // ── ⑦ 工具输出必须体现真实上传状态（AI 谎报"已上传成功"的正面回归） ──
        // 事故原貌：账本里 5 个文件全是 failed，模型却对用户说"两个文件都已经上传成功，网盘上都能看到了"。
        // 根因不在模型 —— **输出里压根没有"传没传上去"这个信息**，它只能拿「本机已有」去脑补。
        var descCheck = FileRepository.RegisterText("输出措辞检查", "desc-check.txt", FileTypes.Upload);
        Check(descCheck.Meta != null, "登记用于工具输出检查的文件", descCheck.Error);
        if (descCheck.Meta != null)
        {
            var pendingDesc = FileToolSupport.Describe(descCheck.Meta);
            Check(!pendingDesc.Contains("云端已就绪"),
                  "还没传上去的文件，描述里绝不能出现「云端已就绪」", pendingDesc);
            Check(pendingDesc.Contains("云端还没有") || pendingDesc.Contains("正在上传"),
                  "还没传上去的文件，描述里必须写明真实上传状态", pendingDesc);

            FileRepository.RecordUploadResult(descCheck.Meta.Id, true, null);
            Check(FileToolSupport.Describe(descCheck.Meta).Contains("云端已就绪"),
                  "真正传成功之后，描述里才允许出现「云端已就绪」");

            Check(!FileTypes.Label(FileTypes.Upload).Contains("已上传"),
                  "文件类型标签不能叫「已上传」—— 它是分类不是状态，会把人和模型一起带偏");
        }

        // ── ⑧ 重试上限必须留有出口 ──
        // 撞上限后永久卡死、用户改对配置也等不到重传 —— 那是比反复重试更难排查的状态。
        var retryCheck = FileRepository.RegisterText("重试出口检查", "retry-check.txt", FileTypes.Artifact);
        Check(retryCheck.Meta != null, "登记用于重试检查的文件", retryCheck.Error);
        if (retryCheck.Meta != null)
        {
            for (var i = 0; i < UploadQueue.MaxRetry; i++)
                FileRepository.RecordUploadResult(retryCheck.Meta.Id, false, "模拟失败");
            Check(FileRepository.StuckUploadCount(UploadQueue.MaxRetry) >= 1,
                  "连续失败达到上限后必须被计为「卡住」（界面靠它把问题暴露给用户）");

            FileRepository.ResetFailedUploads();
            Check(FileRepository.StuckUploadCount(UploadQueue.MaxRetry) == 0,
                  "重置后必须不再卡住（配置修好后要能自动重传）");
        }

        Console.WriteLine();
    }

    /// <summary>该路径是否被网盘沙箱守卫拒绝。</summary>
    private static bool ThrowsSandbox(Action act)
    {
        try { act(); return false; }
        catch (BaiduApiException) { return true; }
    }

    /// <summary>集合内容是否与给定元素完全一致（与顺序无关）。</summary>
    private static bool SetsEqual(HashSet<int> set, params int[] expected)
        => set.Count == expected.Length && expected.All(set.Contains);

    // ── 单测（验收 F：确定性 ID / 加密往返 / 恢复码 / 强度校验） ──

    // ══════════════════ 附件到期清理 / 彻底删除（2026-09-16 重构）══════════════════
    //
    // 这一组守护的是**顺序**：本地状态推进必须无条件，云端删除只影响标注。
    // 旧实现把两者绑在一起（云端删成功 → 才打墓碑 + 贴淘汰标记），删除接口一坏就整条链卡死：
    // 云端没删、本地没清、还每 30 分钟整批重试。回归时最该盯的就是这个顺序被"顺手优化"回去。

    /// <summary>造一批元数据 + 空账本并重载，让每条检查点从已知状态开始。</summary>
    private static void SeedFileMeta(params (string Id, string NetPath, string Type, DateTime? ExpireAt, bool Deleted)[] items)
    {
        var list = items.Select(i => (object)new Dictionary<string, object?>
        {
            ["Id"] = i.Id,
            ["Name"] = i.Id + ".txt",
            ["NetPath"] = i.NetPath,
            ["Size"] = 1024L,
            ["Md5"] = "chk",
            ["Type"] = i.Type,
            ["Tags"] = Array.Empty<string>(),
            ["CreatedAt"] = DateTime.Now.AddDays(-40),
            ["UpdatedAt"] = DateTime.Now.AddDays(-40),
            ["ExpireAt"] = i.ExpireAt,
            ["Deleted"] = i.Deleted,
            ["CloudState"] = CloudStates.Ok,
        }).ToArray();

        File.WriteAllText(FileRepository.MetaPath,
            JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.WriteAllText(FileRepository.LedgerPath, "[]", new UTF8Encoding(false));
        FileRepository.Reload();
    }

    /// <summary>往账本里放一条本机副本记录（UploadState 必须是 uploaded，否则淘汰器保护它）。</summary>
    private static void SeedLedger(string id, string localPath)
    {
        var entry = new object[]
        {
            new Dictionary<string, object?>
            {
                ["Id"] = id,
                ["LocalPath"] = localPath,
                ["CachedAt"] = DateTime.Now.AddDays(-40),
                ["LastAccess"] = DateTime.Now.AddDays(-40),
                ["Origin"] = CacheOrigins.Local,
                ["UploadState"] = UploadStates.Uploaded,
                ["UploadRetry"] = 0,
                ["PendingEvict"] = false,
            },
        };
        File.WriteAllText(FileRepository.LedgerPath,
            JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        FileRepository.Reload();
    }

    private static async Task TestAttachmentCleanup()
    {
        Console.WriteLine("[附件到期清理] 本地先推进 / 云端尽力而为 / 失败退避 / 手动重试 / 彻底删除");

        // ── 路径规则：附件按月分子目录（人工兜底路径的前提：能整目录删）──
        Check(FileRepository.AttachmentMonthDir(new DateTime(2026, 9, 16)).EndsWith("/attachments/2026-09", StringComparison.Ordinal),
            "附件云端路径按月分目录（…/attachments/2026-09）");
        Check(FileRepository.NetDirOf("/apps/A/files/a.md") == "/apps/A/files",
            "NetDirOf 取父目录（建目录/取回统一走它，避免按类型现拼拼错位置）");

        var goodId = "chk-good";
        var badId = "chk-bad";
        var goodNet = FileRepository.NetAttachmentsDir + "/2026-09/chk-good.txt";
        var badNet = FileRepository.NetAttachmentsDir + "/2026-09/chk-bad.txt";
        var localGood = Path.Combine(_sandbox, "files", "chk-good.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(localGood)!);
        File.WriteAllText(localGood, "本地副本");

        SeedFileMeta(
            (goodId, goodNet, FileTypes.Attachment, DateTime.Now.AddDays(-1), false),
            (badId, badNet, FileTypes.Attachment, DateTime.Now.AddDays(-1), false));
        SeedLedger(goodId, localGood);

        var cloud = new FakeCloud();
        cloud.FailFor.Add(badNet);          // 这条必然删不掉
        FileRepository.Cloud = cloud;

        // ── 第一轮：一条能删、一条删不掉 ──
        var done = await AttachmentExpiryService.RunAsync();
        var good = FileRepository.FindMetadata(goodId)!;
        var bad = FileRepository.FindMetadata(badId)!;

        Check(done == 1, "云端能删的那条清理成功 1 条", $"实际 {done}");
        Check(cloud.Deleted.Contains(goodNet), "删除请求确实打到了那一条");
        Check(good.CloudState == CloudStates.Expired, "成功那条标为「已到期清理」", good.CloudState);
        Check(bad.CloudState == CloudStates.CleanupPending, "失败那条标为「待清理」（绝不假装清掉了）", bad.CloudState);
        Check(!good.Deleted && !bad.Deleted, "两条都**没打墓碑** —— 记录留着，用户才看得见它们去哪了");
        Check(FileRepository.FindCache(goodId)?.PendingEvict == true,
            "本地副本已贴「优先淘汰」——本地推进不依赖云端结果");
        Check(FileRepository.CleanupPendingCount() == 1, "待清理计数 = 1（设置页靠它把问题暴露给用户）");

        // ── 第二轮：退避期内不许再撞平台 ──
        var callsBefore = cloud.DeleteCalls;
        var done2 = await AttachmentExpiryService.RunAsync();
        Check(done2 == 0, "第二轮无新增成功（那条已清掉、失败那条在退避）");
        Check(cloud.DeleteCalls == callsBefore, "退避期内**没有**再发删除请求（旧版每轮整批重试就是在这里烧配额）");
        Check(AttachmentExpiryService.IsSkipping(badId), "失败那条确实处于退避状态");

        // ── 还没连网盘时：本地照样推进，云端只标注 ──
        SeedFileMeta((goodId, goodNet, FileTypes.Attachment, DateTime.Now.AddDays(-1), false));
        SeedLedger(goodId, localGood);
        FileRepository.Cloud = new FakeCloud { IsReady = false };
        var doneOffline = await AttachmentExpiryService.RunAsync();
        var offlineMeta = FileRepository.FindMetadata(goodId)!;
        Check(doneOffline == 0, "未连接网盘时不谎报清理成功");
        Check(offlineMeta.CloudState == CloudStates.CleanupPending, "未连接网盘时标为「待清理」，等联网补删");
        Check(FileRepository.FindCache(goodId)?.PendingEvict == true, "未连接网盘时本地副本照样贴淘汰标记（本地不卡在网盘上）");

        // ── 用户手动重试：唯一的"修好环境再试一次"出口 ──
        // 先把状态重置成「只有 badId 待清理、且它删不掉」：上一步的 SeedFileMeta 已经换掉了元数据内容，
        // 不重置就测不到"重试仍失败"这条（检查点自己状态没铺对，最容易伪装成代码 bug）。
        SeedFileMeta((badId, badNet, FileTypes.Attachment, DateTime.Now.AddDays(-1), false));
        FileRepository.MarkCleanupPending(badId);
        FileRepository.Cloud = cloud;
        var (retryDone, retryLeft) = await AttachmentExpiryService.RetryPendingAsync();
        Check(!AttachmentExpiryService.IsSkipping(badId), "手动重试清空了退避（用户改好环境后能立刻再试）");
        Check(retryDone == 0 && retryLeft == 1, "手动重试仍失败时保持「待清理」、不静默丢失", $"成功{retryDone}/剩{retryLeft}");

        // ── 彻底删除：云端失败必须如实交代 ──
        SeedFileMeta((badId, badNet, FileTypes.Upload, null, false));
        FileRepository.Cloud = cloud;
        var (ok, message) = await FileRepository.DeletePermanentlyAsync(badId);
        var afterDelete = FileRepository.FindMetadata(badId)!;
        Check(ok && afterDelete.Deleted, "彻底删除：本机记账成功（墓碑已打）");
        Check(afterDelete.CloudState == CloudStates.CleanupPending, "云端删不掉时标为「待清理」");
        Check(message.Contains("云端删除失败") && message.Contains("还在"),
            "提示必须让用户看清「云端那份还在」，不能只说「已删除」", message);
        Check(message.Contains("重试云端清理"), "提示里给出下一步动作（否则用户只能干瞪眼）");

        // ── 合并：CloudState 变化必须能被识别（否则标注永远同步不出去）──
        var a = new FileMetadata { Id = "x", CloudState = CloudStates.Ok, UpdatedAt = DateTime.Now };
        var b = new FileMetadata { Id = "x", CloudState = CloudStates.CleanupPending, UpdatedAt = DateTime.Now };
        Check(!FileStoreSync.MetadataEquals(new[] { a }, new[] { b }),
            "CloudState 变化被 MetadataEquals 识别（否则清单不会重新上传，他端永远看不到真实状态）");
        Check(FileRepository.CleanupPendingCount() == 1, "待清理计数包含已打墓碑的记录（用户删过但云端没删掉的更要能补删）");

        // 收尾：还原全局状态，别影响后续检查点
        FileRepository.Cloud = null;
        SeedFileMeta();
    }

    /// <summary>内存假云仓库：可指定"哪条删不掉"，并记录真实发过的删除请求。</summary>
    private sealed class FakeCloud : ICloudStorage
    {
        public string NetRoot => "/apps/Test";
        public bool IsReady { get; set; } = true;
        public int DeleteCalls { get; private set; }
        public List<string> Deleted { get; } = new();
        public HashSet<string> FailFor { get; } = new(StringComparer.Ordinal);

        public Task UploadAsync(string localPath, string netPath, IProgress<double>? progress, CancellationToken ct)
            => Task.CompletedTask;

        public Task<bool> DownloadAsync(string netPath, string localPath, IProgress<double>? progress, CancellationToken ct)
            => Task.FromResult(false);

        public Task EnsureDirectoryAsync(string netDir, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteAsync(IEnumerable<string> netPaths, CancellationToken ct)
        {
            foreach (var p in netPaths)
            {
                DeleteCalls++;
                if (FailFor.Contains(p))
                    throw new BaiduApiException(31064, "检查点桩：这条删不掉");
                Deleted.Add(p);
            }
            return Task.CompletedTask;
        }
    }

    private static void TestUnit()
    {
        Console.WriteLine("[单测] 确定性 ID / E2EE / 恢复码");
        var line = "- [2026-08-12 10:00] 你好";
        var id1 = SyncNote.ComputeId(line);
        var id2 = SyncNote.ComputeId(line);
        var id3 = SyncNote.ComputeId("- [2026-08-12 10:01] 你好");
        Check(id1 == id2 && id1 != id3 && id1.Length == 32, "确定性 ID：同行同 ID / 不同行不同 ID");
        Check(id1 != SyncNote.ComputeId("你好"),
            "ID 基于完整原始行（含时间戳前缀，审查修正点）");
        // v4（2026-09-12 行身份改造）：ID 只由行文本决定，不再含相对路径 —— 同一条行在 A 的提醒日文件与
        // B 的创建日文件里必须是同一个身份，否则跨端删除不生效、旧版本行被反复投递回本地。
        // 端到端验收见 G 组（TestLineIdentityAsync），此处只锁"纯函数"性质。
        Check(SyncNote.ComputeId(line) == SyncNote.ComputeId(new string(line.ToCharArray())),
            "ID 为行的纯函数（不受任何外部上下文/路径影响）");

        var salt = CryptoService.GenerateSalt();
        var dek = CryptoService.DeriveKey("MasterPass123", salt);
        var plain = "- [2026-08-12 10:00] 你好 — 来源: 记事本";
        var enc = CryptoService.Encrypt(dek, plain);
        Check(CryptoService.Decrypt(dek, enc) == plain, "AES-GCM 加密往返一致");
        Check(enc != CryptoService.Encrypt(dek, plain), "nonce 随机：两次密文不同");
        var wrongDek = CryptoService.DeriveKey("WrongPass123", salt);
        var threw = false;
        try { CryptoService.Decrypt(wrongDek, enc); }
        catch (System.Security.Cryptography.CryptographicException) { threw = true; }
        Check(threw, "错 DEK 解密抛 CryptographicException");
        Check(CryptoService.DeriveKey("MasterPass123", CryptoService.GenerateSalt()).Length == 32, "盐不同 → 新 DEK 派生正常");

        var code = CryptoService.GenerateRecoveryCode();
        Check(code.Length == 14 &&
              code.All(c => "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789".Contains(c)),
            "恢复码 14 位混合字符（剔除 0/O/1/I/l）");
        var (hash, csalt) = CryptoService.HashRecoveryCode(code);
        Check(CryptoService.VerifyRecoveryCode(code, hash, csalt), "恢复码校验通过");
        Check(!CryptoService.VerifyRecoveryCode(code + "X", hash, csalt), "错误恢复码校验失败");

        Check(CryptoService.IsValidMasterPassword("MasterPass123"), "主密码强度校验通过");
        Check(!CryptoService.IsValidMasterPassword("short"), "弱密码被拒");
        Check(!CryptoService.IsValidMasterPassword("12345678"), "纯数字被拒");
    }

    // ── 桶拆分（验收：打包存储 ≤200 条/桶） ──

    private static async Task TestBucketSplitting()
    {
        Console.WriteLine("[桶拆分] 201 条 → 2 桶（200+1）");
        var root = Path.Combine(Path.GetTempPath(), "fc-sync-bucket-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var server = new TestWebDavServer(Path.Combine(root, "cloud"));
            var provider = new WebDAVProvider(server.BaseUrl, "u", "t");
            var notes = new List<SyncNote>();
            for (int i = 0; i < 201; i++)
            {
                var ts = "2026-08-03T10:00:00Z";   // 同周同天 → 单组 → 按 200 拆
                notes.Add(new SyncNote { Id = i.ToString("x32"), Content = "enc-" + i, UpdatedAt = ts, CreatedAt = ts, DeviceId = "t" });
            }
            var r = await provider.PushAsync(notes, null, CancellationToken.None);
            Check(!string.IsNullOrEmpty(r.NewSince), "PushAsync 返回新游标");
            var buckets = server.ListFiles().Where(f => f.StartsWith("notes-")).ToList();
            Check(buckets.Count == 2, $"201 条 → 2 桶（实际 {buckets.Count}）");
            var total = 0;
            foreach (var b in buckets)
            {
                var bucket = SyncBucket.FromJson(server.ReadFile(b))!;
                total += bucket.Notes.Count;
                Check(bucket.Notes.Count <= 200, $"桶 {b} ≤200 条（实际 {bucket.Notes.Count}）");
            }
            Check(total == 201, $"总条数 201（实际 {total}）");
            Check(server.ListFiles().Contains("sync_meta.json"), "sync_meta.json 已生成（桶清单+游标）");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ── 双设备模拟（验收 C 全流程 + 回声 + D 密钥重置 + E 自愈） ──

    private static async Task TestDualDevice()
    {
        Console.WriteLine("[双设备模拟] 验收 C（双向收敛/删除/软删）+ D（密钥重置）+ E（自愈）");
        var root = Path.Combine(Path.GetTempPath(), "fc-sync-dual-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var server = new TestWebDavServer(Path.Combine(root, "cloud"));
            var dirA = Path.Combine(root, "A"); Directory.CreateDirectory(dirA);
            var dirB = Path.Combine(root, "B"); Directory.CreateDirectory(dirB);
            var sa = new AppSettings { NotesPath = dirA };
            var sb = new AppSettings { NotesPath = dirB };
            // A/B 各自独立的删除记录（2026-09-11）：否则两个 NoteService 实例共享同一份 deleted.json，
            // A 的删除会直接改变 B 的可见结果，「跨设备删除传播」无法被真实检验。
            var na = new NoteService(sa, Path.Combine(dirA, "deleted.json"));
            var nb = new NoteService(sb, Path.Combine(dirB, "deleted.json"));
            var pa = new WebDAVProvider(server.BaseUrl, "u", "t");
            var pb = new WebDAVProvider(server.BaseUrl, "u", "t");
            var ea = new SyncEngine(sa, na, pa, Backoff);
            var eb = new SyncEngine(sb, nb, pb, Backoff);

            // A 首配（空库）：生成盐 → 上传 sync_meta.json
            await ea.SetTokenKeyAsync("MasterPass123");
            Check((await ea.SyncNowAsync()).Success, "C-1 A 首次同步");
            var meta = server.ReadFile("sync_meta.json");
            Check(meta.Contains("saltBase64") && meta.Contains("\"saltBase64\":\"") && meta.Length > 40, "C-1 云端 sync_meta.json 含盐（明文，跨设备一致）");

            // A 写 3 条
            na.SaveNote("A 笔记 1");
            na.SaveNote("A 笔记 2");
            na.SaveNote("A 笔记 3");
            Check((await ea.SyncNowAsync()).Success, "A 推送 3 条");
            var buckets = server.ListFiles().Where(f => f.StartsWith("notes-")).ToList();
            Check(buckets.Count >= 1, "C-1 云端出现桶文件");
            Check(buckets.All(f => !server.ReadFile(f).Contains("A 笔记")), "B/E2EE 云端 content 为密文（无明文）");
            Check(na.ReadAllLines().Count == 3 && File.Exists(Path.Combine(dirA, Path.GetFileName(na.ReadAllLines()[0].RelativePath))),
                "B 本地 MD 保持明文可读");

            // B 首配（同一主密码 + 同一 WebDAV）：拉云端盐派生同一 DEK
            await eb.SetTokenKeyAsync("MasterPass123");
            Check((await eb.SyncNowAsync()).Success, "C-2 B 首次同步");
            Check(nb.ReadAllLines().Count == 3, "C-2 B 拉到 A 的 3 条");
            Check(nb.ReadAllLines().All(x => x.Line.Contains("A 笔记")), "C-2 B 内容一致");

            // A 新增 1 条 → B 拉取
            na.SaveNote("A 笔记 4");
            await ea.SyncNowAsync();
            await eb.SyncNowAsync();
            Check(nb.ReadAllLines().Count == 4, "C-3 A 新增 → B 增量收敛");

            // B 编辑（追加【编辑】行）→ A 拉取
            var bEntry = nb.ReadAllLines().First(x => x.Line.Contains("A 笔记 1"));
            Check(nb.AppendEdit(bEntry.Entry, "B 端编辑内容"), "B 编辑成功（追加行）");
            await eb.SyncNowAsync();
            await ea.SyncNowAsync();
            Check(na.ReadAllLines().Any(x => x.Line.Contains("【编辑】")), "C-3 B 编辑 → A 拉取到编辑行");

            // A 删除 1 条 → 删除即同步（SyncEngine.OnLinesDeleted）：立即生成墓碑并传播到他端
            // 注：原断言期望「未清空回收站前不上云」属 2026-08-27 之前的旧设计；
            //     现行为为「删除即同步」，2026-09-11 实测确认并经用户拍板改写。
            var aEntry = na.ReadAllLines().First(x => x.Line.Contains("A 笔记 2"));
            Check(na.DeleteNote(aEntry.Entry), "A 删除成功（进回收站，未清空）");
            Check(na.RecycleBin.List().Count == 1, "A 回收站有 1 条");
            await ea.SyncNowAsync();
            await eb.SyncNowAsync();
            var bCountC4 = nb.ReadAllLines().Count;
            var bBinC4 = nb.RecycleBin.List().Count;
            var cloudTombC4 = server.ListFiles().Where(f => f.StartsWith("notes-"))
                .Select(f => SyncBucket.FromJson(server.ReadFile(f))!)
                .SelectMany(x => x.Notes).Count(n => n.Deleted);
            Console.WriteLine($"  [C-4] A行={na.ReadAllLines().Count} B行={bCountC4} A回收站={na.RecycleBin.List().Count} B回收站={bBinC4} 云端墓碑={cloudTombC4}");
            Check(cloudTombC4 == 1, "C-4 云端生成软删墓碑（删除即同步）");
            Check(bCountC4 == 4, "C-4 他端同步后少 1 行");
            Check(bBinC4 == 1, "C-4 他端该行进本地回收站（可恢复 —— 防误删保证）");

            // A 清空回收站 → 彻底删除传播 → 他端清除本地行与回收站记录（不再可恢复）
            // 依据 SyncEngine.QueueRecycleBinPurge 注释：「Purged=true 表示彻底删除：他端删除本地行并清除回收站记录」
            var purged = na.RecycleBin.PurgeAll();
            Check(purged.Count == 1, "清空回收站返回记录");
            ea.QueueRecycleBinPurge(purged);
            await ea.SyncNowAsync();
            await eb.SyncNowAsync();
            var bCountC5 = nb.ReadAllLines().Count;
            var bBinC5 = nb.RecycleBin.List().Count;
            var cloudTombC5 = server.ListFiles().Where(f => f.StartsWith("notes-"))
                .Select(f => SyncBucket.FromJson(server.ReadFile(f))!)
                .SelectMany(x => x.Notes).Count(n => n.Deleted);
            Console.WriteLine($"  [C-5] A行={na.ReadAllLines().Count} B行={bCountC5} B回收站={bBinC5} 云端墓碑={cloudTombC5}");
            Check(bCountC5 == 4, "C-5 彻底删除后他端行数不变（该行已不存在）");
            Check(bBinC5 == 0, "C-5 他端回收站记录被清除（彻底删除，不再可恢复）");
            Check(cloudTombC5 == 1, "C-5 云端墓碑保留（Deleted=true）");

            // 回声识别：连续 3 轮双向同步后云端桶无变化（无死循环/无重复推送）
            var before = server.ListFiles().Where(f => f.StartsWith("notes-"))
                .ToDictionary(f => f, f => server.ReadFile(f));
            for (int i = 0; i < 3; i++) { await ea.SyncNowAsync(); await eb.SyncNowAsync(); }
            var after = server.ListFiles().Where(f => f.StartsWith("notes-"))
                .ToDictionary(f => f, f => server.ReadFile(f));
            Check(before.Count == after.Count && before.All(kv =>
                after.TryGetValue(kv.Key, out var v) && v == kv.Value), "回声识别：连续 3 轮云端桶无变化");

            // ── D 换授权码：云端密文自动重写 + 旧码设备可见提示（2026-09-11 新增行为）──
            // 背景：此前换码只改本地钥匙、云端密文不重写 → 换码后新钥匙反而读不到自己的数据
            //（旧码却仍能读）。经用户拍板改为：检测到 DEK 变化即自动全量重传。
            await ea.SetTokenKeyAsync("NewToken456");
            Check(sa.Sync.LastSyncResult.Contains("已全量重传"), "D A 换码后自动全量重传（云端改用新钥匙加密）");
            // 注：此处不断言「游标数值是否变化」—— 游标 = 云端最新上传时刻，重传后是否前移取决于
            // 时间戳比较（曾出现不稳定）。「云端确实换了钥匙」由下一组「B 用旧码解不开」间接验证。

            var rBold = await eb.SyncNowAsync();
            Check(rBold.Success, "D B 用旧码同步不崩溃（跳过解不开的条目）");
            Check(sb.Sync.LastSyncResult.Contains("解密失败"), "D B 用旧码解不开新密文，且提示可见（非静默）");
            Check(nb.ReadAllLines().Count == 4, "D B 本地数据完好（本地为明文事实源，不受密钥不一致影响）");

            await eb.SetTokenKeyAsync("NewToken456");
            Check(sb.Sync.LastSyncResult.Contains("已全量重传"), "D B 换新码后同样自动重传");
            var rB2 = await eb.SyncNowAsync();
            Check(rB2.Success, "D B 换新码后同步成功");
            Check(!sb.Sync.LastSyncResult.Contains("解密失败"), "D 两端密钥对齐后不再有解密失败");

            // E 自愈：重置同步状态（清空云端桶 + 全量重传）→ 双端仍一致
            Check((await ea.ResetSyncAsync()).Success, "E 重置同步状态（清空云端+全量重传）");
            await eb.SyncNowAsync();
            Check(nb.ReadAllLines().Count == 4, "E 重置后 B 数据完整一致");

            // 游标丢失自愈（拉取侧）：清 LastCursor → 全量对账无重复
            sa.Sync.LastCursor = "";
            sa.Save();
            Check((await ea.SyncNowAsync()).Success, "E 游标丢失后重新同步");
            Check(na.ReadAllLines().Count == 4, "E 自愈后 A 无重复无丢失");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ── 行身份改造验收（2026-09-12）：未到期待办的定位 / 删除 / 跨端一致 / 不重复落地 ──
    //
    // 复现并锁住用户 2026-09-11 实测报的两个 bug：
    //   ①本机创建的"未到期待办"删不掉（弹「删除失败：未在笔记文件中找到该条目，可能已被外部修改」）——
    //     带提醒的待办写入时归到**提醒日**文件，而删除查找按**创建日**猜文件 → 文件名对不上 → 恒失败；
    //   ②跨端删除不生效（B 删了 A 上还在）：ID 曾含相对路径，同一行在 A 的提醒日文件、在 B 的创建日文件
    //     → 身份分裂 → 墓碑对不上；附带旧版本行被反复投递（已办待办反复复活、副本每轮 +1）。

    private static async Task TestLineIdentityAsync()
    {
        Console.WriteLine("[行身份] 未到期待办：定位 / 删除 / 跨端一致（2026-09-12 改造验收）");
        var root = Path.Combine(Path.GetTempPath(), "fc-line-identity-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var server = new TestWebDavServer(Path.Combine(root, "cloud"));
            var dirA = Path.Combine(root, "A"); Directory.CreateDirectory(dirA);
            var dirB = Path.Combine(root, "B"); Directory.CreateDirectory(dirB);
            var sa = new AppSettings { NotesPath = dirA };
            var sb = new AppSettings { NotesPath = dirB };
            var na = new NoteService(sa, Path.Combine(dirA, "deleted.json"));
            var nb = new NoteService(sb, Path.Combine(dirB, "deleted.json"));
            var ea = new SyncEngine(sa, na, new WebDAVProvider(server.BaseUrl, "u", "t"), Backoff);
            var eb = new SyncEngine(sb, nb, new WebDAVProvider(server.BaseUrl, "u", "t"), Backoff);
            await ea.SetTokenKeyAsync("MasterPass123");
            Check((await ea.SyncNowAsync()).Success, "G-0 A 首次同步");

            var due = DateTime.Today.AddDays(3).Date.AddHours(9);   // 提醒日 ≠ 创建日 → 归"未到期"
            var dueFile = $"灵感_{due:yyyy-MM-dd}.md";

            // G-1 写入归类：带提醒的待办写进提醒日文件（不是创建日文件）
            var created = na.SaveNote("G1 真建未到期", null, NoteType.Todo, due);
            Check(created != null && File.Exists(Path.Combine(dirA, dueFile)),
                "G-1 带提醒待办写入提醒日文件 灵感_{DueTime}.md");

            // G-2 创建端删除（bug ①）：改造前 FindEntryFile 只按创建日找文件 → 找不到 → 必失败
            var g1 = na.LoadNotes(due.Date).FirstOrDefault(e => e.Content.Contains("G1 真建未到期"));
            Check(g1 != null && na.DeleteNote(g1),
                "G-2 创建端能删掉自己的未到期待办（改造前必红：报『未在笔记文件中找到该条目』）");

            // G-3 手写一条"行时间戳早于现在"的未到期待办进提醒日文件（模拟历史数据）。
            //     行时间戳必须早于删除时刻，否则会命中"删除后重录 → 本机 wins"的保护分支（那是另一条既有规则）。
            var oldTs = DateTime.Now.AddMinutes(-5);
            var handLine = $"- [{oldTs:yyyy-MM-dd HH:mm}] 【待办】G2 手写未到期 (提醒: {due:yyyy-MM-dd HH:mm:ss})";
            File.AppendAllText(Path.Combine(dirA, dueFile), handLine + Environment.NewLine, Encoding.UTF8);
            await ea.SyncNowAsync();
            // B 首配（须在 A 首轮同步之后：云端已有盐，两端才能派生同一 DEK）
            await eb.SetTokenKeyAsync("MasterPass123");
            await eb.SyncNowAsync();

            var bCopies = nb.ReadAllLines().Where(x => x.Line.Contains("G2 手写未到期")).ToList();
            Check(bCopies.Count == 1, "G-3 B 端只落地一份（改造前：两端文件不同 → ID 不同 → 每轮重复落地）");
            Check(bCopies.Count > 0 && Path.GetFileName(bCopies[0].RelativePath) != dueFile,
                "G-3 B 端按创建日落地（与 A 的提醒日文件不同 —— 位置分裂正是改造前 ID 分叉的来源）");
            Check(nb.LoadNotes(due.Date).Any(e => e.Content.Contains("G2 手写未到期")),
                "G-4 B 端在提醒日面板能看到它（显示与行的物理位置解耦）");

            // G-4b 再同步两轮：确认不再重复落地（改造前每轮 +1 份副本）
            await ea.SyncNowAsync();
            await eb.SyncNowAsync();
            Check(nb.ReadAllLines().Count(x => x.Line.Contains("G2 手写未到期")) == 1,
                "G-4 再同步两轮仍只一份（改造前每轮副本 +1）");

            // G-5 跨端删除（bug ②）：B 删掉 A 创建的未到期待办 → A 端必须真的没了
            var bLine = nb.ReadAllLines().First(x => x.Line.Contains("G2 手写未到期"));
            Check(nb.DeleteNote(bLine.Entry), "G-5 B 端删除成功");
            await eb.SyncNowAsync();
            await ea.SyncNowAsync();
            Check(!na.ReadAllLines().Any(x => x.Line.Contains("G2 手写未到期")),
                "G-5 B 的删除传播到 A（改造前墓碑 ID 与 A 的行 ID 不同 → A 上仍在）");
        }
        finally
        {
            if (_failed == 0) { try { Directory.Delete(root, true); } catch { } }
            else Console.WriteLine($"  [有失败] 行身份测试沙箱已保留：{root}");
        }
    }

    // ── 首配子目录缺失（08-15 修复回归：ListFilesAsync 接 404 → MKCOL → 重试） ──

    private static async Task TestFirstSyncDirMissing()
    {
        Console.WriteLine("[首配目录缺失] 云端无子目录：PROPFIND 404 → 自动 MKCOL → 重试成功");
        var root = Path.Combine(Path.GetTempPath(), "fc-sync-mkcol-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var server = new TestWebDavServer(Path.Combine(root, "cloud"), createRoot: false);
            var dirA = Path.Combine(root, "A"); Directory.CreateDirectory(dirA);
            var sa = new AppSettings { NotesPath = dirA };
            var na = new NoteService(sa);
            var pa = new WebDAVProvider(server.BaseUrl, "u", "t");
            var ea = new SyncEngine(sa, na, pa, Backoff);

            // 首配：云端无目录、无 sync_meta → 生成盐 → 同步（Pull 侧应自动建目录）
            await ea.SetTokenKeyAsync("MasterPass123");
            var r = await ea.SyncNowAsync();
            Check(r.Success, "首配目录缺失：SyncNow 自动建目录并成功" + (r.Success ? "" : " ← " + r.Error));
            Check(server.ListFiles().Contains("sync_meta.json"), "首配目录缺失：sync_meta.json 已上传（MKCOL 生效）");

            // 换新目录再次首配（模拟换账号/新设备）：同样自动建
            using var server2 = new TestWebDavServer(Path.Combine(root, "cloud2"), createRoot: false);
            var pa2 = new WebDAVProvider(server2.BaseUrl, "u", "t");
            var ea2 = new SyncEngine(sa, na, pa2, Backoff);
            await ea2.SetTokenKeyAsync("MasterPass123");
            var r2 = await ea2.SyncNowAsync();
            Check(r2.Success, "二配新目录：再次自动建目录并成功" + (r2.Success ? "" : " ← " + r2.Error));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ── 断网 + 失败重试（验收 C-6/C-7） ──

    private static async Task TestNetworkFailure()
    {
        Console.WriteLine("[断网/重试] 验收 C-6（本地可用）/ C-7（3 次失败停止 + 手动恢复）");
        var root = Path.Combine(Path.GetTempPath(), "fc-sync-net-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var server = new TestWebDavServer(Path.Combine(root, "cloud"));
            var dirA = Path.Combine(root, "A"); Directory.CreateDirectory(dirA);
            var dirB = Path.Combine(root, "B"); Directory.CreateDirectory(dirB);
            var sa = new AppSettings { NotesPath = dirA };
            var sb = new AppSettings { NotesPath = dirB };
            var na = new NoteService(sa);
            var nb = new NoteService(sb);
            var pa = new WebDAVProvider(server.BaseUrl, "u", "t");
            var ea = new SyncEngine(sa, na, pa, Backoff);
            await ea.SetTokenKeyAsync("MasterPass123");
            na.SaveNote("断网测试行");
            await ea.SyncNowAsync();

            // C-6 断网（改错 URL）：无法解锁（不生成冲突盐）→ 本地功能正常 → 恢复后补齐

            // ① 真实链路只验一次：底层 HTTP 失败必须被包装成 SyncProviderException（渠道错误），
            //    而不是裸抛、更不是静默返回 null —— 这是全项目唯一一条用真实网络失败覆盖该转换的断言。
            //    成本：本机实测这一跳约 2.2 秒（而连「有人监听」的端口只要 5 ms），所以只留这一处，
            //    其余断网场景走下面的桩。（2026-09-17）
            var realDown = new WebDAVProvider("http://127.0.0.1:19999/", "u", "t");
            var realWrapped = false;
            var realDiag = "(未抛异常：静默返回了正常结果——最坏情况，等于把失败伪装成成功)";
            try { await realDown.GetMetaAsync(CancellationToken.None); }
            catch (SyncProviderException ex) { realWrapped = true; realDiag = $"SyncProviderException(StatusCode={ex.StatusCode}, IsNetwork={ex.IsNetwork}) {ex.Message}"; }
            catch (Exception ex) { realDiag = "裸异常 " + ex.GetType().Name + ": " + ex.Message; }
            // 刻意不断言「必须 StatusCode=0」：具体状态码取决于环境 ——
            // 本机实测这一跳拿到的是 502（Bad Gateway），而 TCP 层直连同一端口是「积极拒绝」，
            // 成因指向系统层网络过滤（详见 .workbuddy 9-17 日志），远程真实断网才会得到网络错误 0，
            // 那条路径由上面的 FailingSyncProvider 桩覆盖。
            // 真正要守的契约：底层失败必须被包装成「可区分的渠道错误」——不许裸抛，更不许静默返回 null。
            Check(realWrapped, "真实 HTTP 层失败必须被包装成渠道错误（不得裸抛，更不得静默返回 null）", "实际=" + realDiag);

            // ② 其余断网场景用秒失败的测试桩：桩抛的是同一个 SyncProviderException(0)，
            //    引擎看到的东西与真实 TCP 失败一致，下面几条上层断言一条没改。
            var ebBad = new SyncEngine(sb, nb, new FailingSyncProvider(), Backoff);
            var unlockThrew = false;
            try { await ebBad.SetTokenKeyAsync("MasterPass123"); }
            catch (InvalidOperationException) { unlockThrew = true; }
            Check(unlockThrew, "C-6 断网时无法解锁（禁止生成冲突盐）");
            nb.SaveNote("断网期间本地新增");   // 本地 100% 可用
            var rBad = await ebBad.SyncNowAsync();
            Check(!rBad.Success && !string.IsNullOrEmpty(rBad.Error), "C-6 断网同步失败（不崩）");
            Check(nb.ReadAllLines().Any(x => x.Line.Contains("断网期间")), "C-6 断网时本地功能正常");

            // 恢复联网（正确 URL）→ 自动补齐
            // 零退避注入：下面 C-7 用 503 触发限流，而 WebDAVProvider 对 503/429 会干等 5s+15s（生产默认值）。
            // 重试次数不变（同为 2 次），只是不等待 —— 本机实测这段独占断网组约 20 秒。
            var ebOk = new SyncEngine(sb, nb, new WebDAVProvider(server.BaseUrl, "u", "t", ThrottleBackoff), Backoff);
            await ebOk.SetTokenKeyAsync("MasterPass123");
            var rOk = await ebOk.SyncNowAsync();
            Check(rOk.Success, "C-6 恢复后同步成功" + (rOk.Success ? "" : " ← " + rOk.Error));
            await ea.SyncNowAsync();
            Check(na.ReadAllLines().Any(x => x.Line.Contains("断网期间")), "C-6 断网期间的新笔记已同步到 A");

            // C-7 限流 503 连续 3 次失败 → 停止自动重试 + 手动恢复
            server.ForceStatus = 503;
            var rRetry = await ebOk.SyncNowAsync(auto: true);
            Check(!rRetry.Success, "C-7 连续 3 次失败后停止自动重试");
            Check(sb.Sync.LastSyncResult.StartsWith("失败"), "C-7 UI 显示失败原因");
            server.ForceStatus = 0;
            var rManual = await ebOk.SyncNowAsync();
            Check(rManual.Success, "C-7 手动『立即同步』恢复" + (rManual.Success ? "" : " ← " + rManual.Error));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ── 游标保护验证（2026-09-11，用户指定先实测、不臆断）──
    // 疑点：PullFlowAsync 在解密失败时刻意不推进游标（2026-09-09 修复，为保住「密钥对齐后重拉」），
    //       但 PushFlowAsync 会以 PushAsync 返回的 NewSince 推进游标，且 Push 在 Pull 之后执行
    //       → 可能把 Pull 的保护覆盖掉。
    // 实验：往云端注入一条「用别的钥匙加密的行」（模拟他端设备以不同密钥推送的数据），
    //       本机同步时该条解密失败，观察游标是否被推进。推进 = 保护失效。
    private static async Task TestCursorProtectionAsync()
    {
        Console.WriteLine("[游标保护] 解密失败时游标不该推进（否则未拉到的行会被增量过滤永久漏掉）");
        var root = Path.Combine(Path.GetTempPath(), "fc-sync-cursor-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var server = new TestWebDavServer(Path.Combine(root, "cloud"));
            var dirA = Path.Combine(root, "A"); Directory.CreateDirectory(dirA);
            var sa = new AppSettings { NotesPath = dirA };
            var na = new NoteService(sa, Path.Combine(dirA, "deleted.json"));
            var pa = new WebDAVProvider(server.BaseUrl, "u", "t");
            var ea = new SyncEngine(sa, na, pa, Backoff);

            await ea.SetTokenKeyAsync("CursorTestCode1");
            na.SaveNote("本机基线行");
            Check((await ea.SyncNowAsync()).Success, "游标实验：基线同步成功");
            var cursorBefore = sa.Sync.LastCursor;

            // 注入一条用「完全不同的钥匙」加密的行（本机必然解不开）
            var foreignDek = CryptoService.DeriveKey(
                "ACompletelyDifferentCode", Convert.FromBase64String(sa.Sync.E2eeSalt));
            var nowIso = NoteService.ToUtcIsoString(DateTime.Now);
            var cloudAll = await pa.FullAsync(CancellationToken.None);
            cloudAll.Add(new SyncNote
            {
                Id = new string('f', 32),
                Content = CryptoService.Encrypt(foreignDek, "- [2026-09-11 12:00] 外来的解不开的行"),
                CreatedAt = nowIso,
                UpdatedAt = nowIso,
                UploadedAt = nowIso,
                DeviceId = "another-device",
            });
            await pa.PushAsync(cloudAll, null, CancellationToken.None);

            // 关键条件 1：同时制造「本地有新增待推送」——这才是保护真正受考验的组合。
            // 若只注入外来行而无本地新增，PushAsync 返回的游标与本机相同，游标天然不变，测不出问题。
            // 关键条件 2：先等待 >1 秒，使新增行的上传时刻严格大于注入行的 —— 否则二者可能落在同一秒，
            //           游标值相等会被误读成「保护有效」（时间精度干扰）。
            await Task.Delay(1100);
            na.SaveNote("本机新增行（用于考验游标保护）");

            await ea.SyncNowAsync();
            var cursorAfter = sa.Sync.LastCursor;
            var advanced = cursorBefore != cursorAfter;
            Console.WriteLine($"  [游标实验] 同步结果=\"{sa.Sync.LastSyncResult}\"");
            Console.WriteLine($"  [游标实验] 游标 before={(cursorBefore ?? "(空)")} after={(cursorAfter ?? "(空)")} 是否推进={advanced}");

            Check(sa.Sync.LastSyncResult.Contains("解密失败"), "游标实验：解密失败被正确报告给用户");
            Check(!advanced, "游标实验：解密失败时游标必须不推进（否则保护失效）");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // 假云盘地址改由 TestWebDavServer 实例提供（端口由系统分配），不再使用固定常量（2026-09-11 修复端口冲突）。
    // ══════════════ AI 附件检查点（2026-09-14：问答输入框支持图片与文档） ══════════════
    // 为什么值得自动化：①会话 JSON 一旦嵌进 base64，云同步链路会被拖垮，而这个退化肉眼看不见
    // ②孤儿附件清理做错了会静默吃掉用户文件 ③"压缩到档位长边"是成本契约，跑偏了就是悄悄多花钱。

    private static void TestChatAttachments()
    {
        Console.WriteLine("[H] AI 附件（格式判定 / 抽文本 / 压缩档位 / 会话引用 / 孤儿清理）");

        // H1 格式判定：2026-09-17 起 PDF / xlsx 纳入支持（走文档抽文本，不要求模型有视觉能力）；
        // 压缩包等二进制仍然一律拒绝
        Check(ChatAttachmentService.IsSupported("a.png") && ChatAttachmentService.IsSupported("a.docx")
              && ChatAttachmentService.IsSupported("b.py") && ChatAttachmentService.IsSupported("c.pdf")
              && ChatAttachmentService.IsSupported("d.xlsx") && !ChatAttachmentService.IsSupported("d.zip")
              && !ChatAttachmentService.IsSupported("e.exe"),
              "H1 格式判定：图片/文档/代码/PDF/Excel 支持；压缩包/可执行文件拒绝");

        // H2 Markdown 抽正文
        var mdPath = Path.Combine(_sandbox, "sample.md");
        File.WriteAllText(mdPath, "# 标题\n第一行正文\n第二行正文", new UTF8Encoding(false));
        var (mdAtt, mdErr) = ChatAttachmentService.CreateFromFileAsync(mdPath, 1).GetAwaiter().GetResult();
        Check(mdAtt?.ExtractedText?.Contains("第二行正文") == true && mdAtt.Kind == ChatAttachmentKind.Document,
              $"H2 md 抽正文成功（错误：{mdErr ?? "无"}）");

        // H3 docx 抽正文（零依赖解 zip）
        var docxPath = Path.Combine(_sandbox, "sample.docx");
        CreateMinimalDocx(docxPath, "Word 里的段落文字");
        var (docxAtt, docxErr) = ChatAttachmentService.CreateFromFileAsync(docxPath, 1).GetAwaiter().GetResult();
        Check(docxAtt?.ExtractedText?.Contains("Word 里的段落文字") == true,
              $"H3 docx 抽正文成功（错误：{docxErr ?? "无"}）");

        // H3b PDF 附件抽正文（2026-09-17：以前这里是被明确拒绝的）
        var pdfPath = Path.Combine(_sandbox, "sample.pdf");
        BuildTestPdf(pdfPath, "PDF attachment extraction works");
        var (pdfAtt, pdfErr) = ChatAttachmentService.CreateFromFileAsync(pdfPath, 1).GetAwaiter().GetResult();
        Check(pdfAtt?.ExtractedText?.Contains("PDF attachment extraction works") == true
              && pdfAtt.Kind == ChatAttachmentKind.Document,
              $"H3b PDF 附件能抽出文字并发给模型（错误：{pdfErr ?? "无"}）");

        // H3c 扫描件 PDF：必须明确报错，绝不能产出一个"空文档"让模型自己去编内容
        var scanPath = Path.Combine(_sandbox, "scan.pdf");
        BuildTestPdf(scanPath, null);
        var (scanAtt, scanErr) = ChatAttachmentService.CreateFromFileAsync(scanPath, 1).GetAwaiter().GetResult();
        Check(scanAtt == null && !string.IsNullOrEmpty(scanErr) && scanErr!.Contains("扫描件"),
              $"H3c 扫描件 PDF 明确拒绝并说明原因（实际：{scanErr ?? "却成功了"}）");

        // H4 非 UTF-8 文本要明确报错，不能把乱码喂给模型
        var gbkPath = Path.Combine(_sandbox, "gbk.txt");
        File.WriteAllBytes(gbkPath, new byte[] { 0xD6, 0xD0, 0xCE, 0xC4, 0xB2, 0xE2, 0xCA, 0xD4 });   // GBK「中文测试」
        var (gbkAtt, gbkErr) = ChatAttachmentService.CreateFromFileAsync(gbkPath, 1).GetAwaiter().GetResult();
        Check(gbkAtt == null && !string.IsNullOrEmpty(gbkErr),
              $"H4 非 UTF-8 文本明确拒绝并给出原因（实际：{gbkErr ?? "却成功了"}）");

        // H5 图片压缩到档位长边（省流档 = 768）
        var bigPng = Path.Combine(_sandbox, "big.png");
        CreateTestPng(bigPng, 3000, 2000);
        var (imgAtt, imgErr) = ChatAttachmentService.CreateFromFileAsync(bigPng, 0).GetAwaiter().GetResult();
        var imgLongest = imgAtt == null ? 0 : Math.Max(imgAtt.PixelWidth, imgAtt.PixelHeight);
        Check(imgLongest == 768, $"H5 省流档把 3000×2000 压到长边 768（实际 {imgLongest}，错误：{imgErr ?? "无"}）");

        // H6 标准档长边 1568
        var (stdAtt, _) = ChatAttachmentService.CreateFromFileAsync(bigPng, 1).GetAwaiter().GetResult();
        var stdLongest = stdAtt == null ? 0 : Math.Max(stdAtt.PixelWidth, stdAtt.PixelHeight);
        Check(stdLongest == 1568, $"H6 标准档长边 1568（实际 {stdLongest}）");

        // H7 上行形态是 base64 data URL
        var dataUrl = imgAtt == null ? null : ChatAttachmentService.BuildImageDataUrl(imgAtt);
        Check(dataUrl?.StartsWith("data:image/jpeg;base64,") == true,
              "H7 上行形态为 base64 data URL（jpeg）");

        // H8 ★核心契约★ 会话 JSON 只存引用，绝不嵌 base64
        //   判据：一张 3000×2000 的图若被嵌进去，base64 后必然 >100KB；只存引用则整个会话文件才几 KB
        var session = new ChatSessionService(ExplainMode.Ask);
        var attachments = new List<ChatAttachment> { imgAtt! };
        attachments[0].InsertOffset = 2;   // 模拟"先打两个字，再插图"
        session.AddUser("看这张图", attachments);
        session.AddAssistant("收到");
        session.Save();

        var savedFile = Path.Combine(_sandbox, "chat_history", session.SessionId + ".json");
        var savedText = File.Exists(savedFile) ? File.ReadAllText(savedFile) : "";
        Check(savedText.Length > 0 && savedText.Length < 20000 && !savedText.Contains("base64"),
              $"H8 会话 JSON 只存附件引用、不嵌 base64（实际 {savedText.Length} 字符）");

        // H9 会话往返：附件引用与混排偏移必须完整保留
        var reloaded = ChatSessionService.Load(savedFile);
        var reUser = reloaded?.Messages.FirstOrDefault(m => m.Role == ChatRoles.User);
        Check(reUser?.Attachments is { Count: 1 }
              && reUser.Attachments[0].StoredName == imgAtt!.StoredName
              && reUser.Attachments[0].InsertOffset == 2,
              "H9 会话存读往返：附件引用与 InsertOffset 完整保留");

        // H10 旧会话兼容：没有 Attachments 字段的老 JSON 反序列化后必须为空，不得抛异常
        var legacyFile = Path.Combine(_sandbox, "chat_history", "legacy.json");
        File.WriteAllText(legacyFile, """
        {
          "Id": "legacy",
          "Rev": 1,
          "Mode": "Ask",
          "SystemPrompt": "x",
          "Messages": [ { "Role": "user", "Content": "老会话没有问题" } ],
          "SavedAt": "2026-01-01T00:00:00"
        }
        """, new UTF8Encoding(false));
        var legacy = ChatSessionService.Load(legacyFile);
        Check(legacy?.Messages.Any(m => m.Role == ChatRoles.User && m.Attachments == null) == true,
              "H10 旧会话 JSON（无附件字段）可正常读取，附件为空");

        // H11 孤儿清理：无人引用的附件该删，被会话引用的必须留
        var attDir = ChatAttachmentService.Dir;
        Directory.CreateDirectory(attDir);
        var orphan = Path.Combine(attDir, "orphan-no-ref.jpg");
        File.WriteAllBytes(orphan, new byte[] { 1, 2, 3 });
        File.SetLastWriteTime(orphan, DateTime.Now.AddHours(-3));   // 绕开"1 小时内新文件跳过"的保护窗
        File.SetLastWriteTime(Path.Combine(attDir, imgAtt!.StoredName), DateTime.Now.AddHours(-3));

        ChatAttachmentService.CleanupOrphans();

        Check(!File.Exists(orphan) && File.Exists(Path.Combine(attDir, imgAtt.StoredName)),
              "H11 孤儿附件被清理，被会话引用的附件必须保留");
    }

    /// <summary>造一个最小可解析的 .docx（zip 里塞 word/document.xml）</summary>
    private static void CreateMinimalDocx(string path, string text)
    {
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                  "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                  "<w:body><w:p><w:r><w:t>" + text + "</w:t></w:r></w:p></w:body></w:document>";
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        var entry = zip.CreateEntry("word/document.xml");
        using var s = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(xml);
        s.Write(bytes, 0, bytes.Length);
    }

    /// <summary>造一张指定尺寸的纯色 PNG（走 GDI+，避免在测试线程上碰 WPF 图像对象的线程亲和性）</summary>
    private static void CreateTestPng(string path, int width, int height)
    {
        using var bmp = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.FromArgb(64, 128, 200));
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            g.FillRectangle(brush, 20, 20, width - 40, 60);
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static readonly int[] Backoff = { 1, 1, 1 };   // 测试注入小退避（SyncEngine 构造参数）

    /// <summary>
    /// 测试注入零退避：WebDAVProvider 的 503/429 内部重试（生产默认 {5,15}）。
    /// 本机实测「断网/重试」组 25.8 秒里约 20 秒耗在这段干等上。数组长度 = 重试次数（与生产同为 2 次），只去掉等待。
    /// </summary>
    private static readonly int[] ThrottleBackoff = { 0, 0 };
}

/// <summary>
/// 秒级失败的同步渠道桩（2026-09-17）：所有方法立即抛 SyncProviderException(0)，即「网络错误」。
/// 存在的理由：原断网检查点用「连一个没人监听的端口」来造断网，而本机实测
/// 连得上 5 ms / 连不上 2.1 秒（loopback），十来次失败连接就把 8 条检查点拖成 25.8 秒。
/// 桩抛出的异常与 WebDAVProvider 真实转换出来的**同一类型、同一状态码**，引擎看到的东西不变，
/// 上层断言一条没改；「真实 TCP 失败 → IsNetwork」这条链路另有一条真实断言专门守护。
/// </summary>
internal sealed class FailingSyncProvider : ISyncProvider
{
    public string Name => "FailingStub";
    public SyncLimits Limits { get; } = new();

    private static SyncProviderException Down() =>
        new(0, "模拟断网：网络错误（测试桩，与 WebDAVProvider 真实转换同型）");

    public Task<SyncPullResult> PullAsync(string? since, CancellationToken ct) => Task.FromException<SyncPullResult>(Down());
    public Task<SyncPushResult> PushAsync(IReadOnlyList<SyncNote> changes, string? lastCursor, CancellationToken ct) => Task.FromException<SyncPushResult>(Down());
    public Task<List<SyncNote>> FullAsync(CancellationToken ct) => Task.FromException<List<SyncNote>>(Down());
    public Task<SyncMeta?> GetMetaAsync(CancellationToken ct) => Task.FromException<SyncMeta?>(Down());
    public Task SaveSaltAsync(string saltBase64, CancellationToken ct) => Task.FromException(Down());
}

/// <summary>
/// 最小 WebDAV 桩：PROPFIND/PUT/GET/DELETE/MKCOL（联调替代方案）。
/// 用 TcpListener 手写 HTTP（HttpListener 需 http.sys URL 预留，无外网沙箱环境会抛"句柄无效"）。
/// </summary>
internal sealed class TestWebDavServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _root;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    /// <summary>!=0 时所有请求返回该状态码（模拟 503 限流等）。</summary>
    public int ForceStatus;

    /// <summary>本实例实际监听地址（端口由系统分配）。</summary>
    /// <remarks>2026-09-11 修复：原先端口硬编码 18080，同一测试内创建第二个桩时必然冲突
    /// （TestFirstSyncDirMissing 会连开两个 → SocketException 10048）。改用端口 0 由系统分配。</remarks>
    public string BaseUrl { get; }

    public TestWebDavServer(string root, bool createRoot = true)
    {
        _root = root;
        if (createRoot) Directory.CreateDirectory(root);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _loop = Task.Run(ProcessLoop);
    }

    private async Task ProcessLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private async Task Handle(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(requestLine)) return;
            var parts = requestLine.Split(' ');
            var method = parts[0];
            var url = parts.Length > 1 ? parts[1].TrimStart('/') : "";

            var contentLength = 0;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
            }
            var body = "";
            if (contentLength > 0)
            {
                var buf = new char[contentLength];
                var read = 0;
                while (read < contentLength)
                    read += await reader.ReadAsync(buf.AsMemory(read, contentLength - read));
                body = new string(buf);
            }

            if (ForceStatus != 0) { WriteResponse(stream, ForceStatus, "", null); return; }
            switch (method)
            {
                case "PROPFIND":
                    if (!Directory.Exists(_root))   // 子目录缺失（真实坚果云：应用未建目录）
                    {
                        WriteResponse(stream, 404, "", null);
                        return;
                    }
                    var sb = new StringBuilder("<?xml version=\"1.0\"?><D:multistatus xmlns:D=\"DAV:\">");
                    sb.Append("<D:response><D:href>http://127.0.0.1:18080/</D:href>" +
                              "<D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop>" +
                              "<D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
                    foreach (var n in ListFiles())
                        sb.Append($"<D:response><D:href>http://127.0.0.1:18080/{n}</D:href>" +
                                  "<D:propstat><D:prop><D:getcontentlength>1</D:getcontentlength></D:prop>" +
                                  "<D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
                    sb.Append("</D:multistatus>");
                    WriteResponse(stream, 207, sb.ToString(), "application/xml");
                    break;
                case "MKCOL":
                    Directory.CreateDirectory(_root);
                    WriteResponse(stream, 201, "", null);
                    break;
                case "PUT":
                    Directory.CreateDirectory(_root);
                    File.WriteAllText(Path.Combine(_root, url), body, Encoding.UTF8);
                    WriteResponse(stream, 201, "", null);
                    break;
                case "GET":
                    var path = Path.Combine(_root, url);
                    if (!File.Exists(path)) { WriteResponse(stream, 404, "", null); return; }
                    WriteResponse(stream, 200, File.ReadAllText(path, Encoding.UTF8), "application/json");
                    break;
                case "DELETE":
                    var delPath = Path.Combine(_root, url);
                    if (File.Exists(delPath)) File.Delete(delPath);
                    WriteResponse(stream, 204, "", null);
                    break;
                default:
                    WriteResponse(stream, 405, "", null);
                    break;
            }
        }
        catch
        {
            /* 单请求失败不影响其他测试 */
        }
        finally
        {
            try { client.Close(); } catch { }
        }
    }

    private static void WriteResponse(Stream stream, int code, string? body, string? contentType)
    {
        var bytes = body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        var status = code switch
        {
            200 => "200 OK", 201 => "201 Created", 204 => "204 No Content",
            207 => "207 Multi-Status", 404 => "404 Not Found", 405 => "405 Method Not Allowed",
            503 => "503 Service Unavailable", _ => $"{code} Status"
        };
        var header = $"HTTP/1.1 {status}\r\nContent-Length: {bytes.Length}\r\n";
        if (contentType != null) header += $"Content-Type: {contentType}\r\n";
        header += "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (bytes.Length > 0) stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    public List<string> ListFiles() =>
        Directory.Exists(_root)
            ? Directory.GetFiles(_root).Select(Path.GetFileName).Where(n => n != null).Select(n => n!).ToList()
            : new List<string>();

    public string ReadFile(string name) => File.ReadAllText(Path.Combine(_root, name), Encoding.UTF8);

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
