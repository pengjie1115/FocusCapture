// 百度网盘 listall（递归获取文件列表）—— 只读验证探针（tools/baidu-listall）
//
// 目的：验证《云端核对方案》的核心接口能否真正落地。
//   清单见 docs/2026-09-27-云端核对方案实施清单.md §8（V1~V6）。
//
// 红线（写死在代码里，不靠自觉）：
//   1. 只读：只调 listall 这一个只读接口 + 只读本地元数据/账本。
//      不建目录、不上传、不删除、不重命名。
//   2. 不写任何文件：不写日志、不写缓存。唯一例外见第 3 条。
//   3. 默认不刷新令牌 —— refresh_token 是一次性的，刷新会改变用户的授权状态。
//      仅当显式传入 --allow-refresh 时才刷新（且会明确提示）。
//   4. 绝不打印 AccessToken / RefreshToken / SecretKey；文件路径一律脱敏（只保留目录 + 扩展名）。
//
// 用法：dotnet run --project tools/baidu-listall -- <listall|compare> [--allow-refresh]

using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FocusCapture.Models;
using FocusCapture.Services.Baidu;
using FocusCapture.Services.Files;

try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 老终端不支持，忽略 */ }

const string MultimediaUrl = "https://pan.baidu.com/rest/2.0/xpan/multimedia";

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var allowRefresh = args.Any(a => a.Equals("--allow-refresh", StringComparison.OrdinalIgnoreCase));

if (cmd is "help" or "-h" or "--help")
{
    Console.WriteLine("""
        listall 只读验证探针（验证「云端核对方案」的核心接口）

        命令：
          listall   递归列沙箱根下所有条目（只读），打印统计
          compare   拉云端清单 + 读本地元数据，做差异比对，打印统计

        选项：
          --allow-refresh  令牌已过期时允许刷新（默认禁止：刷新会改变您的授权状态）

        红线：只读、不写任何文件、文件路径脱敏。
        """);
    return 0;
}

// ══════════════════ 凭据（只读） ══════════════════

var creds = BaiduCredentialStore.LoadCredentials();
if (creds?.IsComplete != true)
{
    Console.WriteLine("[FAIL] 未找到应用凭据（AppKey/SecretKey）。请先在应用「设置 → 文件与网盘」配置。");
    return 2;
}

var token = BaiduCredentialStore.LoadToken();
if (token == null)
{
    Console.WriteLine("[FAIL] 本机无授权令牌。请先在应用里完成一次百度网盘授权。");
    return 2;
}

Console.WriteLine($"[INFO] AppKey={BaiduCredentialStore.Mask(creds.AppKey)}  " +
                  $"令牌到期={token.ExpiresAt:yyyy-MM-dd HH:mm}  有效={token.IsValid}");

var access = token.AccessToken;
if (!token.IsValid)
{
    if (!allowRefresh || token.RefreshToken.Length == 0)
    {
        Console.WriteLine("[FAIL] 令牌已过期。为避免改变您的授权状态，本探针默认不刷新令牌。");
        Console.WriteLine("       请先在应用里正常使用一次（应用会自动刷新），");
        Console.WriteLine("       或显式加 --allow-refresh 授权本次刷新。");
        return 2;
    }
    Console.WriteLine("[WARN] 令牌已过期，因显式传入 --allow-refresh，将刷新令牌（会覆盖本机令牌文件）。");
    var boot = new BaiduNetdiskClient(creds, AppSettings.Load().BaiduNetRoot);
    token = await boot.RefreshTokenAsync(CancellationToken.None);
    access = token.AccessToken;
    Console.WriteLine("[INFO] 令牌已刷新并落盘。");
}

// ══════════════════ 沙箱根 ══════════════════

var settings = AppSettings.Load();
var netRoot = BaiduNetdiskClient.NormalizeNetRoot(settings.BaiduNetRoot);
Console.WriteLine($"[INFO] 沙箱根={netRoot}");
Console.WriteLine($"[INFO] 数据根={FileRepository.MetaPath}");

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("pan.baidu.com");   // 官方要求，不带会吃 31326

// ══════════════════ 工具函数 ══════════════════

// 路径脱敏：保留目录 + 扩展名，文件名本身替换成 ***（避免把用户文件名带进聊天/日志）
static string MaskPath(string path)
{
    var i = path.LastIndexOf('/');
    if (i < 0) return "***";
    var name = path[(i + 1)..];
    var dot = name.LastIndexOf('.');
    var ext = dot >= 0 ? name[dot..] : "";
    return $"{path[..i]}/***{ext}";
}

// 归到「相对沙箱根的目录分组」，如 files、attachments、attachments/2026-09。
// ⚠️ 只取**目录**层级，绝不把文件名当分组名 —— 否则文件名会随统计输出一起泄露。
static string Group(string path, string root)
{
    var rel = path.StartsWith(root, StringComparison.Ordinal) ? path[root.Length..] : path;
    var seg = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (seg.Length == 0) return "(根)";
    // 第二段形如 2026-09 时视作月份子目录；否则第二段就是文件名，不能当分组
    if (seg.Length >= 2 && seg[1].Length == 7 && seg[1][4] == '-') return $"{seg[0]}/{seg[1]}";
    return seg[0];
}

// 调 listall：recursion=1 + has_more/cursor 翻页。返回 (条目, 调用次数, 耗时ms, 备注)
async Task<(List<JsonElement> Items, int Calls, long Ms, string Note)> ListAllAsync(string path)
{
    var all = new List<JsonElement>();
    var calls = 0;
    var note = "";
    var start = 0;
    var sw = Stopwatch.StartNew();

    while (true)
    {
        calls++;
        var url = $"{MultimediaUrl}?method=listall" +
                  $"&access_token={Uri.EscapeDataString(access)}" +
                  $"&path={Uri.EscapeDataString(path)}" +
                  $"&recursion=1&order=name&desc=0&start={start}&limit=1000&web=0";
        string body;
        try
        {
            body = await http.GetStringAsync(url);
        }
        catch (Exception ex)
        {
            note = "HTTP 异常：" + ex.Message;
            break;
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var errno = root.TryGetProperty("errno", out var e) && e.TryGetInt32(out var ev) ? ev : 0;
        if (errno != 0)
        {
            var msg = root.TryGetProperty("errmsg", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "";
            note = $"errno={errno} {msg}";
            break;
        }

        if (root.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var it in arr.EnumerateArray()) all.Add(it.Clone());

        var hasMore = root.TryGetProperty("has_more", out var hm) && hm.TryGetInt32(out var hmv) && hmv == 1;
        if (!hasMore) break;

        // 官方明确：必须用响应里的 cursor 作下一次 start，不要自行累加固定步长
        start = root.TryGetProperty("cursor", out var c) && c.TryGetInt32(out var cv) ? cv : start + 1000;
        if (calls >= 50) { note = "翻页超过 50 次，主动停止"; break; }
    }

    sw.Stop();
    return (all, calls, sw.ElapsedMilliseconds, note);
}

// ══════════════════ 命令：listall ══════════════════

if (cmd == "listall")
{
    Console.WriteLine();
    Console.WriteLine("=== V1/V3/V4/V6：调 listall（recursion=1）列沙箱根 ===");
    var (items, calls, ms, note) = await ListAllAsync(netRoot);

    Console.WriteLine($"[结果] 条目={items.Count}  调用次数={calls}  耗时={ms}ms  {(note.Length > 0 ? "备注=" + note : "errno=0")}");

    var dirs = items.Count(i => i.TryGetProperty("isdir", out var d) && d.TryGetInt32(out var dv) && dv == 1);
    var files = items.Count - dirs;
    Console.WriteLine($"[结果] 目录={dirs}  文件={files}");

    Console.WriteLine();
    Console.WriteLine("--- 按相对路径分组（验证 V3：递归是否覆盖 attachments/月份 子目录）---");
    foreach (var g in items
                 .Where(i => i.TryGetProperty("path", out _))
                 .GroupBy(i => Group(i.GetProperty("path").GetString() ?? "", netRoot))
                 .OrderByDescending(g => g.Count()))
    {
        var f = g.Count(x => !(x.TryGetProperty("isdir", out var d) && d.TryGetInt32(out var dv) && dv == 1));
        Console.WriteLine($"    分组 {g.Key,-28} 共 {g.Count(),5} 项（文件 {f}）");
    }

    Console.WriteLine();
    Console.WriteLine("--- 路径样例（脱敏，验证 V2：path 字段是否存在及其形态）---");
    foreach (var it in items.Where(i => i.TryGetProperty("path", out _)).Take(8))
    {
        var p = it.GetProperty("path").GetString() ?? "";
        var isDir = it.TryGetProperty("isdir", out var d) && d.TryGetInt32(out var dv) && dv == 1;
        var size = it.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
        var md5 = it.TryGetProperty("md5", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "";
        var hasMd5 = !string.IsNullOrEmpty(md5);
        Console.WriteLine($"    {(isDir ? "[D]" : "[F]")} {MaskPath(p)}  {size}B  md5字段={(hasMd5 ? "有" : "无")}");
    }

    Console.WriteLine();
    Console.WriteLine("--- 字段可用性清点（决定比对算法能用哪些键）---");
    var withPath = items.Count(i => i.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String && p.GetString()!.Length > 0);
    var withName = items.Count(i => i.TryGetProperty("server_filename", out var n) && n.ValueKind == JsonValueKind.String);
    var withMd5 = items.Count(i => i.TryGetProperty("md5", out var m) && m.ValueKind == JsonValueKind.String && (m.GetString() ?? "").Length > 0);
    var withMtime = items.Count(i => i.TryGetProperty("server_mtime", out _));
    Console.WriteLine($"    path={withPath}/{items.Count}  server_filename={withName}/{items.Count}  " +
                      $"md5={withMd5}/{items.Count}  server_mtime={withMtime}/{items.Count}");
    return 0;
}

// ══════════════════ 命令：compare ══════════════════

if (cmd == "compare")
{
    Console.WriteLine();
    Console.WriteLine("=== V2/V5：拉云端清单 + 与本地元数据比对 ===");
    var (items, calls, ms, note) = await ListAllAsync(netRoot);
    Console.WriteLine($"[云端] 条目={items.Count}  调用次数={calls}  耗时={ms}ms  {(note.Length > 0 ? "备注=" + note : "errno=0")}");

    // 云端文件路径集合（排除目录条目）
    var cloudFiles = items
        .Where(i => i.TryGetProperty("isdir", out var d) && d.TryGetInt32(out var dv) && dv == 0)
        .Where(i => i.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
        .Select(i => new
        {
            Path = i.GetProperty("path").GetString()!.TrimEnd('/'),
            Md5 = i.TryGetProperty("md5", out var m) && m.ValueKind == JsonValueKind.String ? (m.GetString() ?? "") : "",
            Size = i.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0,
        })
        .ToList();
    var cloudSet = new HashSet<string>(cloudFiles.Select(x => x.Path), StringComparer.Ordinal);
    Console.WriteLine($"[云端] 其中文件={cloudFiles.Count}");

    // 本地元数据（含墓碑）+ 账本 —— 全部只读
    var meta = FileRepository.AllMetadata(includeDeleted: true);
    Console.WriteLine($"[本地] 元数据记录={meta.Count}（含墓碑）");

    int hit = 0, missing = 0, skipTomb = 0, skipExpired = 0, skipPending = 0, skipNotUploaded = 0, skipNoLedger = 0;
    var missingSamples = new List<string>();
    var md5Agree = 0; var md5Checked = 0;

    foreach (var m in meta)
    {
        // 与 §5.2 判定表逐条对齐
        if (m.Deleted) { skipTomb++; continue; }
        if (m.CloudState == CloudStates.Expired) { skipExpired++; continue; }
        if (m.CloudState == CloudStates.CleanupPending) { skipPending++; continue; }

        var key = m.NetPath.TrimEnd('/');
        if (cloudSet.Contains(key))
        {
            hit++;
            // V5：顺带实证「云端 md5 是否等于本地内容 MD5」（即便相等也不作为判定键）
            var ce = cloudFiles.FirstOrDefault(x => x.Path == key);
            if (ce != null && ce.Md5.Length > 0 && m.Md5.Length > 0)
            {
                md5Checked++;
                if (string.Equals(ce.Md5, m.Md5, StringComparison.OrdinalIgnoreCase)) md5Agree++;
            }
            continue;
        }

        var entry = FileRepository.FindCache(m.Id);
        if (entry == null) { skipNoLedger++; continue; }                              // 他端记录，无本机证据
        if (entry.UploadState == UploadStates.Uploaded)
        {
            missing++;
            if (missingSamples.Count < 5) missingSamples.Add(MaskPath(m.NetPath));
        }
        else skipNotUploaded++;                                                        // 还没传上去
    }

    // 云端多出：本地（含墓碑）按路径完全找不到
    var localAll = new HashSet<string>(meta.Select(m => m.NetPath.TrimEnd('/')), StringComparer.Ordinal);
    var orphans = cloudFiles.Where(x => !localAll.Contains(x.Path)).ToList();

    Console.WriteLine();
    Console.WriteLine("--- 比对结果（与实施清单 §5.2 判定表对齐）---");
    Console.WriteLine($"    命中（本地有·云端有）          : {hit}");
    Console.WriteLine($"    ▶ 疑似缺失（本地有·云端无·账本已上传）: {missing}");
    Console.WriteLine($"    跳过·墓碑                       : {skipTomb}");
    Console.WriteLine($"    跳过·云端已到期清理              : {skipExpired}");
    Console.WriteLine($"    跳过·云端待清理                  : {skipPending}");
    Console.WriteLine($"    跳过·还没传上去                  : {skipNotUploaded}");
    Console.WriteLine($"    跳过·他端记录（本机无账本证据）     : {skipNoLedger}");
    Console.WriteLine($"    ▶ 云端多出（只报告、不入账）        : {orphans.Count}");

    if (missingSamples.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("--- 疑似缺失样例（脱敏）---");
        foreach (var s in missingSamples) Console.WriteLine("    " + s);
    }

    Console.WriteLine();
    Console.WriteLine("--- V5：云端 md5 与本地内容 MD5 实测 ---");
    Console.WriteLine(md5Checked == 0
        ? "    无可比对样本（云端 md5 与本地 md5 同时非空的记录为 0）"
        : $"    可比对 {md5Checked} 条，相等 {md5Agree} 条  →  " +
          (md5Agree == md5Checked ? "实测一致（但官方文档称非真实 MD5，仍不采纳为判定键）"
                                  : "实测不一致 —— 印证官方说法，绝不能用作判定键"));
    return 0;
}

Console.WriteLine($"[FAIL] 未知命令：{cmd}（用 help 看用法）");
return 2;
