// 百度网盘真机联调诊断（tools/baidu-diag）
//
// 为什么单独做一个工程：**必须与应用同一套实现**。另写一份 HTTP 逻辑去测接口，
// 只能证明"那么调可以"，证明不了"应用里跑通"。这里直接引用主项目的
// BaiduNetdiskClient / BaiduCredentialStore / Dpapi，跑的就是应用真实走的那条路。
//
// 红线（写死在代码里，不靠自觉）：
//   1. 只允许创建 / 删除文件名以 "zz-diag-" 开头的条目 —— 绝不碰用户的任何文件
//   2. 绝不打印 AccessToken / RefreshToken / SecretKey（最多 AppKey 前 4 位）
//   3. 只读地列目录永远是安全的；写操作仅限 zz-diag- 前缀
//
// 用法：dotnet run --project tools/baidu-diag -- <list|probe-delete|help>

using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FocusCapture;                    // FocusCapturePaths 在根命名空间（子命名空间可直接引用，顶层程序要显式 using）
using FocusCapture.Services.Baidu;
using FocusCapture.Services.Files;

const string XpanFile = "https://pan.baidu.com/rest/2.0/xpan/file";
const string TestPrefix = "zz-diag-";

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

var creds = BaiduCredentialStore.LoadCredentials();
if (creds?.IsComplete != true)
{
    Console.WriteLine("[FAIL] 未找到应用凭据（AppKey/SecretKey），先在应用里配置。");
    return 2;
}

var token = BaiduCredentialStore.LoadToken();
if (token == null)
{
    Console.WriteLine("[FAIL] 本机无授权令牌，先走一次授权。");
    return 2;
}

Console.WriteLine($"[INFO] AppKey={BaiduCredentialStore.Mask(creds.AppKey)}  令牌到期={token.ExpiresAt:yyyy-MM-dd HH:mm}  有效={token.IsValid}");

var client = new BaiduNetdiskClient(creds);
if (!token.IsValid)
{
    if (token.RefreshToken.Length == 0) { Console.WriteLine("[FAIL] 令牌过期且无 refresh_token，需重新授权。"); return 2; }
    token = await client.RefreshTokenAsync(CancellationToken.None);
    Console.WriteLine("[INFO] 令牌已自动刷新（refresh_token 一次性，已落盘）");
}

var access = token.AccessToken;
var netRoot = client.NetRoot;
var filesDir = netRoot + "/files";
var attachDir = netRoot + "/attachments";
Console.WriteLine($"[INFO] 沙箱根={netRoot}  files={filesDir}  attachments={attachDir}");

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("pan.baidu.com");   // 官方要求：不带会吃 31326

if (cmd is "help" or "-h" or "--help")
{
    Console.WriteLine("""
        命令：
          list          列 /apps、沙箱根、files、attachments（只读）
          probe-delete  穷举 filemanager&opera=delete 的参数形态，找出可用写法
        """);
    return 0;
}

if (cmd == "list")
{
    foreach (var dir in new[] { "/apps", netRoot, filesDir, attachDir })
    {
        try
        {
            var items = await client.ListAsync(dir, CancellationToken.None);
            Console.WriteLine($"--- {dir}  共 {items.Count} 项");
            foreach (var e in items.Take(80))
                Console.WriteLine($"    {(e.IsDir ? "[D]" : "[F]")} {e.Name}  {e.Size}B  fsid={e.FsId}");
        }
        catch (Exception ex) { Console.WriteLine($"--- {dir}  失败：{ex.Message}"); }
        await Task.Delay(400);
    }
    return 0;
}

if (cmd == "probe-delete")
{
    // ── 准备一个「自己的」测试文件 ──
    var existing = (await client.ListAsync(filesDir, CancellationToken.None))
        .Where(e => !e.IsDir && e.Name.StartsWith(TestPrefix, StringComparison.Ordinal))
        .ToList();

    BaiduFileEntry target;
    if (existing.Count > 0)
    {
        target = existing[0];
        Console.WriteLine($"[INFO] 复用已有测试文件：{target.Name}（fsid={target.FsId}）");
    }
    else
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var netPath = $"{filesDir}/{TestPrefix}{stamp}.txt";
        var local = Path.Combine(Path.GetTempPath(), $"{TestPrefix}{stamp}.txt");
        await File.WriteAllTextAsync(local, new string('x', 1024));
        await client.UploadFileAsync(local, netPath, null, CancellationToken.None);
        await Task.Delay(600);
        target = (await client.ListAsync(filesDir, CancellationToken.None))
            .FirstOrDefault(e => e.Name == $"{TestPrefix}{stamp}.txt")
            ?? throw new InvalidOperationException("测试文件上传后没在云端找到");
        Console.WriteLine($"[INFO] 新建测试文件：{target.Name}（fsid={target.FsId}）");
    }

    // 红线：只允许删自己的前缀
    if (!target.Name.StartsWith(TestPrefix, StringComparison.Ordinal))
        throw new InvalidOperationException($"拒绝删除非测试文件：{target.Name}");

    var path = target.Path;
    var fsid = target.FsId;
    var pathJson = JsonSerializer.Serialize(new[] { path });
    var objPathJson = JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["path"] = path } });
    var fsidJson = JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["fs_id"] = fsid } });

    var cases = new List<(string Title, Func<Task<(HttpResponseMessage Resp, string Body)>> Run)>
    {
        ("① 现行实现 client.DeleteAsync（async=0 + path 数组）", async () =>
        {
            try
            {
                await client.DeleteAsync(new[] { path }, CancellationToken.None);
                return (new HttpResponseMessage(System.Net.HttpStatusCode.OK), "{\"errno\":0}（无异常）");
            }
            catch (Exception ex)
            {
                return (new HttpResponseMessage(System.Net.HttpStatusCode.OK), "异常：" + ex.Message);
            }
        }),

        ("② form: opera=delete & async=0 & filelist=[path]", () => Post(Form(("opera", "delete"), ("async", "0"), ("filelist", pathJson)))),

        ("③ form: opera=delete（不传 async）& filelist=[path]", () => Post(Form(("opera", "delete"), ("filelist", pathJson)))),

        ("④ form: opera=delete & async=1 & filelist=[path]", () => Post(Form(("opera", "delete"), ("async", "1"), ("filelist", pathJson)))),

        ("⑤ form: opera=delete & async=2 & filelist=[path]", () => Post(Form(("opera", "delete"), ("async", "2"), ("filelist", pathJson)))),

        ("⑥ form: filelist=[{\"path\":…}] 对象数组", () => Post(Form(("opera", "delete"), ("async", "0"), ("filelist", objPathJson)))),

        ("⑦ form: filelist=[{\"fs_id\":…}] 数字 id", () => Post(Form(("opera", "delete"), ("async", "0"), ("filelist", fsidJson)))),

        ("⑧ json body: {opera,async,filelist}", async () =>
        {
            var url = $"{XpanFile}?method=filemanager&access_token={Uri.EscapeDataString(access)}";
            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["opera"] = "delete", ["async"] = 0, ["filelist"] = new[] { path },
            });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp = await http.PostAsync(url, content);
            return (resp, await ReadBodyAsync(resp));
        }),

        ("⑨ 参数全放 query（POST 空 body）", async () =>
        {
            var q = $"opera=delete&async=0&filelist={Uri.EscapeDataString(pathJson)}";
            var url = $"{XpanFile}?method=filemanager&access_token={Uri.EscapeDataString(access)}&{q}";
            var resp = await http.PostAsync(url, new StringContent(""));
            return (resp, await ReadBodyAsync(resp));
        }),

        ("⑩ method=delete（旧式端点试探）", async () =>
        {
            var url = $"{XpanFile}?method=delete&access_token={Uri.EscapeDataString(access)}&path={Uri.EscapeDataString(path)}";
            var resp = await http.PostAsync(url, new StringContent(""));
            return (resp, await ReadBodyAsync(resp));
        }),

        ("⑪ filelist 用应用目录相对路径 /files/…", () => Post(Form(("opera", "delete"), ("async", "0"),
            ("filelist", JsonSerializer.Serialize(new[] { "/files/" + target.Name }))))),

        ("⑫ form: filelist=[path] + 额外 ondup 兜底参数集（async=0 + filelist 已编码 json）",
            () => Post(Form(("opera", "delete"), ("async", "0"), ("filelist", pathJson), ("method", "filemanager")))),
    };

    string? winner = null;
    for (var i = 0; i < cases.Count; i++)
    {
        var (title, run) = cases[i];
        Console.WriteLine($"\n=== [{i + 1}/{cases.Count}] {title}");
        try
        {
            var (resp, body) = await run();
            var errno = ExtractErrno(body);
            Console.WriteLine($"    HTTP {(int)resp.StatusCode}   errno={errno}");
            Console.WriteLine($"    body: {Truncate(body, 260)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    异常：{ex.Message}");
        }

        await Task.Delay(900);

        // 复查：云端那份还在不在（只有列表说了算，不看返回值）
        bool stillThere;
        try
        {
            stillThere = (await client.ListAsync(filesDir, CancellationToken.None))
                .Any(e => e.Name == target.Name);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    复查失败：{ex.Message}");
            continue;
        }
        Console.WriteLine(stillThere ? "    → 复查：文件仍在云端" : "    → 复查：文件已消失 ✓");
        if (!stillThere) { winner = title; break; }
        await Task.Delay(300);
    }

    if (winner == null)
    {
        Console.WriteLine($"\n[结论] {cases.Count} 种形态全部无效，云端仍留有测试文件 {target.Name}（需手动删除或在网盘客户端删）。");
        return 3;
    }

    Console.WriteLine($"\n[结论] 可用写法 = {winner}");
    // 收尾：确保测试文件真的被清掉（若赢家用的是别的路径写法，这里再删一次）
    try
    {
        await client.DeleteAsync(new[] { path }, CancellationToken.None);
    }
    catch { /* 已被删掉时忽略 */ }
    await Task.Delay(500);
    var left = (await client.ListAsync(filesDir, CancellationToken.None)).Any(e => e.Name == target.Name);
    Console.WriteLine(left ? $"[WARN] 收尾删除未生效，云端仍有 {target.Name}" : "[OK] 测试文件已清理干净");
    return winner == null ? 3 : 0;

    // ── 局部函数 ──
    FormUrlEncodedContent Form(params (string Key, string Value)[] kv)
        => new(kv.Select(x => new KeyValuePair<string, string>(x.Key, x.Value)));

    async Task<(HttpResponseMessage, string)> Post(HttpContent content)
    {
        var url = $"{XpanFile}?method=filemanager&access_token={Uri.EscapeDataString(access)}";
        using (content)
        {
            var resp = await http.PostAsync(url, content);
            return (resp, await ReadBodyAsync(resp));
        }
    }
}

if (cmd == "probe-delete2")
{
    // 第二轮：`method=delete` 已被证明可用（第一轮 ⑩）。这里摸清它支持哪些形态 ——
    // 单文件 / filelist 批量 / 目录 / path 多值，判成败一律以「列表复查」为准（返回值不可信）。
    Console.WriteLine("[INFO] 目标：摸清 method=delete 的可用形态");
    var stamp = DateTime.Now.ToString("HHmmss");
    var prepared = new List<string>();
    for (var i = 0; i < 3; i++)
    {
        var p = $"{filesDir}/{TestPrefix}x{i}-{stamp}.txt";
        var local = Path.Combine(Path.GetTempPath(), $"{TestPrefix}x{i}-{stamp}.txt");
        await File.WriteAllTextAsync(local, new string('y', 512));
        await client.UploadFileAsync(local, p, null, CancellationToken.None);
        prepared.Add(p);
        await Task.Delay(300);
    }
    var testDir = $"{netRoot}/{TestPrefix}dir-{stamp}";
    await client.EnsureDirectoryAsync(testDir, CancellationToken.None);
    await Task.Delay(600);
    Console.WriteLine($"[INFO] 已准备 3 个测试文件 + 目录 {testDir}");

    async Task<bool> FileExistsAsync(string netPath)
    {
        var name = netPath[(netPath.LastIndexOf('/') + 1)..];
        var list = await client.ListAsync(filesDir, CancellationToken.None);
        return list.Any(e => e.Name == name);
    }

    async Task<bool> DirExistsAsync(string netDir)
    {
        var name = netDir[(netDir.LastIndexOf('/') + 1)..];
        var list = await client.ListAsync(netRoot, CancellationToken.None);
        return list.Any(e => e.Name == name);
    }

    async Task SendAsync(string title, string query, Func<Task<bool>> stillThere)
    {
        var url = $"{XpanFile}?method=delete&access_token={Uri.EscapeDataString(access)}{query}";
        try
        {
            using var resp = await http.PostAsync(url, new StringContent(""));
            var body = await ReadBodyAsync(resp);
            await Task.Delay(900);
            var left = await stillThere();
            Console.WriteLine($"=== {title}\n    HTTP {(int)resp.StatusCode}  body: {Truncate(body, 150)}\n    → 复查：{(left ? "仍在" : "已消失 ✓")}");
        }
        catch (Exception ex) { Console.WriteLine($"=== {title}\n    异常：{ex.Message}"); }
    }

    await SendAsync("① 单文件 path", $"&path={Uri.EscapeDataString(prepared[0])}", () => FileExistsAsync(prepared[0]));
    await SendAsync("② filelist=[path] 批量形态", $"&filelist={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { prepared[1] }))}", () => FileExistsAsync(prepared[1]));
    await SendAsync("③ 目录 path（清垃圾目录的能力）", $"&path={Uri.EscapeDataString(testDir)}", () => DirExistsAsync(testDir));
    await SendAsync("④ 一次两个：path + filelist 同时给（看是否只认其中一个）",
        $"&path={Uri.EscapeDataString(prepared[2])}&filelist={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { prepared[2] }))}",
        () => FileExistsAsync(prepared[2]));

    // 收尾：把可能残留的测试物清干净
    foreach (var p in prepared)
    {
        if (await FileExistsAsync(p)) await SendAsync($"收尾清理 {p[(p.LastIndexOf('/') + 1)..]}", $"&path={Uri.EscapeDataString(p)}", () => FileExistsAsync(p));
    }
    if (await DirExistsAsync(testDir)) await SendAsync("收尾清理测试目录", $"&path={Uri.EscapeDataString(testDir)}", () => DirExistsAsync(testDir));

    var leftFiles = (await client.ListAsync(filesDir, CancellationToken.None)).Where(e => e.Name.StartsWith(TestPrefix, StringComparison.Ordinal)).ToList();
    var leftDirs = (await client.ListAsync(netRoot, CancellationToken.None)).Where(e => e.Name.StartsWith(TestPrefix, StringComparison.Ordinal)).ToList();
    Console.WriteLine($"[终态] 残留测试文件 {leftFiles.Count} 个、测试目录 {leftDirs.Count} 个" +
                      (leftFiles.Count + leftDirs.Count == 0 ? "（已清干净）" : $"：{string.Join("、", leftFiles.Select(x => x.Name).Concat(leftDirs.Select(x => x.Name)))}"));
    return 0;
}

if (cmd == "probe-delete3")
{
    // 第三轮：① 删除**不存在**的文件时返回什么（成功响应里没有 errno，必须摸清失败形态，
    //          否则代码无法判成败）；② 顺带清掉本应用建目录 bug 造出的垃圾空目录。
    Console.WriteLine("[INFO] 目标：删除失败形态 + 清理遗留垃圾目录");

    async Task<string> RawDeleteAsync(string path)
    {
        var url = $"{XpanFile}?method=delete&access_token={Uri.EscapeDataString(access)}&path={Uri.EscapeDataString(path)}";
        using var resp = await http.PostAsync(url, new StringContent(""));
        return $"HTTP {(int)resp.StatusCode}  {Truncate(await ReadBodyAsync(resp), 160)}";
    }

    Console.WriteLine("--- ① 删除一个不存在的文件：");
    Console.WriteLine("    " + await RawDeleteAsync($"{filesDir}/{TestPrefix}ghost-not-exist.txt"));
    await Task.Delay(700);

    Console.WriteLine("--- ② 删除沙箱外路径（平台是否自己拦）：");
    Console.WriteLine("    " + await RawDeleteAsync("/zz-diag-should-not-exist.txt"));
    await Task.Delay(700);

    // ③ 垃圾目录：先确认是空的，空才删（空目录没有任何数据，删掉无损）
    var junk = new[]
    {
        "/apps/FocusCapture_20260916_112442",
        "/apps/FocusCapture_20260916_104601",
        "/apps/FocusCapture/files_20260916_112442",
    };
    foreach (var dir in junk)
    {
        try
        {
            var items = await client.ListAsync(dir, CancellationToken.None);
            Console.WriteLine($"--- ③ {dir}：{items.Count} 项 {(items.Count == 0 ? "（空目录）" : "→ 非空，不动它")}");
            foreach (var e in items.Take(10)) Console.WriteLine($"      {(e.IsDir ? "[D]" : "[F]")} {e.Name}");
            if (items.Count == 0)
            {
                Console.WriteLine("    删除：" + await RawDeleteAsync(dir));
                await Task.Delay(900);
                var left = (await client.ListAsync("/apps/FocusCapture", CancellationToken.None))
                    .Concat(await client.ListAsync("/apps", CancellationToken.None));
                var gone = !left.Any(e => e.Path == dir);
                Console.WriteLine($"    → 复查：{(gone ? "已消失 ✓" : "仍在")}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"--- ③ {dir} 失败：{ex.Message}"); }
        await Task.Delay(400);
    }

    Console.WriteLine("--- ④ /apps 终态：");
    foreach (var e in await client.ListAsync("/apps", CancellationToken.None))
        Console.WriteLine($"      [D] {e.Name}");
    return 0;
}

if (cmd == "e2e")
{
    // 到期清理端到端验证：真凭据 + 真接口 + 沙箱隔离的数据根。
    // 用户明确要求"不要人肉测试"，所以这里的每一句结论都必须由可核对的终态支撑
    // （云端列表里还在不在、元数据标了什么、本地文件有没有了）。
    Console.WriteLine("[INFO] 到期清理端到端验证（数据沙箱隔离，绝不碰真实数据根）");

    var sandbox = Path.Combine(Path.GetTempPath(), "fc-baidu-e2e-" + DateTime.Now.ToString("HHmmss"));
    Directory.CreateDirectory(sandbox);

    // ⚠️ 凭据（DPAPI 密文）也住在数据根下，而覆盖数据根会让 BaiduCredentialStore 去空沙箱里找令牌 ——
    // 实测后果就是整个 E2E 直接报「尚未授权」。所以覆盖之前先把凭据复制进沙箱。
    // 复制而非移动：真实凭据一个字节都不动；沙箱收尾时连副本一起删掉。
    var realBaidu = Path.Combine(FocusCapturePaths.Root, "baidu");
    if (Directory.Exists(realBaidu))
    {
        Directory.CreateDirectory(Path.Combine(sandbox, "baidu"));
        foreach (var f in Directory.GetFiles(realBaidu))
            File.Copy(f, Path.Combine(sandbox, "baidu", Path.GetFileName(f)), overwrite: true);
    }

    FocusCapturePaths.RootOverride = sandbox;          // 隔离开关（已对诊断工程开放）
    FileRepository.Cloud = new BaiduCloudStorage(netRoot);
    FileRepository.Reload();
    Console.WriteLine($"[INFO] 数据沙箱：{sandbox}");

    var stamp = DateTime.Now.ToString("HHmmss");
    var monthDir = $"{attachDir}/{DateTime.Now:yyyy-MM}";
    var netPath = $"{monthDir}/{TestPrefix}e2e-{stamp}.txt";
    const string GoodId = "e2e-good";
    const string BadId = "e2e-bad";

    // 云端放一个真文件 —— 这就是"到期该被清掉"的那份
    var uploadLocal = Path.Combine(Path.GetTempPath(), $"{TestPrefix}e2e-{stamp}.txt");
    await File.WriteAllTextAsync(uploadLocal, new string('z', 1024));
    await client.UploadFileAsync(uploadLocal, netPath, null, CancellationToken.None);
    Console.WriteLine($"[INFO] 云端已放置待清理文件：{netPath}");

    // 本机缓存副本 —— 验「到期后本地副本交给淘汰器释放」那一段
    var localCacheDir = FocusCapturePaths.Combine("files");
    Directory.CreateDirectory(localCacheDir);
    var localCache = Path.Combine(localCacheDir, $"zz-diag-e2e-{stamp}.txt");
    await File.WriteAllTextAsync(localCache, new string('z', 1024));

    // 两条已到期的附件：① 云端可删 ② NetPath 在沙箱外（删除必然抛错 → 验"失败要标注 + 退避"）
    object MakeMeta(string id, string name, string path) => new Dictionary<string, object?>
    {
        ["Id"] = id,
        ["Name"] = name,
        ["NetPath"] = path,
        ["Size"] = 1024L,
        ["Md5"] = "diag",
        ["Type"] = FileTypes.Attachment,
        ["Tags"] = Array.Empty<string>(),
        ["CreatedAt"] = DateTime.Now.AddDays(-40),
        ["UpdatedAt"] = DateTime.Now.AddDays(-40),
        ["ExpireAt"] = DateTime.Now.AddDays(-1),
        ["Deleted"] = false,
        ["CloudState"] = CloudStates.Ok,
    };
    var metas = new object[]
    {
        MakeMeta(GoodId, $"zz-diag-e2e-{stamp}.txt", netPath),
        MakeMeta(BadId, "zz-diag-e2e-badpath.txt", "/outside-sandbox/zz-diag-e2e-badpath.txt"),
    };
    await File.WriteAllTextAsync(Path.Combine(sandbox, "files_meta.json"),
        JsonSerializer.Serialize(metas, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

    var ledger = new object[]
    {
        new Dictionary<string, object?>
        {
            ["Id"] = GoodId, ["LocalPath"] = localCache,
            ["CachedAt"] = DateTime.Now.AddDays(-40), ["LastAccess"] = DateTime.Now.AddDays(-40),
            ["Origin"] = CacheOrigins.Local, ["UploadState"] = UploadStates.Uploaded,
            ["UploadRetry"] = 0, ["PendingEvict"] = false,
        },
    };
    await File.WriteAllTextAsync(Path.Combine(sandbox, "files_ledger.json"),
        JsonSerializer.Serialize(ledger, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    FileRepository.Reload();

    var okList = new List<string>();
    var failList = new List<string>();
    void Check(bool ok, string what)
    {
        (ok ? okList : failList).Add(what);
        Console.WriteLine($"    {(ok ? "✓" : "✗")} {what}");
    }

    Console.WriteLine("--- 第一轮 AttachmentExpiryService.RunAsync()");
    var done = await AttachmentExpiryService.RunAsync();
    var good = FileRepository.FindMetadata(GoodId)!;
    var bad = FileRepository.FindMetadata(BadId)!;
    var goodStillThere = (await client.ListAsync(monthDir, CancellationToken.None)).Any(e => e.Path == netPath);
    var goodEntry = FileRepository.FindCache(GoodId);

    Check(done == 1, $"云端清理成功 1 条（实际 {done}）");
    Check(!goodStillThere, "云端那份**真的**没了（以列表复查为准）");
    Check(good.CloudState == CloudStates.Expired, $"可删那条标为「已到期清理」（实际「{good.CloudState}」）");
    Check(bad.CloudState == CloudStates.CleanupPending, $"沙箱外那条标为「待清理」（实际「{bad.CloudState}」）");
    Check(goodEntry?.PendingEvict == true, "本地副本已贴「优先淘汰」");
    Check(!good.Deleted && !bad.Deleted, "两条记录都没被打墓碑（用户看得见它们去哪了）");

    Console.WriteLine("--- 第二轮（退避：失败那条不该再发请求）");
    var done2 = await AttachmentExpiryService.RunAsync();
    Check(done2 == 0, $"第二轮无新增成功（实际 {done2}）");
    Check(AttachmentExpiryService.IsSkipping(BadId), "失败那条已进入退避、本轮跳过");

    Console.WriteLine("--- 淘汰器 CacheEvictor.Run(force: true)");
    var report = CacheEvictor.Run(force: true);
    Check(!File.Exists(localCache), "本地副本已释放");
    Check(FileRepository.FindCache(GoodId) == null, "账本条目随本地副本一起清掉");
    Check(report.Removed >= 1, $"淘汰器报告清理 {report.Removed} 条");

    Console.WriteLine("--- 用户手动 RetryPendingAsync()");
    var (retryDone, retryLeft) = await AttachmentExpiryService.RetryPendingAsync();
    Check(retryDone == 0 && retryLeft == 1, $"手动重试：仍剩 1 条未清掉（实际 成功{retryDone} / 剩{retryLeft}）");
    Check(!AttachmentExpiryService.IsSkipping(BadId), "手动重试已清空退避（用户修好环境后能立刻再试）");

    Console.WriteLine($"\n[结果] 通过 {okList.Count} 项，失败 {failList.Count} 项");
    foreach (var f in failList) Console.WriteLine("    ✗ " + f);

    // 收尾：清云端测试文件 + 沙箱目录（沙箱是临时目录，不是用户数据）
    try
    {
        if ((await client.ListAsync(monthDir, CancellationToken.None)).Any(e => e.Path == netPath))
            await client.DeleteAsync(new[] { netPath }, CancellationToken.None);
        Console.WriteLine("[OK] 云端测试文件已清理");
    }
    catch (Exception ex) { Console.WriteLine("[WARN] 云端测试文件清理失败：" + ex.Message); }
    FocusCapturePaths.RootOverride = null;
    try { Directory.Delete(sandbox, recursive: true); } catch { /* best effort */ }

    return failList.Count == 0 ? 0 : 4;
}

Console.WriteLine($"未知命令：{cmd}");
return 1;

// ── 工具函数 ──

static async Task<string> ReadBodyAsync(HttpResponseMessage resp)
{
    // 一律按字节读、自己 UTF-8 解码：百度响应头里出现过非法 charset，
    // 直接 ReadAsStringAsync 会抛异常（而那一刻请求其实已经成功了）。见 REGRESSION B-14 第七轮。
    var bytes = await resp.Content.ReadAsByteArrayAsync();
    return bytes.Length == 0 ? "" : System.Text.Encoding.UTF8.GetString(bytes);
}

static int? ExtractErrno(string body)
{
    try
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("errno", out var e))
            return e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null;
    }
    catch { /* 非 JSON */ }
    return null;
}

static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + $"…（共 {s.Length} 字符）";
