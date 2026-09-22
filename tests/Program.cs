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

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FocusCapture;                 // FocusCapturePaths（数据根，候选目录里的"数据目录"一档取自它）
using FocusCapture.Services;
using FocusCapture.Services.Skills;
using FocusCapture.Services.Sync;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

// ── 子进程模式（只供下面 [8] 组当被测子进程用）──
// 拿自己这个可执行文件当「说完就挂住 / 说完就退出」的被测进程：不依赖 Python 运行时、
// 不联网、不碰任何用户数据，跑在哪儿都成立。
// 必须放在最前面 —— 子进程进来要是一路把全量检查点跑完，那一次就白等几十秒。
if (args.Contains("--child-echo"))
{
    Console.WriteLine("ECHO_OK");
    Console.Out.Flush();
    return 0;
}
if (args.Contains("--child-hold"))
{
    Console.WriteLine("KEEP_ME");
    Console.Out.Flush();
    Thread.Sleep(30_000);   // 挂住等父进程超时把它杀掉（"输出完就卡住"的最小复现）
    return 3;
}

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

// ── [5] 剪贴板写入容错 SafeClipboard ──
// 用户可见性质：剪贴板被其他程序占用时，复制动作**不得弹出界面错误框**。
// 真实事故（2026-09-18）：灵感速览面板「单击复制」未捕获 CLIPBRD_E_CANT_OPEN，
// 异常冒泡到全局处理弹模态框；双击笔记时第一下先走复制 → 弹框吃掉第二下点击
// → 用户表现「双击笔记进不了编辑状态」。
Console.WriteLine("[5] 剪贴板写入容错 SafeClipboard");

var busyAttempts = 0;
var busySleeps = new List<int>();
var recovered = SafeClipboard.TrySetText("内容", _ =>
{
    busyAttempts++;
    if (busyAttempts < 3) throw new COMException("OpenClipboard 失败", unchecked((int)0x800401D0));
}, 3, 25, null, ms => busySleeps.Add(ms));

Check(recovered, "前两次被占用、第三次可用时必须返回 true（临时占用不得被当成失败）");
Check(busyAttempts == 3, "临时占用必须重试到成功（共 3 次尝试）", $"实际尝试 {busyAttempts} 次");
Check(busySleeps.SequenceEqual(new[] { 25, 50 }),
      "重试间隔必须指数退避 25/50ms（不退避会在几十毫秒内烧完全部次数）",
      $"实际间隔 {string.Join("/", busySleeps)}ms");

var alwaysBusyAttempts = 0;
var threwOut = false;
var allBusyResult = true;
try
{
    allBusyResult = SafeClipboard.TrySetText("内容", _ =>
    {
        alwaysBusyAttempts++;
        throw new COMException("剪贴板被占用", unchecked((int)0x800401D0));
    }, 3, 0, null, _ => { });
}
catch { threwOut = true; }

Check(!threwOut, "始终被占用时**不得抛异常**（抛出去就是那个界面错误框，双击手势会被吃掉）");
Check(!allBusyResult, "始终被占用时必须返回 false，好让调用方给出提示而不是假装成功");
Check(alwaysBusyAttempts == 3, "耗尽尝试次数后必须停止重试", $"实际尝试 {alwaysBusyAttempts} 次");

var hardFailAttempts = 0;
var nonTransient = SafeClipboard.TrySetText("内容", _ =>
{
    hardFailAttempts++;
    throw new InvalidOperationException("调用线程不是 STA");
}, 5, 0, null, _ => { });
Check(!nonTransient && hardFailAttempts == 1,
      "确定性失败（非剪贴板占用类异常）不得重试 —— 重试也不可能成功",
      $"实际尝试 {hardFailAttempts} 次");

var wroteAnything = false;
var emptyResult = SafeClipboard.TrySetText("", _ => wroteAnything = true);
var nullResult = SafeClipboard.TrySetText(null, _ => wroteAnything = true);
Check(!emptyResult && !nullResult && !wroteAnything,
      "空内容 / null 不得写入剪贴板（Clipboard.SetText 对空串会抛参数异常）");

var normalSleeps = 0;
var normalResult = SafeClipboard.TrySetText("正常内容", _ => { }, 3, 25, null, _ => normalSleeps++);
Check(normalResult && normalSleeps == 0, "首次即成功时不得有任何退避等待（正常路径零额外延迟）");

Check(SafeClipboard.DefaultAttempts == 3 && SafeClipboard.DefaultBaseDelayMs == 25,
      "默认参数必须是 3 次尝试 / 25ms 退避基数（改动会同时改变所有调用点行为）",
      $"实际 {SafeClipboard.DefaultAttempts} 次 / {SafeClipboard.DefaultBaseDelayMs}ms");

// ── [6] Skill 目录扫描与解析 SkillCatalog ──
// 坏了的表现：装的 Skill 在对话里"看不见"（清单空）/ 中文说明变乱码 / 一个畸形文件把整列表搞没。
// 本组是纯逻辑（解析 + 扫描 + 清单拼装），所以能留在秒级的快层；
// 执行器 SkillScriptRunner 依赖 AppLog，只能进慢层（见 tests/sync）。
Console.WriteLine("[6] Skill 目录扫描与解析 SkillCatalog");

// 6.1 frontmatter 解析（直接调解析方法，不经文件系统）
var fmOk = SkillCatalog.ParseFrontmatter(
    "---\nname: demo-skill\ndescription: 一个示例说明\n---\n\n# 正文标题\n正文内容\n");
Check(fmOk.Name == "demo-skill" && fmOk.Description == "一个示例说明" && fmOk.Body.Contains("正文内容"),
      "标准 frontmatter 必须解析出 name / description / 正文",
      $"实际 name=「{Cut(fmOk.Name)}」 desc=「{Cut(fmOk.Description)}」");

var fmNone = SkillCatalog.ParseFrontmatter("# 只有标题\n正文\n");
Check(fmNone.Name == null && fmNone.Description == null && fmNone.Body.Contains("只有标题"),
      "没有 frontmatter 时不得抛异常，且正文要完整保留",
      $"实际 name=「{Cut(fmNone.Name)}」");

var fmOpen = SkillCatalog.ParseFrontmatter("---\nname: broken\n从未闭合\n");
Check(fmOpen.Name == null && fmOpen.Body.Contains("从未闭合"),
      "frontmatter 未闭合时按「没有 frontmatter」处理，不得把正文吞掉");

var fmQuote = SkillCatalog.ParseFrontmatter(
    "---\nname: \"中文名称\"\ndescription: '带单引号的说明：记一下，别搞乱'\n---\n正文");
Check(fmQuote.Name == "中文名称" && fmQuote.Description == "带单引号的说明：记一下，别搞乱",
      "带引号的值必须脱掉引号，中文与标点不得被破坏",
      $"实际 name=「{Cut(fmQuote.Name)}」 desc=「{Cut(fmQuote.Description)}」");

var fmMulti = SkillCatalog.ParseFrontmatter("---\nname: multi\ndescription: >\n  第一行\n  第二行\n---\n正文");
Check(fmMulti.Description == "第一行 第二行",
      "YAML 多行标量（>）必须拼成一行，不能把 > 本身当成描述",
      $"实际「{Cut(fmMulti.Description)}」");

// 6.2 真实目录扫描
var skillTmp = Path.Combine(Path.GetTempPath(), "fc-skillcheck-" + Guid.NewGuid().ToString("N")[..8]);
var skillTmpMissing = Path.Combine(Path.GetTempPath(), "fc-skillcheck-missing-" + Guid.NewGuid().ToString("N")[..8]);
try
{
    Directory.CreateDirectory(skillTmp);

    void WriteSkill(string dirName, string content)
    {
        var d = Path.Combine(skillTmp, dirName);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "SKILL.md"), content, new UTF8Encoding(false));
    }

    WriteSkill("normal-skill", "---\nname: 正常技能\ndescription: 正常描述\n---\n正文");
    WriteSkill("dir-name-fallback", "---\ndescription: 没有 name 字段\n---\n正文");
    WriteSkill("no-desc", "---\nname: 无描述技能\n---\n\n# 从正文首行取描述\n其余内容");
    WriteSkill("飞书知识库", "---\nname: 飞书知识库\ndescription: 中文目录名\n---\n正文");
    WriteSkill("broken-skill", "这不是 frontmatter，只是一段普通文本");
    WriteSkill("long-desc", "---\nname: 长描述\ndescription: " + new string('长', 500) + "\n---\n正文");
    Directory.CreateDirectory(Path.Combine(skillTmp, "not-a-skill"));   // 没有 SKILL.md

    var catalog = new SkillCatalog(skillTmp);
    var skills = catalog.GetSkills();

    Check(skills.Count == 6,
          "有 SKILL.md 的目录都要扫到，没有的要跳过",
          $"期望 6 个，实际 {skills.Count} 个：{string.Join("、", skills.Select(s => s.DirectoryName))}");

    Check(!skills.Any(s => s.DirectoryName == "not-a-skill"),
          "没有 SKILL.md 的目录不得被当成 Skill（用户会在 Skills\\ 下放自己的杂物）");

    var sNormal = skills.FirstOrDefault(s => s.DirectoryName == "normal-skill");
    Check(sNormal?.Name == "正常技能", "frontmatter 里的 name 优先于目录名", $"实际「{Cut(sNormal?.Name)}」");

    var sFallback = skills.FirstOrDefault(s => s.DirectoryName == "dir-name-fallback");
    Check(sFallback?.Name == "dir-name-fallback",
          "frontmatter 缺 name 时用目录名兜底（否则这个 Skill 会从清单里静默消失）",
          $"实际「{Cut(sFallback?.Name)}」");

    var sNoDesc = skills.FirstOrDefault(s => s.DirectoryName == "no-desc");
    Check(sNoDesc != null && sNoDesc.Description.Contains("从正文首行取描述"),
          "frontmatter 缺 description 时取正文首个非空行",
          $"实际「{Cut(sNoDesc?.Description)}」");

    Check(skills.Any(s => s.Name == "飞书知识库"),
          "中文 Skill 目录名必须能正常扫描（用户很可能用中文命名）");

    var sBroken = skills.FirstOrDefault(s => s.DirectoryName == "broken-skill");
    Check(sBroken != null && sBroken.Name == "broken-skill",
          "畸形 SKILL.md 不得让整次扫描失败 —— 它自己降级为「用目录名」，其他 Skill 照常出清单");

    var sLong = skills.FirstOrDefault(s => s.DirectoryName == "long-desc");
    Check(sLong != null && sLong.Description.Length < 300 && sLong.Description.Contains("省略"),
          "超长 description 必须截断（否则装几个长说明的 Skill 就会把每轮上下文撑爆）",
          $"实际长度 {sLong?.Description.Length}");

    // 6.3 缓存
    var c1 = catalog.GetSkills();
    var c2 = catalog.GetSkills();
    Check(ReferenceEquals(c1, c2), "目录没变时必须命中缓存（否则每轮对话都要重扫一遍目录）");

    // 6.4 Skills 目录不存在（首次启动就是这个状态）
    var missingCatalog = new SkillCatalog(skillTmpMissing);
    Check(missingCatalog.GetSkills().Count == 0,
          "Skills 目录不存在时必须返回空清单且不抛异常");

    // 6.5 清单拼装
    Check(SkillManifest.Build(new List<SkillInfo>(), true).Contains("没有安装任何 Skill"),
          "一个 Skill 都没有时，清单必须明说「没有」，避免模型自行脑补能力");

    var fakeMany = Enumerable.Range(0, 300)
        .Select(i => new SkillInfo($"skill-{i}", $"skill-{i}", skillTmp,
                                   new string('描', 180), "body", new List<string> { "a.py" }, ""))
        .ToList();
    var bigManifest = SkillManifest.Build(fakeMany, true);
    Check(bigManifest.Length <= SkillManifest.MaxChars + 300,
          "Skill 再多，清单也必须封顶（否则每轮请求的上下文会爆炸）",
          $"实际 {bigManifest.Length} 字符，上限 {SkillManifest.MaxChars}");

    Check(SkillManifest.Build(fakeMany.Take(1).ToList(), false).Contains("缺少运行时"),
          "运行时缺失时清单必须如实标注，不能假装可执行");
}
finally
{
    try { Directory.Delete(skillTmp, true); } catch { }
}

Console.WriteLine();
Console.WriteLine("[7] 运行时部件候选目录 SkillRuntimeLocations");

// 守的是什么（2026-09-21 新增）：运行时部件（lark-cli / 内置 Python）有「自带」与「数据目录
// （按需下载）」两处来源，找的顺序必须固定且可预期。顺序错了的后果是**静默的** ——
// 照样跑得起来，但跑的是旧的那一份，这类"看着对其实错"最难查，所以由检查点守。
// 本组会把数据根临时指到沙箱（候选里的"数据目录"一档取自 FocusCapturePaths.Root），跑完还原。
const string skillId = "lark-cli";
var locTmp = Path.Combine(Path.GetTempPath(), "fc-tests-runtimeloc-" + Guid.NewGuid().ToString("N")[..8]);
var locOldRoot = FocusCapturePaths.RootOverride;   // 记住原值：跑完还原，别影响后面的组
FocusCapturePaths.RootOverride = locTmp;
try
{
    var appDir = Path.Combine(locTmp, "app");                 // 假装的应用目录
    var locBundled = Path.Combine(appDir, "runtime", skillId); // 自带那一档
    var locData = SkillRuntimeLocations.DataDir(skillId);      // 数据目录那一档

    // 7.1 顺序：自带 → 数据目录
    var cands = SkillRuntimeLocations.CandidateDirs(appDir, skillId);
    Check(cands.Count == 2 &&
          string.Equals(cands[0], locBundled, StringComparison.OrdinalIgnoreCase) &&
          string.Equals(cands[1], locData, StringComparison.OrdinalIgnoreCase),
          "候选顺序必须是「自带 → 数据目录」",
          $"实际：{string.Join(" | ", cands)}");

    // 7.2 数据目录取自数据根，不是写死的 %AppData%
    Check(locData.StartsWith(locTmp, StringComparison.OrdinalIgnoreCase),
          "数据目录必须来自数据根（写死 %AppData% 会出现「下载到 A、使用找 B」）",
          $"实际：{locData}");

    // 7.3 两处都没有 → null，不抛也不猜
    Check(SkillRuntimeLocations.FindInCandidates(appDir, skillId, "lark-cli") is null,
          "候选目录都没有该文件时必须返回 null（不抛异常、也不乱猜一个路径出来）");

    // 7.4 只有数据目录有 → 命中数据目录（按需下载之后的常态）
    Directory.CreateDirectory(locData);
    File.WriteAllText(Path.Combine(locData, "lark-cli.exe"), "stub");
    var hitData = SkillRuntimeLocations.FindInCandidates(appDir, skillId, "lark-cli");
    Check(hitData != null && string.Equals(Path.GetDirectoryName(hitData), locData, StringComparison.OrdinalIgnoreCase),
          "自带没有、数据目录有时，必须命中数据目录那一份（否则按需下载等于白下）",
          $"实际：{hitData}");

    // 7.5 两处都有 → 必须命中自带那一份（证明顺序真的生效，不是"哪个存在就返回哪个"）
    Directory.CreateDirectory(locBundled);
    File.WriteAllText(Path.Combine(locBundled, "lark-cli.exe"), "stub");
    var hitBundled = SkillRuntimeLocations.FindInCandidates(appDir, skillId, "lark-cli");
    Check(hitBundled != null && string.Equals(Path.GetDirectoryName(hitBundled), locBundled, StringComparison.OrdinalIgnoreCase),
          "两处都有时必须命中自带的那一份（自带优先，顺序不能反）",
          $"实际：{hitBundled}");

    // 7.6 拿不到应用目录时不抛，候选退化为数据目录一档
    var noBase = SkillRuntimeLocations.CandidateDirs(null, skillId);
    Check(noBase.Count == 1 && string.Equals(noBase[0], locData, StringComparison.OrdinalIgnoreCase),
          "拿不到应用目录时候选应退化为只剩数据目录，且不抛异常",
          $"实际：{string.Join(" | ", noBase)}");

    // 7.7 自带与数据目录重合（应用恰好装在数据根里）时不重复前置同一个目录
    var overlapped = SkillRuntimeLocations.CandidateDirs(locTmp, skillId);
    Check(overlapped.Count == 1, "自带目录与数据目录重合时不应重复列出", $"实际 {overlapped.Count} 条");
}
finally
{
    FocusCapturePaths.RootOverride = locOldRoot;
    try { Directory.Delete(locTmp, true); } catch { }
}

// ── [8] 子进程流式读与超时保留 SkillProcess ──
// 守的是什么（2026-09-21 新增）：「输出完就卡住」是子进程真实存在的形态 ——
// lark-cli 的 `config init --new` 就是这样：启动约 1 秒把验证链接吐完（走 stderr），
// 然后一直等使用者在浏览器里把应用建完才退出。
// 旧实现按「进程退出后才读输出」写（ReadToEndAsync），于是链接明明已经出来了却永远读不到；
// 超时分支还把已读内容丢成空字符串 —— 等于把唯一的证据也扔了。
// 本组拿「自己这个 exe」当被测子进程，两端都可控。
Console.WriteLine();
Console.WriteLine("[8] 子进程流式读与超时保留 SkillProcess");

var selfExe = Environment.ProcessPath;
Check(!string.IsNullOrEmpty(selfExe) && File.Exists(selfExe),
      "应能拿到自身的可执行文件路径（本组要拿它当被测子进程）", $"实际：{selfExe}");

if (!string.IsNullOrEmpty(selfExe) && File.Exists(selfExe))
{
    // 8.1 超时保留已读：进程「说完就挂住」时，已经读到的 stdout 不许丢
    var holdPsi = SkillProcess.Build(selfExe!, new[] { "--child-hold" }, Path.GetTempPath(), false);
    var rHeld = await SkillProcess.RunAsync(holdPsi, null, 1500);
    Check(rHeld.TimedOut, "该子进程应当是在超时点被杀掉的（否则本组前提不成立）",
          $"实际 TimedOut={rHeld.TimedOut}，exit={rHeld.ExitCode}");
    Check(rHeld.Stdout.Contains("KEEP_ME"),
          "超时被杀时，已经读到的 stdout 必须原样交出来（旧实现丢弃成空字符串）",
          $"实际 stdout：{Cut(rHeld.Stdout)}");

    // 8.2 流式回调：进程还活着的时候就要能收到行，不必等它退出
    var streamed = new List<string>();
    var streamGate = new object();
    var rStream = await SkillProcess.RunAsync(holdPsi, null, 1500, default,
                                              line => { lock (streamGate) streamed.Add(line); });
    Check(streamed.Any(l => l.Contains("KEEP_ME")),
          "行回调必须在进程还活着时就收到输出（等退出才回调 = 阻塞命令永远拿不到链接）",
          $"实际收到 {streamed.Count} 行，TimedOut={rStream.TimedOut}");

    // 8.3 正常路径不许被改坏：退出码与完整输出都要在
    var echoPsi = SkillProcess.Build(selfExe!, new[] { "--child-echo" }, Path.GetTempPath(), false);
    var rEcho = await SkillProcess.RunAsync(echoPsi, null, 20_000);
    Check(rEcho.Ok && rEcho.ExitCode == 0 && rEcho.Stdout.Contains("ECHO_OK"),
          "正常退出的路径必须照旧：退出码 0 + 完整 stdout（改成流式读不能把这条改坏）",
          $"实际 exit={rEcho.ExitCode}，stdout：{Cut(rEcho.Stdout)}");

    // 8.4 启动失败也必须如实返回，不许抛（SkillProcess 的「永不抛」契约）
    var noSuchExe = Path.Combine(Path.GetTempPath(), "fc-no-such-" + Guid.NewGuid().ToString("N")[..6] + ".exe");
    var badPsi = SkillProcess.Build(noSuchExe, new[] { "x" }, Path.GetTempPath(), false);
    var rBad = await SkillProcess.RunAsync(badPsi, null, 2000);
    Check(!rBad.Ok && rBad.StartError != null,
          "启动失败必须走 StartError 返回、不抛异常（调用方要按场景自己措辞）",
          $"实际 StartError：{Cut(rBad.StartError)}");
}

// ── [9] 内置 Skill 落地与恢复 BuiltinSkills ──
// 守的是什么（2026-09-21 新增，授权闭环步骤 4）：
//   ① 内置技能是「目标不存在才复制」—— 一旦落地就归用户，应用不再改动它。
//      这条错了两边都难看：判据写宽了会覆盖用户改过的版本（静默丢东西），
//      判据写窄了该出现的技能永远不出现（用户只会觉得"这功能没生效"）。
//   ② 「恢复内置技能」是覆盖用户改动的破坏性动作，覆盖前必须先把现存版本挪走当备份，
//      而且备份**不能放在 Skills 目录里** —— 那份备份里也有 SKILL.md，
//      会被扫描器当成第二个同名 Skill，清单里凭空多一行。
Console.WriteLine();
Console.WriteLine("[9] 内置 Skill 落地与恢复 BuiltinSkills");

var bsTmp = Path.Combine(Path.GetTempPath(), "fc-tests-builtin-" + Guid.NewGuid().ToString("N")[..8]);
try
{
    var bsSrc = Path.Combine(bsTmp, "builtin-skills");          // 假装的应用目录里那一份
    var bsDst = Path.Combine(bsTmp, "data", "Skills");          // 假装的数据根下的 Skills

    void MakeBuiltin(string name, string skillMd, string? scriptName = null, string scriptBody = "")
    {
        var d = Path.Combine(bsSrc, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "SKILL.md"), skillMd, new UTF8Encoding(false));
        if (scriptName != null)
        {
            Directory.CreateDirectory(Path.Combine(d, "scripts"));
            File.WriteAllText(Path.Combine(d, "scripts", scriptName), scriptBody, new UTF8Encoding(false));
        }
    }

    // 9.1 源目录不存在（精简包/开发机没带内置技能）—— 功能性降级，不是错误
    Check(BuiltinSkills.Deploy(Path.Combine(bsTmp, "no-such-source"), bsDst).Count == 0,
          "源目录不存在时不得抛异常、也不得凭空造目录（没带内置技能是降级，不是错误）");

    MakeBuiltin("lark-cli",
                "---\nname: lark-cli\ndescription: 桥接官方 lark-cli\n---\n正文",
                "lark.py", "print('透传 lark-cli')");
    MakeBuiltin("another-skill", "---\nname: another-skill\ndescription: 第二个内置技能\n---\n正文");
    Directory.CreateDirectory(Path.Combine(bsSrc, "not-a-skill"));   // 源里也可能有杂物

    // 9.2 首次落地：该复制的都复制到位（含 scripts 子目录）
    var bsDeployed = BuiltinSkills.Deploy(bsSrc, bsDst);
    Check(bsDeployed.Count == 2 && bsDeployed.Contains("lark-cli") && bsDeployed.Contains("another-skill"),
          "源里真有 SKILL.md 的技能都要落地（没有 SKILL.md 的杂物目录不算）",
          $"实际落地：{string.Join("、", bsDeployed)}");
    Check(File.Exists(Path.Combine(bsDst, "lark-cli", "SKILL.md")) &&
          File.Exists(Path.Combine(bsDst, "lark-cli", "scripts", "lark.py")),
          "scripts 子目录必须一起复制（只复制一层的话脚本就丢了，Skill 等于没有手）");

    // 9.3 已落地 → 一个字节都不许动（用户改过的版本优先）
    var bsUserFile = Path.Combine(bsDst, "lark-cli", "SKILL.md");
    const string bsUserEdited = "---\nname: lark-cli\ndescription: 我自己改的\n---\n这是我的版本";
    File.WriteAllText(bsUserFile, bsUserEdited, new UTF8Encoding(false));
    var bsSecond = BuiltinSkills.Deploy(bsSrc, bsDst);
    Check(bsSecond.Count == 0 && File.ReadAllText(bsUserFile) == bsUserEdited,
          "目标里已经有 SKILL.md 时必须原样不动（覆盖用户改过的版本是静默丢东西）",
          $"实际又落地了 {bsSecond.Count} 个");

    // 9.4 目标目录存在但没有 SKILL.md（用户在那儿放了个空目录/杂物目录）→ 仍要落地
    var bsOdd = Path.Combine(bsTmp, "data2", "Skills");
    Directory.CreateDirectory(Path.Combine(bsOdd, "lark-cli"));
    File.WriteAllText(Path.Combine(bsOdd, "lark-cli", "我的备忘.txt"), "x", new UTF8Encoding(false));
    var bsOddDeployed = BuiltinSkills.Deploy(bsSrc, bsOdd);
    Check(bsOddDeployed.Contains("lark-cli") && File.Exists(Path.Combine(bsOdd, "lark-cli", "SKILL.md")),
          "判据是「有没有 SKILL.md」，不是「目录存不存在」—— 空目录本来就不算 Skill，拿它挡住内置技能是永久静默失效",
          $"实际落地：{string.Join("、", bsOddDeployed)}");

    // 9.5 落地出来的东西必须真的是 Skill（跨组件联测：别只是"文件拷过去了"）
    var bsCatalog = new SkillCatalog(bsDst);
    Check(bsCatalog.TryGet("lark-cli", out var bsInfo) && bsInfo.ScriptFiles.Contains("lark.py"),
          "落地产物必须能被 Skill 扫描器认出来，且能看到它的脚本（否则 AI 那边等于没这个能力）",
          $"实际：{Cut(string.Join("、", bsCatalog.GetSkills().Select(s => s.Name)))}");

    // 9.6 恢复：覆盖前先备份，备份里是用户改过的那份，且**不能落在 Skills 目录里**
    var bsRestore = BuiltinSkills.Restore(bsSrc, bsDst, "lark-cli");
    Check(bsRestore.Ok, "恢复必须成功", $"实际：{bsRestore.Detail}");
    Check(File.ReadAllText(bsUserFile).Contains("桥接官方 lark-cli"),
          "恢复后必须是应用自带的那一份（这才是「恢复」的语义）",
          $"实际：{Cut(File.ReadAllText(bsUserFile))}");
    Check(bsRestore.BackupPath.Length > 0 && Directory.Exists(bsRestore.BackupPath) &&
          File.ReadAllText(Path.Combine(bsRestore.BackupPath, "SKILL.md")) == bsUserEdited,
          "覆盖前必须把用户那一份整体挪走当备份（不删除、留后悔药）",
          $"实际备份：{Cut(bsRestore.BackupPath)}");

    // 断言"用户能看见的性质"：清单里不许出现两行同名技能。
    // 为什么不用"备份路径不以 Skills 开头"这种路径判据 —— 那正是本项目踩过的坑：
    // `Skills_backup` 在字符串上就是以 `Skills` 开头的，前缀判法会误判（SourceOf 那条注释同理）。
    var bsAfterRestore = new SkillCatalog(bsDst).GetSkills();
    Check(bsAfterRestore.Count(s => string.Equals(s.Name, "lark-cli", StringComparison.OrdinalIgnoreCase)) == 1,
          "恢复之后清单里不得出现两行同名技能（备份要是落在 Skills 目录里就会这样）",
          $"实际 lark-cli 出现了 {bsAfterRestore.Count(s => string.Equals(s.Name, "lark-cli", StringComparison.OrdinalIgnoreCase))} 次");

    var bsBackupDir = Path.GetDirectoryName(bsRestore.BackupPath.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    var bsBackupParent = Path.GetDirectoryName(bsBackupDir ?? "") ?? "";
    Check(!string.Equals(bsBackupParent.TrimEnd(Path.DirectorySeparatorChar),
                         bsDst.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
          "备份目录不能就是 Skills 目录本身（按目录边界比，不按字符串前缀比）",
          $"实际：{Cut(bsRestore.BackupPath)}");

    // 9.7 恢复一个还没落地的技能：直接落地，不产生备份（没什么可备份的）
    var bsFreshDst = Path.Combine(bsTmp, "data-fresh", "Skills");
    var bsFresh = BuiltinSkills.Restore(bsSrc, bsFreshDst, "another-skill");
    Check(bsFresh.Ok && bsFresh.BackupPath.Length == 0 &&
          File.Exists(Path.Combine(bsFreshDst, "another-skill", "SKILL.md")),
          "恢复一个还没落地的内置技能 = 直接落地，不该产生空备份目录",
          $"实际 Ok={bsFresh.Ok}，备份={Cut(bsFresh.BackupPath)}");

    // 9.8 名字越界与「源里没有」都必须被拒（绝不把外部传来的字符串直接拼进路径）
    Check(!BuiltinSkills.Restore(bsSrc, bsDst, "../evil").Ok, "技能名带 .. 必须拒绝（路径越界）");
    Check(!BuiltinSkills.Restore(bsSrc, bsDst, "a/b").Ok, "技能名带分隔符必须拒绝（路径越界）");
    Check(!BuiltinSkills.Restore(bsSrc, bsDst, "no-such-builtin").Ok,
          "源里没有这个技能时必须拒绝，不能凭空造一个空目录出来");

    // 9.9 列内置技能：只列真的有 SKILL.md 的
    var bsNames = BuiltinSkills.ListBuiltin(bsSrc);
    Check(bsNames.Count == 2 && !bsNames.Contains("not-a-skill"),
          "列内置技能时，没有 SKILL.md 的杂物目录不算",
          $"实际：{string.Join("、", bsNames)}");
}
finally
{
    try { Directory.Delete(bsTmp, true); } catch { }
}

// ── [10] 运行时按需下载 RuntimeDownloader ──
// 守的是什么（2026-09-21 新增，授权闭环步骤 5）：使用者点一下就能把 lark-cli（47MB）与
// 内置 Python（11MB）装到数据目录里。这条路全是"静默"的错法：
//   · 下到一个错误页却当成安装包 → 装完了、用不了；
//   · 该删的没删（Python 的 *._pth）→ 解释器能跑，但同目录 import 失败，某些 Skill 静默失效；
//   · 自检没过也提交 → 目标目录看着"装好了"，实际是废物；
//   · 断流 / 403 抛出异常 → 那是个 UI 事件路径，抛出去就是吃掉点击的模态框。
// 检查点铁律不许联网，所以这里自己造一个极简的本地 HTTP 源（TcpListener），
// 并且**用自己这个 exe 当被测"运行时文件"** —— 于是"解压 → 自检真的跑一次 → 提交"整条路都真跑。
Console.WriteLine();
Console.WriteLine("[10] 运行时按需下载 RuntimeDownloader");

var dlTmp = Path.Combine(Path.GetTempPath(), "fc-tests-dl-" + Guid.NewGuid().ToString("N")[..8]);
var dlOldRoot = FocusCapturePaths.RootOverride;
FocusCapturePaths.RootOverride = dlTmp;   // 落地目录取自数据根 → 整组关在沙箱里动
try
{
    Directory.CreateDirectory(dlTmp);

    // ── 假下载源（本地 TCP 上的极简 HTTP）──
    var servedPaths = new List<string>();
    var payload = Array.Empty<byte>();
    var truncate = false;
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var dlPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    var serverCts = new System.Threading.CancellationTokenSource();

    var serverLoop = Task.Run(async () =>
    {
        while (!serverCts.IsCancellationRequested)
        {
            System.Net.Sockets.TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(serverCts.Token); }
            catch { break; }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var head = new StringBuilder();
                        var buf = new byte[2048];
                        while (true)
                        {
                            var n = await stream.ReadAsync(buf);
                            if (n <= 0) break;
                            head.Append(Encoding.ASCII.GetString(buf, 0, n));
                            if (head.ToString().Contains("\r\n\r\n")) break;
                        }

                        var parts = head.ToString().Split('\n')[0].Split(' ');
                        var path = parts.Length > 1 ? parts[1].Trim() : "/";
                        lock (servedPaths) servedPaths.Add(path);

                        var status = path.Contains("fail") ? 500 : 200;
                        var body = status == 200 ? payload : Array.Empty<byte>();
                        var header = $"HTTP/1.1 {status} X\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                        if (body.Length > 0)
                        {
                            // 断流：声明了总长却只发一半（真实网络里很常见）
                            var send = truncate ? body.AsMemory(0, body.Length / 2) : body.AsMemory();
                            await stream.WriteAsync(send);
                        }
                        await stream.FlushAsync();
                    }
                    catch { /* 客户端断开是常态 */ }
                }
            });
        }
    });

    string Url(string file) => $"http://127.0.0.1:{dlPort}/{file}";
    string UrlAlt(string file) => $"http://localhost:{dlPort}/{file}";   // 换主机名，用于验"失败信息里带主机"
    int Hits() { lock (servedPaths) return servedPaths.Count; }

    // 造一个压缩包：把一个**真的能跑**的文件（我们自己这个 exe）塞进两层深目录
    static byte[] MakeZip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            foreach (var (name, data) in entries)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                s.Write(data, 0, data.Length);
            }
        }
        return ms.ToArray();
    }

    var selfExeBytes = File.ReadAllBytes(Environment.ProcessPath!);
    // 整包解压型（对应 Python）：含一个 sib.py 与一个必须被删掉的 ._pth
    var pyLikeZip = MakeZip(
        ("python.exe", selfExeBytes),
        ("python313._pth", Encoding.UTF8.GetBytes("python313.zip\n.\n")),
        ("lib/readme.txt", Encoding.UTF8.GetBytes("x")));
    // 挑单文件型（对应 lark-cli）：目标文件藏在两层深目录里
    var cliLikeZip = MakeZip(
        ("package/bin/lark-cli.exe", selfExeBytes),
        ("package/README.md", Encoding.UTF8.GetBytes("x")));

    const string testId = "dl-probe-cli";
    var targetDir = SkillRuntimeLocations.DataDir(testId);

    // 自检桩一（数据型）：只看暂存目录里的文件对不对。
    // 用它验"自检通过才提交 / 不通过就不提交"这两条契约 —— 把自检的成败拿在手里，才测得出下载器的行为。
    Task<(bool Ok, string Detail)> VerifyPickedFile(string dir, CancellationToken ct)
    {
        var exe = Path.Combine(dir, "lark-cli.exe");
        if (!File.Exists(exe)) return Task.FromResult((false, "缺少 lark-cli.exe"));
        return Task.FromResult(new FileInfo(exe).Length == selfExeBytes.Length
            ? (true, "文件与源一致")
            : (false, "文件长度对不上"));
    }

    // 自检桩二（数据型）：整包解压型（对应 Python）的自检 —— 看关键文件在不在。
    // 特意不看 *._pth：那正是"该被删掉的东西"，拿它当自检条件就自相矛盾了。
    Task<(bool Ok, string Detail)> VerifyPyLike(string dir, CancellationToken ct)
    {
        var ok = File.Exists(Path.Combine(dir, "python.exe"))
              && File.Exists(Path.Combine(dir, "lib", "readme.txt"));
        return Task.FromResult(ok ? (true, "文件齐") : (false, "文件不齐"));
    }

    // 自检桩三（真跑进程型）：证明"自检真的起了一个进程、读了它的输出"这条契约在下载器里是通的。
    // ⚠ 不能只把这个 exe 拷过去就算了 —— .NET 的 apphost 还需要**同目录的 .dll** 才能跑起来，
    // 少了 dll 报的是「The application to execute does not exist」（本组第一次跑就是这么红的）。
    var selfExeName = Path.GetFileName(Environment.ProcessPath!);
    var selfDllPath = Path.ChangeExtension(Environment.ProcessPath!, ".dll");
    async Task<(bool Ok, string Detail)> VerifyRunChild(string dir, CancellationToken ct)
    {
        var exe = Path.Combine(dir, selfExeName);
        if (!File.Exists(exe)) return (false, $"缺少 {selfExeName}");
        var psi = SkillProcess.Build(exe, new[] { "--child-echo" }, dir, false);
        var r = await SkillProcess.RunAsync(psi, null, 30_000, ct);
        return r.Ok && r.Stdout.Contains("ECHO_OK") ? (true, "能跑") : (false, $"跑不起来：{Cut(r.Combined)}");
    }

    RuntimeRecipe CliRecipe(bool extractAll = false, string? pick = "lark-cli.exe", long minBytes = 10,
                            IReadOnlyList<string>? urls = null, IReadOnlyList<string>? del = null,
                            Func<string, CancellationToken, Task<(bool, string)>>? verify = null) =>
        new(testId, "测试部件", urls ?? new[] { Url("ok.zip") }, minBytes, extractAll, pick,
            del ?? Array.Empty<string>(), verify ?? VerifyPickedFile);

    payload = cliLikeZip;

    // 10.1 从包里挑文件（递归找）+ 真跑自检 + 提交到数据目录
    var dl1 = new RuntimeDownloader();
    var dlRes1 = await dl1.InstallAsync(CliRecipe(), null, default);
    Check(dlRes1.Ok && !dlRes1.AlreadyPresent,
          "装一个「从包里挑单文件」的部件必须成功（对应 lark-cli：包里的 exe 不在根目录，要递归找）",
          dlRes1.Detail);
    Check(string.Equals(dlRes1.TargetDir, targetDir, StringComparison.OrdinalIgnoreCase) &&
          targetDir.StartsWith(dlTmp, StringComparison.OrdinalIgnoreCase),
          "落地目录必须是「数据根\\runtime\\<id>」（跟随数据根，不写死 %AppData%）",
          $"实际：{dlRes1.TargetDir}");
    Check(File.Exists(Path.Combine(targetDir, "lark-cli.exe")),
          "挑出来的文件要真的落在目标目录里（只解压到暂存 = 白下 47MB）");

    // 10.2 进度回调：要真的被调、字节数单调不减
    var progressSeen = new List<RuntimeProgress>();
    var dl2 = new RuntimeDownloader();
    FocusCapturePaths.RootOverride = Path.Combine(dlTmp, "p2");   // 换个数据根，免得撞上"已装好"
    Directory.CreateDirectory(FocusCapturePaths.RootOverride);
    var dlRes2 = await dl2.InstallAsync(
        CliRecipe(urls: new[] { Url("ok.zip") }),
        new SyncProgress(progressSeen.Add), default);
    Check(dlRes2.Ok && progressSeen.Count > 0,
          "下载过程必须报进度（界面要给使用者看「下到哪了」）",
          $"实际收到 {progressSeen.Count} 条进度");
    var dlBytes = progressSeen.Where(p => p.Received > 0).Select(p => p.Received).ToList();
    Check(dlBytes.Count > 0 && dlBytes.SequenceEqual(dlBytes.OrderBy(x => x)),
          "进度里的字节数必须单调不减（跳来跳去说明回调算错了）",
          $"实际：{string.Join(",", dlBytes)}");
    FocusCapturePaths.RootOverride = dlTmp;   // 还原

    // 10.3 多源兜底：第一个源 500 → 必须自动落到第二个源
    FocusCapturePaths.RootOverride = Path.Combine(dlTmp, "p3");
    Directory.CreateDirectory(FocusCapturePaths.RootOverride);
    var hitsBefore = Hits();
    var dlRes3 = await new RuntimeDownloader().InstallAsync(
        CliRecipe(urls: new[] { Url("fail.zip"), Url("ok.zip") }), null, default);
    Check(dlRes3.Ok && Hits() >= hitsBefore + 2,
          "第一个下载源失败必须自动试下一个（国内镜像挂了还有官方源兜底）",
          $"{dlRes3.Detail}；请求数 {hitsBefore} → {Hits()}");

    // 10.4 全部源失败：报错要带上"是哪些源失败了"
    // 换个干净的数据根 —— 否则会撞上前面刚装好的那一份（"已装好就不下载"直接把这一条短路掉）
    FocusCapturePaths.RootOverride = Path.Combine(dlTmp, "p4");
    Directory.CreateDirectory(FocusCapturePaths.RootOverride);
    var dlRes4 = await new RuntimeDownloader().InstallAsync(
        CliRecipe(urls: new[] { Url("fail.zip"), UrlAlt("fail.zip") }), null, default);
    Check(!dlRes4.Ok && dlRes4.Detail.Contains("127.0.0.1") && dlRes4.Detail.Contains("localhost"),
          "所有源都失败时，失败信息里必须能看出试过哪些源（否则使用者只能干瞪眼）",
          Cut(dlRes4.Detail));

    // 10.5 体积下限：下到一小段错误页不能当成安装包
    FocusCapturePaths.RootOverride = Path.Combine(dlTmp, "p5");
    Directory.CreateDirectory(FocusCapturePaths.RootOverride);
    payload = Encoding.UTF8.GetBytes("<html>404 not found</html>");
    var dlRes5 = await new RuntimeDownloader().InstallAsync(
        CliRecipe(minBytes: 100_000), null, default);
    Check(!dlRes5.Ok,
          "下载内容远小于合理下限时必须判失败（不然会把一个错误页当成 47MB 的安装包装上去）",
          Cut(dlRes5.Detail));
    Check(!Directory.Exists(SkillRuntimeLocations.DataDir(testId)),
          "失败时不许在目标目录留下半成品（「看着装好了其实跑不起来」是最难查的状态）");
    payload = cliLikeZip;

    // 10.6 自检没过 → 不许提交
    FocusCapturePaths.RootOverride = Path.Combine(dlTmp, "p6");
    Directory.CreateDirectory(FocusCapturePaths.RootOverride);
    var r6 = await new RuntimeDownloader().InstallAsync(
        CliRecipe(verify: (_, _) => Task.FromResult((false, "故意让自检失败"))), null, default);
    Check(!r6.Ok && r6.Detail.Contains("自检"),
          "自检失败必须如实报出来（「文件在」≠「能跑」）", Cut(r6.Detail));
    Check(!Directory.Exists(SkillRuntimeLocations.DataDir(testId)),
          "自检没过时绝不能提交到目标目录（否则使用者会以为已经装好了）");

    // 10.7 断流（声明总长却只发一半）：不许抛异常
    FocusCapturePaths.RootOverride = Path.Combine(dlTmp, "p7");
    Directory.CreateDirectory(FocusCapturePaths.RootOverride);
    truncate = true;
    var r7 = await new RuntimeDownloader().InstallAsync(CliRecipe(), null, default);
    truncate = false;
    Check(!r7.Ok, "断流必须判失败（拿到的字节数对不上就不能当成下载完成）", Cut(r7.Detail));

    // 10.8 整包解压型（对应 Python）+ 解压后必须删掉 *._pth
    FocusCapturePaths.RootOverride = Path.Combine(dlTmp, "p8");
    Directory.CreateDirectory(FocusCapturePaths.RootOverride);
    payload = pyLikeZip;
    var pyTarget = SkillRuntimeLocations.DataDir("dl-probe-py");
    var r8 = await new RuntimeDownloader().InstallAsync(
        new RuntimeRecipe("dl-probe-py", "测试部件", new[] { Url("py.zip") }, 10,
                          extractAll: true, pickFileName: null,
                          deleteAfterExtract: new[] { "*._pth" },
                          verify: VerifyPyLike),
        null, default);
    Check(r8.Ok, "整包解压型部件必须能装成功", Cut(r8.Detail));
    Check(!File.Exists(Path.Combine(pyTarget, "python313._pth")),
          "解压后必须删掉 *._pth（python.exe 还在，但它会让解释器进 isolated 模式 → 同目录 import 静默失败）");
    Check(File.Exists(Path.Combine(pyTarget, "python.exe")) &&
          File.Exists(Path.Combine(pyTarget, "lib", "readme.txt")),
          "整包解压要保留目录结构（只解一层的话子目录里的东西就丢了）");
    payload = cliLikeZip;

    // 10.9 包里没有要找的文件 → 明确报"包结构可能变了"，不许装作成功
    FocusCapturePaths.RootOverride = Path.Combine(dlTmp, "p9");
    Directory.CreateDirectory(FocusCapturePaths.RootOverride);
    var r9 = await new RuntimeDownloader().InstallAsync(
        CliRecipe(pick: "no-such-file.exe", verify: (_, _) => Task.FromResult((true, "不该走到这"))),
        null, default);
    Check(!r9.Ok && r9.Detail.Contains("结构"),
          "压缩包里找不到目标文件时必须说清「包结构可能变了」（否则下 47MB 换来一句看不懂的错）",
          Cut(r9.Detail));

    // 10.10 已经装好且自检通过 → 一个请求都不该再发
    FocusCapturePaths.RootOverride = dlTmp;   // 10.1 已经把 testId 装在这里了
    var hitsIdle = Hits();
    var r10 = await new RuntimeDownloader().InstallAsync(CliRecipe(), null, default);
    Check(r10.Ok && r10.AlreadyPresent && Hits() == hitsIdle,
          "已经装好的部件不许重复下载（数据目录那份归使用者，也不要白花使用者 47MB 流量）",
          $"AlreadyPresent={r10.AlreadyPresent}，请求数 {hitsIdle} → {Hits()}");

    // 10.11 压缩包里的越界路径 → 拒绝（防 zip slip 写到目录外）
    FocusCapturePaths.RootOverride = Path.Combine(dlTmp, "p11");
    Directory.CreateDirectory(FocusCapturePaths.RootOverride);
    payload = MakeZip(("../escaped.txt", Encoding.UTF8.GetBytes("x")));
    var r11 = await new RuntimeDownloader().InstallAsync(
        new RuntimeRecipe("dl-probe-slip", "测试部件", new[] { Url("ok.zip") }, 10,
                          extractAll: true, pickFileName: null,
                          deleteAfterExtract: Array.Empty<string>(),
                          verify: (_, _) => Task.FromResult((true, "不该走到这"))),
        null, default);
    Check(!r11.Ok && !File.Exists(Path.Combine(dlTmp, "escaped.txt")),
          "压缩包里带 ../ 的项必须被拒绝（否则等于让远端决定往哪写文件）");
    payload = cliLikeZip;

    payload = cliLikeZip;

    // 10.12 自检真的起了一个进程（用自己这个 exe 当"运行时文件"）
    // ⚠ 光拷 exe 是不够的：.NET 的框架依赖型程序还要同目录的 .dll + .runtimeconfig.json（+ .deps.json），
    // 少了后面两个，进程起来就报 hostfxr 那句「A fatal error occurred…」—— 本组第一次就是这么红的。
    var selfBase = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath!)!,
        Path.GetFileNameWithoutExtension(Environment.ProcessPath!));
    var selfRuntimeConfig = selfBase + ".runtimeconfig.json";
    var selfDeps = selfBase + ".deps.json";

    Check(File.Exists(selfDllPath) && File.Exists(selfRuntimeConfig),
          "本组前提：应能找到自身 exe 旁边的 .dll 与 .runtimeconfig.json（框架依赖型程序要它们才能跑）",
          $"dll={File.Exists(selfDllPath)} runtimeconfig={File.Exists(selfRuntimeConfig)}");
    if (File.Exists(selfDllPath) && File.Exists(selfRuntimeConfig))
    {
        FocusCapturePaths.RootOverride = Path.Combine(dlTmp, "p12");
        Directory.CreateDirectory(FocusCapturePaths.RootOverride);

        var bundle = new List<(string Name, byte[] Data)>
        {
            (selfExeName, selfExeBytes),
            (Path.GetFileName(selfDllPath), File.ReadAllBytes(selfDllPath)),
            (Path.GetFileName(selfRuntimeConfig), File.ReadAllBytes(selfRuntimeConfig)),
        };
        if (File.Exists(selfDeps)) bundle.Add((Path.GetFileName(selfDeps), File.ReadAllBytes(selfDeps)));
        payload = MakeZip(bundle.ToArray());

        var r12 = await new RuntimeDownloader().InstallAsync(
            new RuntimeRecipe("dl-probe-run", "测试部件", new[] { Url("ok.zip") }, 10,
                              extractAll: true, pickFileName: null,
                              deleteAfterExtract: Array.Empty<string>(),
                              verify: VerifyRunChild),
            null, default);
        Check(r12.Ok && r12.Detail.Contains("能跑"),
              "自检必须真的能起进程并读它的输出（只查「文件在不在」验不出「能不能跑」）",
              Cut(r12.Detail));
        payload = cliLikeZip;
    }

    // 10.13 安装前必须清扫历史暂存残留
    // （2026-09-22 真机事故：lark-cli 连续失败后 runtime\ 下留了 7 个 staging 目录 ——
    //   失败路径的删除被安全软件锁挡住，残留既误导用户又没人提示。重试时要先扫一遍。）
    FocusCapturePaths.RootOverride = Path.Combine(dlTmp, "p13");
    var rtParent13 = Path.GetDirectoryName(SkillRuntimeLocations.DataDir(testId))!;
    Directory.CreateDirectory(rtParent13);
    var staleDir = Path.Combine(rtParent13, testId + ".staging-deadbeef");
    Directory.CreateDirectory(staleDir);
    File.WriteAllText(Path.Combine(staleDir, "junk.txt"), "x");
    var r13 = await new RuntimeDownloader().InstallAsync(CliRecipe(), null, default);
    Check(r13.Ok && !Directory.Exists(staleDir),
          "安装前必须清扫同部件的历史暂存残留（失败被锁留下的 staging 目录不能永远躺在 runtime\\ 里）",
          $"ok={r13.Ok} 残留还在={Directory.Exists(staleDir)}");

    // 10.14 「本地 IO 被拒」类失败必须被识别为疑似安全软件拦截（提示文案的判定器本体）
    Check(RuntimeDownloader.LooksLikeSecurityBlock(
              new[] { "registry.npmmirror.com：UnauthorizedAccess_IODenied_Path, C:\\x" }),
          "IODenied 类错误必须被识别为「疑似安全软件拦截」（2026-09-22 用户就是被它误导成网络问题）",
          "分类器没认出 UnauthorizedAccess_IODenied");
    Check(!RuntimeDownloader.LooksLikeSecurityBlock(
              new[] { "127.0.0.1：Response status code does not indicate success: 500." }),
          "普通网络失败不得误报成安全软件拦截（提示乱贴等于没贴）",
          "分类器把 500 也当成了安全拦截");

    serverCts.Cancel();
    listener.Stop();
    try { await serverLoop; } catch { }
}
finally
{
    FocusCapturePaths.RootOverride = dlOldRoot;
    try { Directory.Delete(dlTmp, true); } catch { }
}

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

Console.WriteLine();
Console.WriteLine($"===== {pass} 项通过，{fail} 项失败 =====");
return fail == 0 ? 0 : 1;

/// <summary>
/// 同步进度收集器。
/// 为什么不用 <c>Progress&lt;T&gt;</c>：它会把回调投递到同步上下文（控制台里就是线程池），
/// 检查点读到的列表可能还没填完 —— 那种"偶发少几条"的红绿最费时间。这里同步调用，确定性强。
/// </summary>
internal sealed class SyncProgress : IProgress<RuntimeProgress>
{
    private readonly Action<RuntimeProgress> _sink;
    public SyncProgress(Action<RuntimeProgress> sink) => _sink = sink;
    public void Report(RuntimeProgress value) => _sink(value);
}
