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

Console.WriteLine();
Console.WriteLine($"===== {pass} 项通过，{fail} 项失败 =====");
return fail == 0 ? 0 : 1;
