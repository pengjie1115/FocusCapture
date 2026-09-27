// 百度网盘 unisearch（语义搜索）—— 只读验证探针（tools/baidu-unisearch）
//
// 目的：验证《网盘语义检索可行性评估》的决定性未知数。
//   见 docs/2026-09-27-网盘语义检索可行性评估.md §五（U1~U4）。
//
// 红线（写死在代码里，不靠自觉）：
//   1. 只读：只调 unisearch 这一个只读搜索接口。
//      不下载、不上传、不删除、不重命名、不建目录。
//   2. 不写任何文件：不写日志、不写缓存。
//   3. 不刷新令牌 —— refresh_token 是一次性的，刷新会改变用户的授权状态。
//   4. 绝不打印 AccessToken / RefreshToken / SecretKey；
//      路径一律脱敏：沙箱内只保留到目录层级，沙箱外一律记作 (沙箱外)。
//
// 用法：dotnet run --project tools/baidu-unisearch -- [--with-dir]

using System.Net.Http;
using System.Text;
using System.Text.Json;
using FocusCapture.Models;
using FocusCapture.Services.Baidu;

try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 老终端不支持，忽略 */ }

const string UniSearchUrl = "https://pan.baidu.com/xpan/unisearch";

var withDir = args.Any(a => a.Equals("--with-dir", StringComparison.OrdinalIgnoreCase));

// 可选：自定义单次查询（用于针对某个已知沙箱文件做定点验证）
//   用法：--query "民法典" [--type 0|1|2]
string? customQuery = null; var customType = 0;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--query" && i + 1 < args.Length) customQuery = args[i + 1];
    if (args[i] == "--type" && i + 1 < args.Length && int.TryParse(args[i + 1], out var tv)) customType = tv;
}

// ══════════════════ 凭据（只读，绝不刷新） ══════════════════

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

if (!token.IsValid)
{
    Console.WriteLine("[FAIL] 令牌已过期。本探针不刷新令牌（刷新会改变您的授权状态）。");
    Console.WriteLine("       请先在应用里正常使用一次（应用会自动刷新）后再跑。");
    return 2;
}

var settings = AppSettings.Load();
var netRoot = BaiduNetdiskClient.NormalizeNetRoot(settings.BaiduNetRoot);
Console.WriteLine($"[INFO] 沙箱根={netRoot}");

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("pan.baidu.com");   // 官方要求，不带会吃 31326

// ══════════════════ 场景 ══════════════════

var scenarios = new List<(string Id, string Desc, string Query, int Type, bool UseDir)>();
if (withDir)
{
    // 只跑 S3 —— S1/S2 的数据已有，避免重复调用白白消耗配额。
    // 刻意与 S2 完全同参（同 query、同 search_type），唯一变量 = 加了 dir 限定。
    // 这样 S2 vs S3 就是一次干净的对照实验：dir 到底生不生效，一眼可判。
    scenarios.Add(("S3", "沙箱限定对照（同 S2，唯一变量=传 dir）", "关于民事法律中公民权利与义务的规定", 1, true));
}
else if (args.Any(a => a.Equals("--s4", StringComparison.OrdinalIgnoreCase)))
{
    // 定点验证：沙箱内 attachments 目录的已知文件能否被**文件名关键词**召回。
    //   「民法典.docx」实际位于 /apps/FocusCapture/attachments/2026-09/
    //   中文 query 写死在代码里，避免命令行传参被 GBK 破坏。
    scenarios.Add(("S4", "定点·沙箱内 attachments 文件（文件名关键词）", "民法典", 0, false));
}
else if (customQuery != null)
{
    // 定点验证：只跑用户指定的这一条 query（省配额）
    scenarios.Add(("SC", $"定点查询 query=\"{customQuery}\"（search_type={customType}）", customQuery, customType, false));
}
else
{
    scenarios.Add(("S1", "关键词·文件名召回（不传 dir）", "体检报告", 0, false));
    scenarios.Add(("S2", "语义·内容召回（不传 dir）", "关于民事法律中公民权利与义务的规定", 1, false));
}

var anyError = false;

foreach (var s in scenarios)
{
    Console.WriteLine();
    Console.WriteLine($"=== {s.Id} {s.Desc} ===");
    Console.WriteLine($"[请求] query=\"{s.Query}\"  search_type={s.Type}  dir={(s.UseDir ? netRoot : "(未传→默认全盘)")}");

    try
    {
        var url = $"{UniSearchUrl}?scene=mcpserver&access_token={Uri.EscapeDataString(token.AccessToken)}" +
                  $"&query={Uri.EscapeDataString(s.Query)}&num=20&search_type={s.Type}";
        if (s.UseDir) url += $"&dir={Uri.EscapeDataString("[\"" + netRoot + "\"]")}";

        using var body = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(url, body, CancellationToken.None);
        var text = await resp.Content.ReadAsStringAsync(CancellationToken.None);

        Console.WriteLine($"[HTTP] {(int)resp.StatusCode}");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var errNo = root.TryGetProperty("error_no", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : -1;
        var errMsg = root.TryGetProperty("error_msg", out var m) ? (m.GetString() ?? "") : "";
        Console.WriteLine($"[结果] error_no={errNo}  error_msg=\"{errMsg}\"");
        if (errNo != 0) anyError = true;

        var items = new List<JsonElement>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var g in data.EnumerateArray())
                if (g.TryGetProperty("list", out var lst) && lst.ValueKind == JsonValueKind.Array)
                    foreach (var it in lst.EnumerateArray()) items.Add(it);

        var inSandbox = 0; var outSandbox = 0; var contentNonEmpty = 0;
        var dirs = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var sources = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var cats = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var it in items)
        {
            var path = it.TryGetProperty("path", out var p) ? (p.GetString() ?? "") : "";
            if (path.StartsWith(netRoot, StringComparison.Ordinal))
            {
                inSandbox++;
                var i = path.LastIndexOf('/');
                var dir = i > netRoot.Length ? path[..i] : netRoot;
                dirs[dir] = dirs.GetValueOrDefault(dir) + 1;
            }
            else { outSandbox++; dirs["(沙箱外)"] = dirs.GetValueOrDefault("(沙箱外)") + 1; }

            var c = it.TryGetProperty("content", out var cv) ? (cv.GetString() ?? "") : "";
            if (c.Trim().Length > 0) contentNonEmpty++;

            var src = it.TryGetProperty("source", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetInt32().ToString() : "?";
            sources[src] = sources.GetValueOrDefault(src) + 1;
            var cat = it.TryGetProperty("category", out var catv) ? catv.ToString() : "?";
            cats[cat] = cats.GetValueOrDefault(cat) + 1;
        }

        Console.WriteLine($"[统计] 条目={items.Count}  沙箱内={inSandbox}  沙箱外={outSandbox}");
        Console.WriteLine($"[统计] content 非空={contentNonEmpty}（>0 表示文件内容已被召回，即已做内容预处理）");
        Console.WriteLine($"[统计] source 分布={string.Join(", ", sources.Select(kv => $"{kv.Key}:{kv.Value}"))}");
        Console.WriteLine($"[统计] category 分布={string.Join(", ", cats.Select(kv => $"{kv.Key}:{kv.Value}"))}");
        Console.WriteLine($"[统计] 目录分布={string.Join(", ", dirs.Select(kv => $"{kv.Key}={kv.Value}"))}");
    }
    catch (Exception ex)
    {
        anyError = true;
        Console.WriteLine($"[FAIL] 调用异常：{ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine(anyError ? "[DONE] 存在失败/非零 error_no，见上。" : "[DONE] 全部调用成功（error_no=0）。");
return anyError ? 1 : 0;
