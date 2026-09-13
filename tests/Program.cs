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
using System.Security.Cryptography;
using FocusCapture.Services;
using FocusCapture.Services.Sync;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

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

Console.WriteLine();
Console.WriteLine($"===== {pass} 项通过，{fail} 项失败 =====");
return fail == 0 ? 0 : 1;
