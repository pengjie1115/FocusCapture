using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.Sync;

/// <summary>
/// FocusCapture QUEST-5 同步验收测试（单机双设备模拟，验收 B/C/D/E/F）。
/// - 本地 WebDAV 桩（HttpListener 实现 PROPFIND/PUT/GET/DELETE/MKCOL）替代真实坚果云（QUEST-5 §8 联调说明）；
/// - 两台设备 A/B：独立 NotesPath + 独立 AppSettings（内存）+ 同一桩地址；
/// - 测试前备份/恢复真实 settings.json（SyncEngine 内部会调 AppSettings.Save）。
/// </summary>
internal static class Program
{
    private static int _failed;

    private static void Check(bool cond, string name)
    {
        Console.WriteLine((cond ? "  PASS  " : "  FAIL  ") + name);
        if (!cond) _failed++;
    }

    private static async Task<int> Main()
    {
        Console.WriteLine("=== FocusCapture QUEST-5 同步验收测试 ===");
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FocusCapture", "settings.json");
        string? backup = null;
        if (File.Exists(settingsPath)) backup = File.ReadAllText(settingsPath);
        try
        {
            TestUnit();
            await TestBucketSplitting();
            await TestFirstSyncDirMissing(); // 首配子目录缺失：PROPFIND 404 → 自动 MKCOL → 重试
            await TestDualDevice();      // 验收 C（含删除流程）+ 回声 + 密钥重置 D + 自愈 E
            await TestNetworkFailure();  // 验收 C-6/C-7
        }
        finally
        {
            try
            {
                if (backup != null) File.WriteAllText(settingsPath, backup);
                else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            }
            catch { /* 恢复失败不影响测试结论 */ }
        }
        Console.WriteLine(_failed == 0 ? "\n===== ALL TESTS PASSED =====" : $"\n===== {_failed} TEST(S) FAILED =====");
        return _failed == 0 ? 0 : 1;
    }

    // ── 单测（验收 F：确定性 ID / 加密往返 / 恢复码 / 强度校验） ──

    private static void TestUnit()
    {
        Console.WriteLine("[单测] 确定性 ID / E2EE / 恢复码");
        var id1 = SyncNote.ComputeId("灵感_2026-08-12.md", "- [2026-08-12 10:00] 你好");
        var id2 = SyncNote.ComputeId("灵感_2026-08-12.md", "- [2026-08-12 10:00] 你好");
        var id3 = SyncNote.ComputeId("灵感_2026-08-12.md", "- [2026-08-12 10:01] 你好");
        Check(id1 == id2 && id1 != id3 && id1.Length == 32, "确定性 ID：同行同 ID / 不同行不同 ID");
        Check(id1 != SyncNote.ComputeId("灵感_2026-08-12.md", "你好"),
            "ID 基于完整原始行（含时间戳前缀，审查修正点）");

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
            var provider = new WebDAVProvider(BaseUrl, "u", "t");
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
            var na = new NoteService(sa);
            var nb = new NoteService(sb);
            var pa = new WebDAVProvider(BaseUrl, "u", "t");
            var pb = new WebDAVProvider(BaseUrl, "u", "t");
            var ea = new SyncEngine(sa, na, pa, Backoff);
            var eb = new SyncEngine(sb, nb, pb, Backoff);

            // A 首配（空库）：生成盐 → 上传 sync_meta.json
            await ea.SetMasterPasswordAsync("MasterPass123");
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
            await eb.SetMasterPasswordAsync("MasterPass123");
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

            // A 删除 1 条（未清空回收站）→ 云端保留 → B 不受影响
            var aEntry = na.ReadAllLines().First(x => x.Line.Contains("A 笔记 2"));
            Check(na.DeleteNote(aEntry.Entry), "A 删除成功（进回收站，未清空）");
            Check(na.RecycleBin.List().Count == 1, "A 回收站有 1 条");
            await ea.SyncNowAsync();
            await eb.SyncNowAsync();
            Check(nb.ReadAllLines().Count == 5, "C-4 未清空前 B 行数不变（云端无软删标记）");

            // A 清空回收站 → 软删传播 → B 对应行进回收站
            var purged = na.RecycleBin.PurgeAll();
            Check(purged.Count == 1, "清空回收站返回记录");
            ea.QueueRecycleBinPurge(purged);
            await ea.SyncNowAsync();
            await eb.SyncNowAsync();
            var bLines5 = nb.ReadAllLines();
            var deletedNotes = server.ListFiles().Where(f => f.StartsWith("notes-"))
                .Select(f => SyncBucket.FromJson(server.ReadFile(f))!)
                .SelectMany(x => x.Notes).Where(n => n.Deleted).ToList();
            var bLocalIds = bLines5.Select(x => SyncNote.ComputeId(x.RelativePath, x.Line)).ToList();
            Console.WriteLine($"  [诊断 C-5] B 行数={bLines5.Count} B 回收站={nb.RecycleBin.List().Count} 云端软删数={deletedNotes.Count} B.LastCursor={sb.Sync.LastCursor}");
            foreach (var d in deletedNotes)
                Console.WriteLine($"  [诊断 C-5] 云端软删 Id={d.Id} UpdatedAt={d.UpdatedAt} B 本地含该 ID={bLocalIds.Contains(d.Id)}");
            Check(bLines5.Count == 4, "C-5 软删传播后 B 少 1 行");
            Check(nb.RecycleBin.List().Count == 1, "C-5 B 对应行进本地回收站（可恢复）");
            var bDeleted = server.ListFiles().Where(f => f.StartsWith("notes-"))
                .Select(f => SyncBucket.FromJson(server.ReadFile(f))!)
                .SelectMany(x => x.Notes)
                .Where(n => n.Deleted);
            Check(bDeleted.Any(), "C-5 云端该笔记 Deleted=true");

            // 回声识别：连续 3 轮双向同步后云端桶无变化（无死循环/无重复推送）
            var before = server.ListFiles().Where(f => f.StartsWith("notes-"))
                .ToDictionary(f => f, f => server.ReadFile(f));
            for (int i = 0; i < 3; i++) { await ea.SyncNowAsync(); await eb.SyncNowAsync(); }
            var after = server.ListFiles().Where(f => f.StartsWith("notes-"))
                .ToDictionary(f => f, f => server.ReadFile(f));
            Check(before.Count == after.Count && before.All(kv =>
                after.TryGetValue(kv.Key, out var v) && v == kv.Value), "回声识别：连续 3 轮云端桶无变化");

            // D 密钥重置：A 重置主密码（新盐+全量重传）→ B 旧密码同步被中止 → B 新密码恢复
            Check((await ea.ResetMasterPasswordAsync("NewPass456")).Success, "D A 重置主密码（全量重传）");
            var rOld = await eb.SyncNowAsync();
            Check(!rOld.Success && (rOld.Error ?? "").Contains("密钥已重置"), "D B 旧密码同步被中止（云端密钥已重置提示）");
            await eb.SetMasterPasswordAsync("NewPass456");
            Check((await eb.SyncNowAsync()).Success, "D B 新密码恢复同步");
            Check(nb.ReadAllLines().Count == 4, "D B 用新密码解密拉取全部数据");

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
            var pa = new WebDAVProvider(BaseUrl, "u", "t");
            var ea = new SyncEngine(sa, na, pa, Backoff);

            // 首配：云端无目录、无 sync_meta → 生成盐 → 同步（Pull 侧应自动建目录）
            await ea.SetMasterPasswordAsync("MasterPass123");
            var r = await ea.SyncNowAsync();
            Check(r.Success, "首配目录缺失：SyncNow 自动建目录并成功" + (r.Success ? "" : " ← " + r.Error));
            Check(server.ListFiles().Contains("sync_meta.json"), "首配目录缺失：sync_meta.json 已上传（MKCOL 生效）");

            // 换新目录再次首配（模拟换账号/新设备）：同样自动建
            using var server2 = new TestWebDavServer(Path.Combine(root, "cloud2"), createRoot: false);
            var pa2 = new WebDAVProvider(BaseUrl, "u", "t");
            var ea2 = new SyncEngine(sa, na, pa2, Backoff);
            await ea2.SetMasterPasswordAsync("MasterPass123");
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
            var pa = new WebDAVProvider(BaseUrl, "u", "t");
            var ea = new SyncEngine(sa, na, pa, Backoff);
            await ea.SetMasterPasswordAsync("MasterPass123");
            na.SaveNote("断网测试行");
            await ea.SyncNowAsync();

            // C-6 断网（改错 URL）：无法解锁（不生成冲突盐）→ 本地功能正常 → 恢复后补齐
            var badProvider = new WebDAVProvider("http://127.0.0.1:19999/", "u", "t");
            var ebBad = new SyncEngine(sb, nb, badProvider, Backoff);
            var unlockThrew = false;
            try { await ebBad.SetMasterPasswordAsync("MasterPass123"); }
            catch (InvalidOperationException) { unlockThrew = true; }
            Check(unlockThrew, "C-6 断网时无法解锁（禁止生成冲突盐）");
            nb.SaveNote("断网期间本地新增");   // 本地 100% 可用
            var rBad = await ebBad.SyncNowAsync();
            Check(!rBad.Success && !string.IsNullOrEmpty(rBad.Error), "C-6 断网同步失败（不崩）");
            Check(nb.ReadAllLines().Any(x => x.Line.Contains("断网期间")), "C-6 断网时本地功能正常");

            // 恢复联网（正确 URL）→ 自动补齐
            var ebOk = new SyncEngine(sb, nb, new WebDAVProvider(BaseUrl, "u", "t"), Backoff);
            await ebOk.SetMasterPasswordAsync("MasterPass123");
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

    private const string BaseUrl = "http://127.0.0.1:18080/";
    private static readonly int[] Backoff = { 1, 1, 1 };   // 测试注入小退避（SyncEngine 构造参数）
}

/// <summary>
/// 最小 WebDAV 桩：PROPFIND/PUT/GET/DELETE/MKCOL（QUEST-5 §8 联调替代方案）。
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

    public TestWebDavServer(string root, bool createRoot = true)
    {
        _root = root;
        if (createRoot) Directory.CreateDirectory(root);
        _listener = new TcpListener(IPAddress.Loopback, 18080);
        _listener.Start();
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
