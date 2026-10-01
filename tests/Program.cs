// ═══════════════════════════════════════════════════════════════════
// FocusCapture 自动化检查点
//
// 铁律（改动本文件前必读）：
//   1. 不得读写用户真实数据目录、不得联网 —— 检查点必须能在隔离环境独立跑完
//   2. 检查点失败必须输出中文人话（期望什么 / 实际什么），不能只吐堆栈
//   3. 禁止为「让检查点变绿」而放宽断言；改标准必须经用户确认，
//      并在被改动的检查点旁注明日期与原因
//
// 退出码：0 = 全部通过；1 = 有检查点失败
// ═══════════════════════════════════════════════════════════════════

// 本文件是顶层语句（没有 namespace FocusCapture 包着），所以链接进来的被测类的命名空间
// 必须显式 using —— 不能用 Services.AI.X 这种从 FocusCapture 起算的写法。
using FocusCapture.Services.AI;

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FocusCapture.Services;
using FocusCapture.Services.Files;
using FocusCapture.Services.Sync;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

// ── 分层（2026-09-25 物理分层）──
// 本层只放「纯逻辑」检查点：单进程、无 I/O、无 sleep、无网络（对齐 Google Small 测试定义）。
// 原先压着 95% 耗时的 6 个「真做事」组已**物理搬到慢层**（tests/sync/Program.OutOfScope.cs）：
//   [5] 剪贴板容错（真写系统剪贴板）/ [6] Skill 目录扫描（真建临时目录）/ [7] 候选目录（真建删临时目录）
//   [8] 子进程流式读（真起子进程）/ [9] 内置技能落地（真复制文件树）/ [10] 运行时下载器（自建本地 HTTP）
// 它们所需的源文件 <Compile Include> 一并从本工程 csproj 移除 —— 于是「本层不出现这些 API」
// 成为**机器可读的契约**（想加回来必须同时改 csproj，那是显眼动作），不靠人读注释判断。
// 改前实测 49,336 ms → 改后 ~0.65 秒。
//
// 运行方式：
//   dev.ps1 test         本层（纯逻辑，秒级）
//   dev.ps1 test -Slow   慢层（含上面 6 组 + 同步 / 加密 / 删除等重活）
//   dev.ps1 ready        交付点：编译 + 本层 + 慢层 + 文档引用检查

int pass = 0, fail = 0;

void Check(bool ok, string name, string? detail = null)
{
    if (ok)
    {
        pass++;
        Console.WriteLine("  PASS  " + name);
    }
    else
    {
        fail++;
        Console.WriteLine("  FAIL  " + name);
        if (!string.IsNullOrEmpty(detail)) Console.WriteLine("        说明：" + detail);
    }
}

string Cut(string? s) => s is null ? "(null)" : (s.Length <= 40 ? s : s[..40] + "…");

// ── 逐组耗时（2026-09-25 新增，机制与慢层一致）──
// 为什么加：此前快层只有一个「全跑一次多久」的数字，哪一组慢全靠猜（慢层 2026-09-17 装同款后
// 立刻暴露真瓶颈：8 条独占 25.8 秒，而 57 条只花 0.3 秒）。本层照搬。
// 只做测量：不包任何断言、不改任何行为、不参与退出码。
var swTotal = System.Diagnostics.Stopwatch.StartNew();
long markAt = 0;
var groupMs = new List<(string Name, long Ms)>();
void Mark(string name)
{
    var now = swTotal.ElapsedMilliseconds;
    groupMs.Add((name, now - markAt));
    markAt = now;
}

Console.WriteLine("===== FocusCapture 自动化检查点 =====");
Console.WriteLine();

// ── [1] 加密解密 CryptoService ──
// 不可逆损失类：这块坏了 = 云端数据永久读不回来，而且用户自己察觉不到。
// 因此列为第一批最高优先级。
Console.WriteLine("[1] 加密解密 CryptoService");

var salt = CryptoService.GenerateSalt();
var dek = CryptoService.DeriveKey("Abc12345", salt);

Check(dek.Length == 32, "密钥派生结果必须是 32 字节", $"实际 {dek.Length} 字节");

Check(CryptoService.DeriveKey("Abc12345", salt).SequenceEqual(dek),
      "同密码 + 同盐，派生结果必须一致（不一致 = 换台设备就解不开）");

Check(!CryptoService.DeriveKey("Abc12346", salt).SequenceEqual(dek),
      "密码不同，派生结果必须不同");

var plain = "今天要记得写周报 ABC 123\n第二行带标点：，。！";
var enc = CryptoService.Encrypt(dek, plain);
var dec = CryptoService.Decrypt(dek, enc);

Check(dec == plain, "加密后必须能原样解密（含中文 / 换行 / 标点）",
      $"期望「{Cut(plain)}」，实际「{Cut(dec)}」");

Check(CryptoService.Encrypt(dek, plain) != CryptoService.Encrypt(dek, plain),
      "同一段内容加密两次，密文必须不同（否则等于没加密）");

var wrongKey = CryptoService.DeriveKey("Abc12345", CryptoService.GenerateSalt());
var threw = false;
try { CryptoService.Decrypt(wrongKey, enc); }
catch (CryptographicException) { threw = true; }
catch { }

Check(threw, "密钥不对时必须抛异常，不能静默返回乱码内容");

var code = CryptoService.GenerateRecoveryCode();
Check(code.Length == 14, "恢复码必须是 14 位", $"实际 {code.Length} 位");
Check(!code.Any(c => "0O1Il".Contains(c)), "恢复码不得含易混淆字符 0O1Il", $"实际「{code}」");

Console.WriteLine();

// ── [2] 时间解析 TimeParser ──
// 注意：本组只校验「时分」，不校验日期 —— 解析依赖真实当前时间，
// 写死日期会随日历漂移，造成周期性误报。
Console.WriteLine("[2] 时间解析 TimeParser");

var r1 = TimeParser.Parse("今天8点30分");
Check(r1.Matched && r1.Time.Hour == 8 && r1.Time.Minute == 30,
      "「今天8点30分」→ 08:30", $"实际 {r1.Time:HH:mm}");

var r2 = TimeParser.Parse("上午10点");
Check(r2.Matched && r2.Time.Hour == 10,
      "「上午10点」→ 10:00", $"实际 {r2.Time:HH:mm}");

var r3 = TimeParser.Parse("下午3点");
Check(r3.Matched && r3.Time.Hour == 15,
      "「下午3点」→ 15:00", $"实际 {r3.Time:HH:mm}");

var r4 = TimeParser.Parse("半小时后");
Check(r4.Matched && Math.Abs((r4.Time - DateTime.Now.AddMinutes(30)).TotalSeconds) < 5,
      "「半小时后」→ 约 30 分钟后");

var r5 = TimeParser.Parse("下午茶");
Check(!r5.Matched, "「下午茶」不该被识别成时间");

// 时间表达剥离（2026-09-27 phase2）：手动建待办设上提醒后，正文里的日期时间要剥掉只留事情本身。
// 样例全部选用「任何日期跑都命中」的表达（相对日 / 月日 / 相对时长），不写死日期防日历漂移。
var s1 = TimeParser.StripTimeExpression("10月1日上午9点12分要坐高铁回家");
Check(s1 == "要坐高铁回家", "日期+时段+时刻要整体剥掉，只留事情本身",
      $"实际「{s1}」");
var s2 = TimeParser.StripTimeExpression("明天下午3点，部门会议");
Check(s2 == "部门会议", "剥除后暴露在首尾的标点要收掉", $"实际「{s2}」");
var s3 = TimeParser.StripTimeExpression("半小时后开会");
Check(s3 == "开会", "相对时长（半小时后）同样要剥掉", $"实际「{s3}」");
var s4 = TimeParser.StripTimeExpression("买牛奶");
Check(s4 == "买牛奶", "没命中时间表达 → 原样返回（一个字都不动）", $"实际「{s4}」");

// ── [3] 灵感速览标题栏目录 QuickViewToolbarCatalog ──
// 坏了的表现：升级后标题栏空白 / 手改 settings.json 后面板再也唤不出按钮 / 设置页预算虚标。
Console.WriteLine("[3] 灵感速览标题栏目录 QuickViewToolbarCatalog");

var (dL, dR) = QuickViewToolbarCatalog.Sanitize(null, null);
Check(dL.SequenceEqual(QuickViewToolbarCatalog.DefaultLeft) && dR.SequenceEqual(QuickViewToolbarCatalog.DefaultRight),
      "配置为空时必须回退默认布局（升级老用户 settings.json 无此字段即走此路径）",
      $"实际 left=[{string.Join(",", dL)}] right=[{string.Join(",", dR)}]");

var (cL, cR) = QuickViewToolbarCatalog.Sanitize(
    new List<string> { "Bogus", "Refresh", "Search", "Search" },
    new List<string> { "NoSuchThing", "GetNote" });
Check(cL.SequenceEqual(new[] { "Refresh", "Search" }) && cR.SequenceEqual(new[] { "GetNote" }),
      "未知 id 必须过滤、重复 id 必须去重、有效项保持原顺序",
      $"实际 left=[{string.Join(",", cL)}] right=[{string.Join(",", cR)}]");

var (gL, gR) = QuickViewToolbarCatalog.Sanitize(new List<string> { "X", "Y" }, new List<string>());
Check(gL.SequenceEqual(QuickViewToolbarCatalog.DefaultLeft) && gR.SequenceEqual(QuickViewToolbarCatalog.DefaultRight),
      "整列全是无效 id 时必须回退默认（不能让标题栏彻底清空）");

var defAll = QuickViewToolbarCatalog.DefaultLeft.Concat(QuickViewToolbarCatalog.DefaultRight)
    .Select(QuickViewToolbarCatalog.Find).ToList();
Check(defAll.All(f => f != null) && defAll.Count == 8,
      "默认布局的 8 个 id 必须都能在功能目录中找到（typo 会让按钮凭空消失）");

Check(Math.Abs(QuickViewToolbarCatalog.AvailableBudget(620) - 440) < 0.01,
      "620 宽面板的标题栏预算 = 620 - 固定开销 180 = 440px",
      $"实际 {QuickViewToolbarCatalog.AvailableBudget(620):0}px");

var used = QuickViewToolbarCatalog.UsedBudget(QuickViewToolbarCatalog.DefaultLeft.ToList(),
    QuickViewToolbarCatalog.DefaultRight.ToList());
Check(Math.Abs(used - 396) < 0.01,
      "默认布局的估算占用必须是 396px（预算表数字被随手改动会同时坑设置页与面板）",
      $"实际 {used:0}px");

Check(!QuickViewToolbarCatalog.CanAdd(620, QuickViewToolbarCatalog.DefaultLeft.ToList(),
          QuickViewToolbarCatalog.DefaultRight.ToList(), "Export"),
      "620 宽 + 默认布局下再加一个文字钮必须判为空间不足（396+74 > 440）");

Check(QuickViewToolbarCatalog.CanAdd(1280, QuickViewToolbarCatalog.DefaultLeft.ToList(),
          QuickViewToolbarCatalog.DefaultRight.ToList(), "AiAsk"),
      "面板拉宽到 1280 后加按钮必须放行（预算随宽度增长）");

// ── [4] 文件仓库与文件句柄 ──
// 说明（2026-09-16）：本组**放在慢层**（tests/sync/），不放这里。
// 原因：本工程刻意不引用主项目（只链接少数无依赖的源文件，以保持秒级编译），
//       而文件仓库必然依赖日志/设置/附件服务，链进来会把这层轻量结构毁掉。
// 红线守卫（句柄不可伪造）与淘汰保护都在慢层，交付前必跑。

Mark("[1]-[3] 加密 / 时间解析 / 标题栏目录");


// ── [11] 待办与提醒面板的源码契约（2026-09-22）──
// 守的全是「改错了不报错、只静默退化」的东西，判据直接读源码文本（这几处坏了只有真机才看得出来）：
//   ① 提醒时间弹窗写死 Height → 底部按钮被窗口下沿裁掉一截（2026-09-22 用户截图确认）。
//      实测：内容需要 182px，写死的 170 差 12px —— 正好是按钮缺掉的那一截。
//   ② 设置面板用 ShowDialog 打开 → WPF 模态「禁用应用内所有其他窗口」（官方文档原文）。
//      先开灵感速览/AI 问答再开设置，那两个面板就点不动了。
//   ③ 右键菜单自带 Style → 顶掉 App.xaml 的隐式深色模板，默认模板左侧那道浅色图标槽就是「白条」。
Console.WriteLine("[11] 待办与提醒面板的源码契约");

var uiRepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

var dueTimeXamlPath = Path.Combine(uiRepoRoot, "Windows", "DueTimeDialog.xaml");
var dueTimeXaml = File.Exists(dueTimeXamlPath) ? File.ReadAllText(dueTimeXamlPath) : "";
Check(dueTimeXaml.Length > 0 && !dueTimeXaml.Contains("Height=\""),
      "提醒时间弹窗不得写死高度（Height 含系统标题栏，客户区不够会把底部按钮裁掉一截）",
      "DueTimeDialog.xaml 里又出现了固定 Height —— 该用 SizeToContent=\"Height\" 让窗口跟着内容走");
Check(dueTimeXaml.Contains("SizeToContent=\"Height\""),
      "提醒时间弹窗必须声明 SizeToContent=\"Height\"（高度随内容自适应）",
      "在 DueTimeDialog.xaml 里找不到 SizeToContent=\"Height\"");

var mainCsPath = Path.Combine(uiRepoRoot, "MainWindow.xaml.cs");
var mainCs = File.Exists(mainCsPath) ? File.ReadAllText(mainCsPath) : "";
var openIdx = mainCs.IndexOf("private void OpenSettings()", StringComparison.Ordinal);
var openBody = openIdx >= 0 ? mainCs.Substring(openIdx, Math.Min(2600, mainCs.Length - openIdx)) : "";
Check(openIdx >= 0 && !openBody.Contains(".ShowDialog()"),
      "设置面板不得用 ShowDialog 打开（模态会禁用应用内所有其他窗口，先开的面板会点不动）",
      openIdx < 0
          ? "MainWindow.xaml.cs 里找不到 OpenSettings 方法"
          : "OpenSettings 里又出现了 ShowDialog —— 改用 Show() + EnsureWindowVisible（返回值本来也没人用）");
Check(mainCs.Contains("EnsureWindowVisible("),
      "窗口唤出必须走统一的 EnsureWindowVisible（最小化归位 + 前置），否则会「只在任务栏出现」",
      "找不到 EnsureWindowVisible —— 唤出流程被绕过了");

var todoCsPath = Path.Combine(uiRepoRoot, "Windows", "TodoSummaryWindow.xaml.cs");
var todoCs = File.Exists(todoCsPath) ? File.ReadAllText(todoCsPath) : "";
Check(todoCs.Contains("new ContextMenu()") && !todoCs.Contains("new ContextMenu {"),
      "待办汇总右键菜单必须走隐式样式（new ContextMenu()），不得自带 Style/模板（会顶掉 App.xaml 的深色模板 = 白条）",
      "菜单创建写法变了：应保持 new ContextMenu() 且不带对象初始化器");
Check(todoCs.Contains("Header = \"编辑\"") && todoCs.Contains("Header = \"复制\"")
      && todoCs.Contains("跳转到灵感速览") && todoCs.Contains("设置提醒…") && todoCs.Contains("取消提醒"),
      "待办汇总右键菜单五项必须齐全：编辑 / 复制 / 跳转到灵感速览 / 设置提醒… / 取消提醒",
      "少了某一项（用户 2026-09-22 明确要求的五项）");
Check(todoCs.Contains("ClickCount"),
      "待办汇总必须支持双击进编辑（Border 是 Decorator，没有 MouseDoubleClick，只能看 ClickCount）",
      "找不到 ClickCount —— 双击编辑入口可能被删了");
Check(todoCs.Contains("TodoEditService.ResolveDueAsync"),
      "待办汇总改完时间必须走 TodoEditService.ResolveDueAsync（与灵感速览同一条识别路径，用户拍板照搬）",
      "找不到 ResolveDueAsync 调用 —— 时间识别被写成另一套了");

var snapCsPath = Path.Combine(uiRepoRoot, "Diagnostics", "UiSnapshot.cs");
var snapCs = File.Exists(snapCsPath) ? File.ReadAllText(snapCsPath) : "";
Check(snapCs.Contains("new DueTimeDialog("),
      "界面快照必须注册「提醒时间弹窗」场景（按钮被裁这类问题只有出图才看得见，此前正是漏了这个场景）",
      "UiSnapshot.cs 里找不到 DueTimeDialog 场景");

// ── [12] 运行时下载器失败报告的源码契约（2026-09-22）──
// 守的是「失败报告接线」本身 —— 分类器和提示文案写了没人调用等于没写（判据读源码文本）。
Console.WriteLine("[12] 运行时下载器失败报告的源码契约");

var rdCsPath = Path.Combine(uiRepoRoot, "Services", "Skills", "RuntimeDownloader.cs");
var rdCs = File.Exists(rdCsPath) ? File.ReadAllText(rdCsPath) : "";
Check(rdCs.Contains("LooksLikeSecurityBlock(errors)"),
      "失败汇总必须接上安全拦截判定（LooksLikeSecurityBlock 只定义不调用 = 提示永远不会出现）",
      "RuntimeDownloader.cs 的 InstallAsync 里找不到 LooksLikeSecurityBlock(errors) 调用");
Check(rdCs.Contains("安全软件") && rdCs.Contains("白名单"),
      "安全拦截提示文案必须常驻源码（提示正文被清空等于没有提示）",
      "RuntimeDownloader.cs 里找不到 SecurityBlockHint 的正文关键词");
Check(rdCs.Contains("SecurityBlockHint;"),
      "Fail 路径必须真的追加 SecurityBlockHint（return 处漏接 = 白写）",
      "找不到 failMessage += SecurityBlockHint 的接线");

// ── [13] AI 模型数值输入解析（2026-09-23）──
// 守的是「用户照供应商文档抄的写法能不能被吃下」。这批错法全是静默的：
// 384K 被当非法值丢掉（用户以为填上了）、K 与 M 差 1024 倍（窗口算错一个数量级）——
// 界面上都不会报错，所以只能靠机器守。
Console.WriteLine("[13] AI 模型数值输入解析 TokenCountParser");
Mark("[11]-[12] 源码契约检查");

Check(TokenCountParser.TryParse("4096", out var tk1) && tk1 == 4096,
      "纯数字：4096 → 4096");
Check(TokenCountParser.TryParse("384K", out var tk2) && tk2 == 393216,
      "带 K 后缀：384K → 393216（1024 进制）");
Check(TokenCountParser.TryParse("384k", out var tk3) && tk3 == 393216,
      "K 后缀大小写不敏感");
Check(TokenCountParser.TryParse("1M", out var tk4) && tk4 == 1048576,
      "带 M 后缀：1M → 1048576");
Check(TokenCountParser.TryParse("1.5K", out var tk5) && tk5 == 1536,
      "小数 + 后缀：1.5K → 1536");
Check(TokenCountParser.TryParse("  8 K  ", out var tk6) && tk6 == 8192,
      "前后空格容忍（用户复制粘贴常带空格）");
Check(TokenCountParser.TryParse("", out var tk7) && tk7 == 0,
      "留空 = 不限制：返回 true + 0，不是非法输入");
Check(TokenCountParser.TryParse(null, out var tk8) && tk8 == 0,
      "null 与留空同处理");
Check(TokenCountParser.TryParse("   ", out var tk9) && tk9 == 0,
      "全空白与留空同处理");
Check(TokenCountParser.TryParse("0", out var tk10) && tk10 == 0,
      "显式填 0 = 不限制（与留空同义）");
Check(!TokenCountParser.TryParse("abc", out _),
      "非数字必须判非法 —— 调用方保留原值，不许静默写成 0");
Check(!TokenCountParser.TryParse("-5", out _),
      "负数判非法（窗口/输出上限都没有负数语义）");
Check(!TokenCountParser.TryParse("K", out _),
      "只写单位不写数字判非法");
Check(!TokenCountParser.TryParse("99999999999", out _),
      "超出 int 范围判非法 —— 打错一位数时宁可拒绝，也不静默存下天文数字（它会真被写进请求体）");
Check(TokenCountParser.MaxContextWindow == 10_000_000
      && TokenCountParser.MaxOutputTokens == 1_000_000,
      "上限常量与拍板值一致：窗口 1~10,000,000 / 输出 1~1,000,000");

// ── [14] /models 响应解析与探测结果分类（2026-09-23）──
// 守的是「换一家供应商就悄悄少几个模型」「欠费被说成稍后重试」这两类静默错误。
Console.WriteLine("[14] 供应商 /models 解析与探测分类");

var lmStandard = AiModelListParser.Parse("""
{"object":"list","data":[{"id":"m1","object":"model"},{"id":"m2","object":"model"}]}
""");
Check(lmStandard.Count == 2 && lmStandard[0].Id == "m1",
      "标准 OpenAI 结构（顶层 data）：解析出 2 条");

var lmModelsKey = AiModelListParser.Parse("""
{"models":[{"id":"a"},{"id":"b"}]}
""");
Check(lmModelsKey.Count == 2,
      "顶层容器是 models 也能认（少数节点不用 data）");

var lmZhiPu = AiModelListParser.Parse("""
{"data":[{"id":"glm-4-flash","object":"model"}]}
""");
Check(lmZhiPu.Count == 1 && lmZhiPu[0].Id == "glm-4-flash" && lmZhiPu[0].ContextWindow == 0,
      "智谱那种只给 id+object 的元素：解析得出来，窗口留 0（拿不到就不猜）");

var lmTokenHub = AiModelListParser.Parse("""
{"data":[{"id":"hunyuan-t1","object":"model","name":"腾讯混元 T1","status":"online"}]}
""");
Check(lmTokenHub.Count == 1 && lmTokenHub[0].DisplayName == "腾讯混元 T1",
      "腾讯 TokenHub 带 name：显示名取 name（用户不用自己填）");

var lmKimi = AiModelListParser.Parse("""
{"data":[{"id":"kimi-k2","object":"model","context_length":262144,"supports_reasoning":true}]}
""");
Check(lmKimi.Count == 1 && lmKimi[0].ContextWindow == 262144,
      "Kimi 的 context_length：带出上下文窗口（7 家里唯一给这个字段的）");

var lmAltWindow = AiModelListParser.Parse("""
{"data":[{"id":"x","context_window":"131072"}]}
""");
Check(lmAltWindow.Count == 1 && lmAltWindow[0].ContextWindow == 131072,
      "窗口字段名变体 context_window、且值是字符串：也要能认");

var lmNameFallback = AiModelListParser.Parse("""
{"data":[{"name":"只有 name 没有 id"}]}
""");
Check(lmNameFallback.Count == 1 && lmNameFallback[0].Id == "只有 name 没有 id",
      "缺 id 但有 name：按 id→name→model 的退让链取标识，不要丢掉这条");

var lmDedup = AiModelListParser.Parse("""
{"data":[{"id":"dup"},{"id":"dup"}]}
""");
Check(lmDedup.Count == 1, "同一 id 出现两次要去重（列表里给用户看两遍同一个模型很怪）");

var lmJunk = AiModelListParser.Parse("""
{"data":["不是对象",42,{"id":"ok"}]}
""");
Check(lmJunk.Count == 1 && lmJunk[0].Id == "ok",
      "数组里混了非对象元素：跳过它们，不要把整批都丢掉");

var lmBad = AiModelListParser.Parse("{这不是 JSON");
Check(lmBad.Count == 0, "非法 JSON → 空列表，不抛（上层给「没解析出模型」的人话提示）");
Check(AiModelListParser.Parse(null).Count == 0 && AiModelListParser.Parse("   ").Count == 0,
      "null / 空白 → 空列表，不抛");

Check(AiHealthClassifier.Classify(200, "").Status == AiHealthStatus.Ok,
      "200 → 通过");
Check(AiHealthClassifier.Classify(401, "").Status == AiHealthStatus.Key,
      "401 → Key 未通过验证");
Check(AiHealthClassifier.Classify(403, "").Status == AiHealthStatus.Key,
      "403 → 也归 Key（少数节点用 403 表达无权限）");
Check(AiHealthClassifier.Classify(402, "").Status == AiHealthStatus.Account,
      "402 → 余额不足（DeepSeek 用它表达欠费）");

var hz1 = AiHealthClassifier.Classify(429, """{"error":{"code":1113,"message":"欠费"}}""");
Check(hz1.Status == AiHealthStatus.Account,
      "429 + 智谱业务码 1113（欠费）→ 必须是「账户余额/套餐」，不是「稍后重试」",
      "误判成 Server 的话，用户会对着欠费一直重试，而我们还在说「稍后重试」—— 那是误导");

var hz2 = AiHealthClassifier.Classify(429, """{"error":{"code":1302,"message":"速率限制"}}""");
Check(hz2.Status == AiHealthStatus.Server,
      "429 + 限流码 1302 → 仍是「稍后重试」（不能把所有 429 都当账户问题）");

Check(AiHealthClassifier.Classify(429, "").Status == AiHealthStatus.Server,
      "429 且读不到业务码 → 保守判「稍后重试」");
Check(AiHealthClassifier.Classify(500, "").Status == AiHealthStatus.Server,
      "5xx → 供应商侧问题");

var hzPlain = AiHealthClassifier.Classify(401, "Authentication Fails (governor)");
Check(hzPlain.Status == AiHealthStatus.Key && hzPlain.Message.Contains("Authentication Fails"),
      "错误体是纯文本（DeepSeek 那种）时：按状态码分类，且原文必须照实带给用户",
      "吞掉原文 = 用户只看到「失败」两个字，无从下手");

var hzNet = AiHealthClassifier.Unreachable("No such host is known");
Check(hzNet.Status == AiHealthStatus.Network
      && hzNet.Message.Contains("网络") && hzNet.Message.Contains("填错"),
      "连不上 → Network，且文案要同时提示「本机网络」与「地址填错」两种可能",
      "只说网络问题会把「地址写错」这种用户自己能修的情况掩盖掉");

// ── [15] 上下文裁剪 ContextBudget（2026-09-23）──
// 守六个坑，每一个错了都不报错，只在特定长会话里表现为「AI 莫名忘了前面说的」或「随机 400」。
Console.WriteLine("[15] 上下文裁剪 ContextBudget");

ContextItem Ci(string role, int tokens, bool toolCalls = false, int images = 0)
    => new(role, toolCalls, tokens, images);

// 坑①：估算系数与安全余量
Check(ContextBudget.EstimateTokens("") == 0 && ContextBudget.EstimateTokens(null) == 0,
      "空文本估算为 0 token");
Check(ContextBudget.EstimateTokens("中文八个字呀") > ContextBudget.EstimateTokens("abcdefghij"),
      "中文按更密的系数估（同样长度中文 token 更多）—— 按 ASCII 的 4 字符算会严重低估");

// 窗口留空（0）= 不裁剪：这是改造前行为的回归保护
var noWindow = ContextBudget.Plan(new[] { Ci("user", 999999) }, 0, 4096, 0);
Check(!noWindow.ShouldTrim && !noWindow.SingleMessageTooLarge,
      "上下文窗口留空（0）→ 一律不裁剪（与改造前行为一致）");

var emptyPlan = ContextBudget.Plan(Array.Empty<ContextItem>(), 100000, 4096, 0);
Check(!emptyPlan.ShouldTrim, "没有消息 → 不裁剪");

// 装得下就不裁
var fits = ContextBudget.Plan(new[] { Ci("user", 100), Ci("assistant", 100), Ci("user", 50) }, 100000, 4096, 1000);
Check(!fits.ShouldTrim, "内容装得下 → 一条都不裁（不做无谓动作）");

// 超窗口 → 从最早丢、保留最近的
var many = Enumerable.Range(0, 40).Select(i => Ci("user", 300)).ToList();
var cut = ContextBudget.Plan(many, 3000, 0, 0);
Check(cut.ShouldTrim && cut.DroppedCount > 0 && cut.KeepFromIndex > 0,
      "超窗口 → 从最早的开始丢（保留最近的对话）");
Check(cut.UserHint != null && cut.UserHint.Contains(cut.DroppedCount.ToString()),
      "裁剪必须给出轻提示，且提示里带丢掉的条数",
      "没有提示的话用户只会解读成「AI 变笨了」，永远查不到原因");

// 坑②：工具调用对不能被拆散（**本组最重要的一条**）
var toolUnit = new[]
{
    Ci("user", 30),
    Ci("assistant", 40, toolCalls: true),
    Ci("tool", 40),
    Ci("user", 5),
};
var unitPlan = ContextBudget.Plan(toolUnit, 100, 0, 0);   // 可用 80：只剩最后一组装得下
// 组划分：①[user30] ②[assistant40(tool_calls), tool40] ③[user5]
// 期望丢掉 ①② 共 3 条；关键是 KeepFromIndex 必须落在**组边界**（2）上。
// 若落在组内部（1）说明工具调用对被从中间切开 —— 那正是会引发随机 400 的形态。
Check(unitPlan.KeepFromIndex == 2 && unitPlan.DroppedCount == 3,
      "裁剪边界必须落在「完整工具调用单元」的边界上（不许从组内部切）",
      $"实际 KeepFromIndex={unitPlan.KeepFromIndex}、DroppedCount={unitPlan.DroppedCount}；"
      + "落在 1 就意味着 assistant(tool_calls) 与它的 tool 结果被拆散 → 接口 400（tool_call_id 找不到对应调用）");

var toolUnit2 = new[]
{
    Ci("user", 30),
    Ci("assistant", 30, toolCalls: true),
    Ci("tool", 5),
    Ci("tool", 5),
    Ci("assistant", 10),
    Ci("user", 5),
};
var unitPlan2 = ContextBudget.Plan(toolUnit2, 100, 0, 0);
Check(unitPlan2.DroppedCount == 1 && unitPlan2.KeepFromIndex == 1,
      "只丢最前面那条独立消息时，工具调用组完整保留（不误伤）");

// 坑④：图片按固定值计入，不按字符
var textOnly = ContextBudget.Plan(new[] { Ci("user", 100), Ci("user", 100), Ci("user", 100) }, 1000, 0, 0);
var withImage = ContextBudget.Plan(
    new[] { Ci("user", 100, images: ContextBudget.ImageTokensPerImage), Ci("user", 100), Ci("user", 100) },
    1000, 0, 0);
Check(withImage.KnownTokens - textOnly.KnownTokens == ContextBudget.ImageTokensPerImage,
      "图片必须单独占一份固定 token 估算（差值恰为 ImageTokensPerImage）",
      "图片以 base64 塞进正文，按字符算会严重低估，然后被供应商 400");

// 坑⑥：单条自己就超窗口 → 拒绝发送，而不是把用户的消息也丢掉
var oversized = ContextBudget.Plan(new[] { Ci("user", 5000) }, 2000, 0, 0);
Check(oversized.SingleMessageTooLarge && !oversized.ShouldTrim && oversized.UserHint != null,
      "单条消息本身就超窗口 → 标为「太大」并给出人话提示，绝不裁剪掉用户刚发的内容",
      "裁剪历史救不了这种情况，只能拒绝发送并说清原因，不然就是去撞英文 400");

// 固定开销（系统提示词 + 工具定义）把窗口吃光
var eaten = ContextBudget.Plan(new[] { Ci("user", 10) }, 2000, 500, 1900);
Check(eaten.SingleMessageTooLarge && eaten.UserHint != null && eaten.UserHint.Contains("工具定义"),
      "系统提示词 + 工具定义占满窗口 → 提示换更大窗口的模型（这种情况裁剪无能为力）");

// 坑①续：安全余量真的存在 —— 内容没超窗口但超「窗口 − 20%」时也该裁
var marginItems = Enumerable.Range(0, 10).Select(_ => Ci("user", 900)).ToList();   // 共 9000
var marginPlan = ContextBudget.Plan(marginItems, 10000, 0, 0);                     // 可用 8000
Check(marginPlan.ShouldTrim,
      "内容 9000 < 窗口 10000，但超过「窗口 − 20% 余量」→ 必须裁",
      "不留余量的话，估算误差（没有 tokenizer，误差可达 20%+）会偶发把请求顶出窗口");

// ── [16] 全文搜索匹配 ChatSearchMatcher（2026-09-23）──
// 守的核心是一个隐形坑：片段里的换行会被压成空格 —— 替换会改变字符串长度，
// 若拿替换前的下标去高亮，高亮位置会整体偏移（界面上只是"高亮歪了一点"，不报任何错）。
Console.WriteLine("[16] 全文搜索匹配 ChatSearchMatcher");

Check(ChatSearchMatcher.ExtractSnippet("", "abc") == null && ChatSearchMatcher.ExtractSnippet("abc", "") == null,
      "空正文 / 空关键词 → 不返回片段",
      "用户在搜索框里每敲一个字都会调它，抛异常就是搜索框直接崩");

Check(ChatSearchMatcher.ExtractSnippet("abc", "   ") == null,
      "纯空白关键词等同没输入 —— 不能把空格当成搜索词");

Check(ChatSearchMatcher.ExtractSnippet("hello world", "xyz") == null,
      "未命中 → null（调用方据此跳过这条会话）");

var searchHit1 = ChatSearchMatcher.ExtractSnippet("hello world", "world");
Check(searchHit1 != null && searchHit1.Value.Start == 6 && searchHit1.Value.Length == 5,
      "下标必须是「命中词在返回片段内」的位置，长度 = 关键词长度",
      "这个下标是给界面做高亮用的，差一位就是高亮错位");

Check(ChatSearchMatcher.ExtractSnippet("Hello World", "world") != null,
      "英文大小写不敏感",
      "区分大小写会让用户搜 world 找不到 World，且他不知道为什么找不到");

Check(ChatSearchMatcher.CountMatches("a ba ba", "ba") == 2,
      "命中计数：多次出现要数全");

Check(ChatSearchMatcher.CountMatches("aaa", "aa") == 1,
      "不重叠计数（aaa 里 aa 只算 1 次）",
      "重叠计数会让「命中 N 处」这个提示数字虚高");

var searchMulti = "第一行\n第二行有目标词\n第三行";
var searchHit2 = ChatSearchMatcher.ExtractSnippet(searchMulti, "目标词");
Check(searchHit2 != null && searchHit2.Value.Snippet.Contains("目标词"),
      "跨行命中：命中词必须完整落在片段里");

Check(searchHit2 != null && !searchHit2.Value.Snippet.Contains('\n'),
      "片段里不允许出现换行（界面按一行展示，带换行会撑成多行破坏排版）");

Check(searchHit2 != null
      && searchHit2.Value.Start >= 0
      && searchHit2.Value.Start + searchHit2.Value.Length <= searchHit2.Value.Snippet.Length,
      "压平换行之后，高亮下标仍必须落在片段范围内",
      "换行替换改变了长度 —— 沿用替换前的下标就会越界或错位，这正是本组要守的坑");

var searchLong = new string('x', 100) + "目标" + new string('y', 100);
var searchHit3 = ChatSearchMatcher.ExtractSnippet(searchLong, "目标");
Check(searchHit3 != null && searchHit3.Value.Snippet.Length == 20 + 2 + 20,
      "命中两侧各带 20 字上下文（默认值）",
      "上下文太短看不出命中在说什么，太长则把列表撑乱");

var searchHit4 = ChatSearchMatcher.ExtractSnippet("目标在开头后面还有很长很长的一段", "目标");
Check(searchHit4 != null && searchHit4.Value.Start == 0,
      "命中在正文开头：不越界，下标为 0");

Check(ChatSearchMatcher.CountMatches(null!, "a") == 0 && ChatSearchMatcher.ExtractSnippet(null!, "a") == null,
      "null 正文必须安全返回（不抛）",
      "会话正文可能为 null（附件消息），搜索不能因此整体失败");

// ── [17] AI 整理的提示词 / 规则拼接 / 待办行解析 / 结果清洗 NoteTidyPrompt（2026-09-26；2026-09-27 规则化）──
// 守的是三件"坏了不报错"的事：
//   ① 长度闸门算错 → 超长正文照发：要么被服务商 400，要么只整理了前半段
//      （后者更糟：用户会以为后半段也整理过了，而笔记里那半段还是乱的原样）；
//   ② 结果清洗漏掉围栏 / 开场白 → 笔记里凭空多出 ``` 与「以下是整理后的内容：」，写进去就擦不掉；
//   ③ 提示词拼错段 → 预置规则漏「不翻译」模型就顺手翻译；自定义规则若被偷偷拼了硬约束，
//      用户写"输出成代码块"这类指令就会静默失效（2026-09-27 拍板：自定义整段替换）。
Console.WriteLine("[17] AI 整理提示词与结果清洗 NoteTidyPrompt");

var (tidySys, tidyUser) = NoteTidyPrompt.BuildMessages("  一段随手写的乱文字  ");
Check(tidySys.Contains("不新增任何事实") && tidySys.Contains("不要翻译"),
      "默认系统提示词（理顺条理）必须同时锁死「不新增事实」与「不翻译」",
      "漏一条，模型就会替用户补全或顺手翻译 —— 产出不再是他的原始记录了");
Check(tidySys.Contains("正常序号"),
      "默认系统提示词必须要求层级用正常序号（1. 2. 3.）—— 旧版「只用 - 与 **」已按用户要求取消（结果难读）");
Check(tidyUser == "一段随手写的乱文字",
      "送给模型的正文要去掉首尾空白", $"实际「{Cut(tidyUser)}」");

var customSys = NoteTidyPrompt.BuildMessages("正文", "你是一个翻译腔很重的助手。").System;
Check(customSys == "你是一个翻译腔很重的助手。",
      "自定义规则的提示词必须整段生效，不许偷偷拼上预置的输出纪律或指令段",
      "2026-09-27 拍板：自定义完全交给用户 —— 拼了就等于背着他改他的指令");

Check(NoteTidyPrompt.OutputDiscipline.Contains("不要翻译") && NoteTidyPrompt.OutputDiscipline.Contains("代码块围栏"),
      "预置规则共用的输出纪律必须锁「不翻译」与「无围栏无开场白」");
Check(NoteTidyPrompt.ExtractTodoInstruction.Contains("【yyyy-MM-dd HH:mm】") && NoteTidyPrompt.ExtractTodoInstruction.Contains("未发现待办"),
      "提取待办的指令必须约定行内时间戳格式与「未发现待办」空态标记（解析器按此机械解析）");
Check(NoteTidyPrompt.ExplainInstruction.Contains("名词解释") && NoteTidyPrompt.ExplainInstruction.Contains("完整保留用户原文"),
      "解释扩展必须「原文一字不改 + 末尾附注」—— 它是唯一允许新增内容的预置规则，边界收在附注区");

Check(NoteTidyPrompt.CharCount("  abc  ") == 3, "字符数不计首尾空白");
Check(!NoteTidyPrompt.IsTooLong(new string('中', NoteTidyPrompt.MaxInputChars)),
      $"上限内（恰 {NoteTidyPrompt.MaxInputChars} 字符）必须放行",
      "边界取错一个字，用户就会在刚好够用的长度上被拒");
Check(NoteTidyPrompt.IsTooLong(new string('中', NoteTidyPrompt.MaxInputChars + 1)),
      "超出上限 1 个字符也必须拦下（不做截断、不做分段 —— 用户拍板：宁可拒绝）");

var tooLongMsg = NoteTidyPrompt.TooLongMessage(9000);
Check(tooLongMsg.Contains("9000") && tooLongMsg.Contains(NoteTidyPrompt.MaxInputChars.ToString()),
      "超长提示必须同时给出「实际多少字」与「上限多少字」",
      "只说超了，用户不知道该删到多少");

Check(NoteTidyPrompt.CleanResult("") == "" && NoteTidyPrompt.CleanResult(null) == "",
      "空回包清洗后仍是空串（调用方据此判「模型没返回内容」）");

var fenced = NoteTidyPrompt.CleanResult("```markdown\n- 第一条\n- 第二条\n```");
Check(fenced == "- 第一条\n- 第二条",
      "整体包裹的代码块围栏必须剥掉（否则笔记里多出 ``` 行）",
      $"实际「{Cut(fenced)}」");

Check(NoteTidyPrompt.CleanResult("好的，以下是整理后的内容：\n- 第一条") == "- 第一条",
      "开场白必须剥掉", $"实际「{Cut(NoteTidyPrompt.CleanResult("好的，以下是整理后的内容：\n- 第一条"))}」");

Check(NoteTidyPrompt.CleanResult("以下是整理后的内容：\n好的：\n- 第一条") == "- 第一条",
      "连续多行开场白要一路剥干净（回包常带两层客套）");

var keepHead = NoteTidyPrompt.CleanResult("会议纪要：讨论了排期\n- 第一条");
Check(keepHead == "会议纪要：讨论了排期\n- 第一条",
      "用户正文的首行必须原样保留 —— 清洗只认「以下是 / 整理后 / 好的」这类开场白",
      $"实际「{Cut(keepHead)}」—— 把用户自己的小标题当客套吃掉，等于静默删了他的内容");

var longHead = new string('长', 50) + "整理后的安排：\n- 第一条";
Check(NoteTidyPrompt.CleanResult(longHead) == longHead,
      "超过 40 字符的首行一律不当作开场白（长句是正文，不是客套）");

Check(NoteTidyPrompt.CleanResult("  只有一段话，没有围栏也没有开场白。  ") == "只有一段话，没有围栏也没有开场白。",
      "既没有围栏也没有开场白时，原样返回（清洗不许画蛇添足）");

// 提取待办的回包解析（2026-09-27）：解析错一行 = 静默少建一条待办或建出一条带时间戳文字的待办
var todos = NoteTidyPrompt.ParseTodoLines(
    "【2026-09-30 14:05】把方案发到项目群\n【2026-10-01】给供应商回电话\n- 1. 明天买打印纸\n\n");
Check(todos.Count == 3, "逐行解析：非空行一条一个，空行跳过", $"实际 {todos.Count} 条");
Check(todos[0].Text == "把方案发到项目群" && todos[0].Due == new DateTime(2026, 9, 30, 14, 5, 0),
      "「【日期 时间】内容」→ 内容 + 当天该时刻", $"实际「{todos[0].Text}」@{todos[0].Due:yyyy-MM-dd HH:mm}");
Check(todos[1].Text == "给供应商回电话" && todos[1].Due == new DateTime(2026, 10, 1),
      "只有日期没有钟点 → 当天 00:00（创建待办时由此走「按日期不问钟点」的正常链路）");
Check(todos[2].Text == "明天买打印纸" && todos[2].Due == null,
      "没有【】的行 = 纯文字、无截止时间；行首的序号 / 分点符号（模型不守格式时）要剥掉",
      $"实际「{todos[2].Text}」");

Check(NoteTidyPrompt.IsNoTodoMarker("未发现待办") && !NoteTidyPrompt.IsNoTodoMarker("未发现待办事项"),
      "「未发现待办」空态标记必须精确匹配 —— 误判会把这句话建成一条待办");

Check(NoteTidyPrompt.ParseTodoLines("").Count == 0 && NoteTidyPrompt.ParseTodoLines(null).Count == 0,
      "空回包解析出 0 条（调用方据此退回纯文本展示、创建待办置灰）");

// ── phase2（2026-09-27）：占位符剥除 / 时间兜底 / Markdown 机械清洗 ──
// 占位符照抄是实测发生过的（原文带「【yyyy-MM-dd】」模型原样抄回来），不兜底 = 待办标题永远带脏前缀
var phTodos = NoteTidyPrompt.ParseTodoLines("【yyyy-MM-dd】实现点击规则重跑\n【YYYY-MM-DD HH:mm】给供应商回电话");
Check(phTodos.Count == 2 && phTodos[0].Text == "实现点击规则重跑" && phTodos[0].Due == null
      && phTodos[1].Text == "给供应商回电话" && phTodos[1].Due == null,
      "行首字面量占位「【yyyy-MM-dd】」必须剥掉：不当日期解析、也不留在待办文字里",
      $"实际「{phTodos[0].Text}」@{phTodos[0].Due:yyyy-MM-dd HH:mm}");

var fbTodos = NoteTidyPrompt.ParseTodoLines("明天买打印纸\n买牛奶",
    t => t.Contains("明天") ? new DateTime(2026, 10, 2, 10, 0, 0) : null);
Check(fbTodos[0].Due == new DateTime(2026, 10, 2, 10, 0, 0) && fbTodos[0].Text == "明天买打印纸",
      "timeFallback 兜底：模型漏写【】时由注入的解析器补截止时间（文字原样，剥离交给提示词）",
      $"实际 {fbTodos[0].Due:yyyy-MM-dd HH:mm}");
Check(fbTodos[1].Due == null, "fallback 认不出的行保持无截止时间（不强填）");

var normalized = NoteTidyPrompt.NormalizePlainFormatting("**标题**\n- 甲\n  * 乙\n1. 丙");
Check(normalized == "标题\n· 甲\n  · 乙\n1. 丙",
      "Markdown 机械清洗：** 去星号、行首 -/*/• 转「· 」（缩进保留）、正常序号不动",
      $"实际「{Cut(normalized)}」");
Check(NoteTidyPrompt.NormalizePlainFormatting("") == "" && NoteTidyPrompt.NormalizePlainFormatting(null) == "",
      "空回包清洗后仍是空串");
Check(NoteTidyPrompt.OutputDiscipline.Contains("不用 ** 加粗") && NoteTidyPrompt.OutputDiscipline.Contains("不用 - 或 *"),
      "输出纪律必须明令禁用 Markdown 记号（结果区是纯 TextBox，** 与 - 展示出来就是裸符号）");
Check(NoteTidyPrompt.ExtractTodoInstruction.Contains("顺延一年") && NoteTidyPrompt.ExtractTodoInstruction.Contains("绝不允许照抄"),
      "提取待办指令必须写死年份补全规则（未到补当年、已过顺延一年）并禁止照抄占位符");

Mark("[13]-[17] AI 解析 / 裁剪 / 搜索匹配 / 整理清洗");

// ── [18] 界面快照的按需出图契约（2026-09-26）──
// 起因两条，都是「开发工具自己的坑」：
//   ① 45 个场景全量出图约 1 分钟，改一个板块时大半的图白跑 —— 而按需出图**光加过滤是不够的**：
//      场景名与代码板块的映射只活在 UiSnapshot.cs 的注释里，纯子串匹配会静默漏图
//      （改「AI 功能」板块时 03d-设置-AI 问答界面 名字里根本没这四个字）→ 所以必须配 --list 拿清单。
//   ② 脚本自己打印的建议「对比法最有效（改动前后各跑一次）」根本执行不了：
//      每次 snap 都清空输出目录，第二遍跑完基线图就没了 —— 建议与实现互相打脸。
// 四条断言读源码守（与 [11] 里那条 DueTimeDialog 场景断言同一风格）：
Console.WriteLine("[18] 界面快照的按需出图契约");

Check(snapCs.Contains("--only") && snapCs.Contains("--list"),
      "界面快照必须支持按需出图（--only 过滤 + --list 清单）",
      "UiSnapshot.cs 里找不到 --only / --list —— 又退回「只能 45 张全量」了");

Check(snapCs.Contains("_matched == 0") && snapCs.Contains("Shutdown(exitCode)"),
      "「--only 一个都没匹配」必须失败退出（退出码 2），不许安静地出 0 张图",
      "找不到未匹配即失败的逻辑 —— 按需出图的致命失败是「漏了图却不知道」，"
      + "安静地出 0 张等于把整个判读环节骗过去，比全量慢危险得多");

var devPs1Path = Path.Combine(uiRepoRoot, "tools", "dev.ps1");
var devPs1 = File.Exists(devPs1Path) ? File.ReadAllText(devPs1Path) : "";
var snapIdx = devPs1.IndexOf("function Invoke-Snap", StringComparison.Ordinal);
var nextFnIdx = snapIdx >= 0 ? devPs1.IndexOf("\nfunction ", snapIdx + 1, StringComparison.Ordinal) : -1;
var snapBody = snapIdx >= 0 && nextFnIdx > snapIdx ? devPs1.Substring(snapIdx, nextFnIdx - snapIdx) : "";
Check(snapBody.Contains("Invoke-External 'dotnet'"),
      "snap 出图前必须先增量编译（原实现「有 exe 就直接用」拍到的是旧二进制的画面：时间戳新、内容旧）",
      snapIdx < 0
          ? "dev.ps1 里找不到 Invoke-Snap 函数"
          : "Invoke-Snap 里没有编译动作 —— 改完代码直接 snap 会出旧图，且按需出图让它更隐蔽");

Check(snapBody.Contains("yyyyMMdd-HHmmss"),
      "snap 出图必须落在带时间戳的子目录（否则每次清空，改动前后的基线图无法并排对比）",
      "Invoke-Snap 里找不到时间戳目录命名（yyyyMMdd-HHmmss）");

// 这一条守的是两个**只在 PowerShell 层才会发生**的静默错误（2026-09-26 两个都实测踩到了）：
//   ① `-Only 03b,03c` 不加引号 → 逗号被当数组分隔符 → 绑给 [string] 直接参数绑定失败：
//      脚本压根不执行、**零输出**，而 $LASTEXITCODE 还保留着上一次的值（看着像成功）。
//   ② `-Only 03d` 不加引号 → `d` 是 PowerShell 的 decimal 数字后缀 → 值变成「3」，
//      照样匹配得到一堆无关场景（13/23/30 里都有 3），脚本报「已生成 N 张图」成功退出。
// 两条都是「照文档写反而静默出错」，所以参数类型与示例引号都得钉死。
Check(devPs1.Contains("[string[]]$Only") && devPs1.Contains("-Only '"),
      "dev.ps1 的 snap -Only：参数必须收 [string[]]、示例必须带引号",
      "改回 [string] 或丢掉引号 —— 照文档写 `-Only 03d,10e` 会静默出错（参数绑定失败零输出 / "
      + "03d 被 decimal 后缀吃成 3 却报成功）");

Mark("[18] 界面快照的按需出图契约");

// ── [19] 云端核对判定 CloudVerify（2026-09-27）──
// 这块错了的后果**全是静默的**：漏标 → 用户以为文件还在网盘上，取回时才发现是空的；
// 误标 → 明明还在后台上传的文件被说成"云端已不存在"，用户白折腾一轮。两种都不报错、只发作。
// 判定表逐条对应 docs/2026-09-27-云端核对方案实施清单.md §5.2（含 2026-09-27 实机修正）。
Console.WriteLine("[19] 云端核对判定 CloudVerify");

var vNow = new DateTime(2026, 9, 27, 12, 0, 0);
var vOpts = new CloudVerifyOptions { Now = vNow, FreshWindow = TimeSpan.FromHours(1) };
var vOld = vNow.AddDays(-1);
var vFresh = vNow.AddMinutes(-5);

LocalRecord LR(string id, string path, bool deleted = false, string state = "",
               bool hasLedger = false, bool uploaded = false, DateTime? created = null)
    => new()
    {
        Id = id, NetPath = path, Deleted = deleted, CloudState = state,
        HasLedgerEntry = hasLedger, WasUploaded = uploaded, CreatedAt = created ?? vOld,
    };
CloudEntry CE(string path) => new() { Path = path };

var v1 = CloudVerify.Compare(
    new[] { LR("a", "/apps/F/files/1.md", hasLedger: true, uploaded: true) },
    new[] { CE("/apps/F/files/1.md") }, vOpts);
Check(v1.Hit == 1 && v1.MarkMissing.Count == 0,
      "本地有 + 云端有 → 命中，不标注",
      $"命中 {v1.Hit} / 待标 {v1.MarkMissing.Count}");

var v2 = CloudVerify.Compare(
    new[] { LR("a", "/apps/F/files/1.md", hasLedger: true, uploaded: true) },
    Array.Empty<CloudEntry>(), vOpts);
Check(v2.MarkMissing.Count == 1 && v2.MarkMissing[0] == "a",
      "本地有 + 云端无 + 账本记过上传成功 → 标「云端已不存在」（最初要修的就是这种失真）",
      $"待标 {v2.MarkMissing.Count} 条");

var v3 = CloudVerify.Compare(
    new[] { LR("a", "/apps/F/files/1.md", hasLedger: true, uploaded: false) },
    Array.Empty<CloudEntry>(), vOpts);
Check(v3.MarkMissing.Count == 0 && v3.SkipNotUploaded == 1,
      "本地有 + 云端无 + 账本存在但没传成功 → 不标注（云端本该就没有）",
      $"待标 {v3.MarkMissing.Count} / 跳过·未上传 {v3.SkipNotUploaded}");

var v4 = CloudVerify.Compare(
    new[] { LR("a", "/apps/F/files/1.md", hasLedger: false, created: vOld) },
    Array.Empty<CloudEntry>(), vOpts);
Check(v4.MarkMissing.Count == 1,
      "本地有 + 云端无 + 无账本证据但记录够旧 → 标（2026-09-27 修正规则：实机测得 61% 的失真落在这里，"
      + "原规则「无账本一律不标」会把它们全部漏掉）",
      $"待标 {v4.MarkMissing.Count} 条");

var v5 = CloudVerify.Compare(
    new[] { LR("a", "/apps/F/files/1.md", hasLedger: false, created: vFresh) },
    Array.Empty<CloudEntry>(), vOpts);
Check(v5.MarkMissing.Count == 0 && v5.SkipTooFresh == 1,
      "本地有 + 云端无 + 记录很新（安全窗口内）→ 不标注（防「他端刚传、清单先到、云端还在收」）",
      $"待标 {v5.MarkMissing.Count} / 跳过·太新 {v5.SkipTooFresh}");

var v6 = CloudVerify.Compare(
    new[] { LR("a", "/apps/F/files/1.md", deleted: true, hasLedger: true, uploaded: true) },
    Array.Empty<CloudEntry>(), vOpts);
Check(v6.MarkMissing.Count == 0 && v6.SkipTombstone == 1, "墓碑记录 → 跳过（用户已彻底删除）");

var v7 = CloudVerify.Compare(
    new[] { LR("a", "/apps/F/attachments/2026-09/1.jpg", state: "expired", hasLedger: true, uploaded: true) },
    Array.Empty<CloudEntry>(), vOpts);
Check(v7.MarkMissing.Count == 0 && v7.SkipCloudExpired == 1,
      "云端已到期清理 → 跳过（如期清理，不是意外消失）");

var v8 = CloudVerify.Compare(
    new[] { LR("a", "/apps/F/files/1.md", state: "cleanup-pending", hasLedger: true, uploaded: true) },
    Array.Empty<CloudEntry>(), vOpts);
Check(v8.MarkMissing.Count == 0 && v8.SkipCloudPending == 1, "云端待清理 → 跳过（交给清理器补删）");

var v9 = CloudVerify.Compare(
    new[] { LR("a", "/apps/F/files/1.md", hasLedger: true, uploaded: true) },
    new[] { CE("/apps/F/files/1.md"), CE("/apps/F/files/other.md") }, vOpts);
Check(v9.CloudOnly.Count == 1 && v9.CloudOnly[0] == "/apps/F/files/other.md" && v9.MarkMissing.Count == 0,
      "云端有 + 本地无记录 → 只进「云端多出」报告，不产生任何标注（绝不自动入账）",
      $"云端多出 {v9.CloudOnly.Count} 条");

var v10 = CloudVerify.Compare(
    new[] { LR("a", "/apps/F/files/1.md/", hasLedger: true, uploaded: true) },
    new[] { CE("/apps/F/files/1.md") }, vOpts);
Check(v10.Hit == 1, "路径尾部斜杠差异 → 规范化后视为命中");

var v11 = CloudVerify.Compare(
    new[] { LR("a", "/apps/F/files/ABC.md", hasLedger: true, uploaded: true) },
    new[] { CE("/apps/F/files/abc.md") }, vOpts);
Check(v11.Hit == 0 && v11.MarkMissing.Count == 1,
      "路径大小写不同 → 视为**不**命中（百度网盘路径大小写敏感）",
      $"命中 {v11.Hit} / 待标 {v11.MarkMissing.Count}");

var v12 = CloudVerify.Compare(
    new[] { LR("a", "/apps/F/files/1.md", state: "missing", hasLedger: true, uploaded: true) },
    new[] { CE("/apps/F/files/1.md") }, vOpts);
Check(v12.ClearMissing.Count == 1 && v12.ClearMissing[0] == "a",
      "此前标过「云端已不存在」、这次核对发现云端又有了 → 撤销标注",
      $"待撤销 {v12.ClearMissing.Count} 条");

Mark("[19] 云端核对判定 CloudVerify");

// ── [20] 会话列表展示规则 ChatListRules（2026-10-01）──
// 这块错了的后果**全是静默的**：
//   · 只贴文件不打字的会话，列表上变成一行**没有文字**的条目（用户点进去才知道是哪个文件）；
//   · 分组指令按钮的文案与点击行为对不上（写着「查看指令」点下去却是别的动作）。
// 两者都不报错，只让用户觉得"功能坏了" —— 所以必须有断言守着。
Console.WriteLine("[20] 会话列表展示规则 ChatListRules");

Check(ChatListRules.BuildPreview("", "", new[] { "报告.pdf" }) == "文件：报告.pdf",
      "只发文件不打字 → 标题回退「文件：<文件名>」（2026-10-01 用户实测的空标题根因）",
      $"实际「{ChatListRules.BuildPreview("", "", new[] { "报告.pdf" })}」");

Check(ChatListRules.BuildPreview("", "", new[] { "a.png", "b.png", "c.png" }) == "文件：a.png 等 3 个",
      "多个附件 → 「文件：第一个 等 N 个」（不逐条罗列，列表标题要短）",
      $"实际「{ChatListRules.BuildPreview("", "", new[] { "a.png", "b.png", "c.png" })}」");

Check(ChatListRules.BuildPreview("我改过的标题", "", new[] { "报告.pdf" }) == "我改过的标题",
      "重命名过的标题优先级最高（不能被附件名盖掉）");

Check(ChatListRules.BuildPreview("", "用户打的字", new[] { "报告.pdf" }) == "用户打的字",
      "有正文时用正文，不走附件名兜底");

Check(ChatListRules.BuildPreview("", "", null) == "",
      "既无标题、也无正文、也无附件 → 仍返回空串（兜底文案「（空会话）」由 UI 层负责，不在服务层硬编码）");

var longUserText = new string('字', 60);
var longPreview = ChatListRules.BuildPreview("", longUserText, null);
Check(longPreview.Length == 41 && longPreview.EndsWith("…"),
      "正文超 40 字截断并加省略号（40 字 + 省略号 1 字）",
      $"长度 {longPreview.Length}、结尾「{(longPreview.Length > 0 ? longPreview[^1] : ' ')}」");

Check(ChatListRules.InstructionButtonText(false, false) == "添加指令",
      "没有指令 → 按钮文案「添加指令」（点了直接进编辑弹窗）");
Check(ChatListRules.InstructionButtonText(true, false) == "查看指令",
      "已有指令且收起 → 「查看指令」（点了展开只读区）");
Check(ChatListRules.InstructionButtonText(true, true) == "收起指令",
      "已有指令且展开 → 「收起指令」（否则按钮写着「查看」其实已经展开，再点像没反应）");

Mark("[20] 会话列表展示规则 ChatListRules");

Console.WriteLine();
Console.WriteLine($"=== 各组耗时（按耗时降序）| 纯逻辑集 | 墙钟 {swTotal.ElapsedMilliseconds} ms | 测量段之和 {groupMs.Sum(g => g.Ms)} ms ===");
foreach (var g in groupMs.OrderByDescending(x => x.Ms))
{
    Console.WriteLine($"  {g.Ms,6} ms | {g.Name}");
}

// ★ 本层必须真的快 —— 防腐化的第二道闸（第一道是 csproj）
// 判据刻意用「跑出来的结果」而不是「源码文本长什么样」：本项目一贯判据是
// 「能用产物证明的，就不信声明的字面」。谁把慢操作加回本层（起子进程 / 开网络 /
// 建真实文件树 / 写系统剪贴板），这条立刻变红，不需要人去读代码 —— 这正是本次
// 「快层实测 49.3 秒而文档写秒级、整整腐化了没人发现」要防的事。
// 两道闸的分工：csproj 管**事前**（那些类的源文件已不在本工程编译清单里，想用根本编译不过），
// 这条管**事后**（万一用别的办法绕过了 csproj）。物理分层后本层实测 ~0.65 秒，阈值留 3 秒。
Check(swTotal.ElapsedMilliseconds < 3000,
      "本层（纯逻辑集）必须在 3 秒内跑完",
      $"实际 {swTotal.ElapsedMilliseconds} ms —— 本层被塞进了慢操作："
      + "按分桶规则应搬到慢层（tests/sync/Program.OutOfScope.cs）");

Console.WriteLine();
Console.WriteLine($"===== {pass} 项通过，{fail} 项失败 =====");
return fail == 0 ? 0 : 1;
