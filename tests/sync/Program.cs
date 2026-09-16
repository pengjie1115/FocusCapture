using System;
using System.Collections.Generic;
using System.IO;
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
using FocusCapture.Services.Baidu;
using FocusCapture.Services.Files;
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
    private static string _sandbox = "";

    private static void Check(bool cond, string name, string? detail = null)
    {
        Console.WriteLine((cond ? "  PASS  " : "  FAIL  ") + name);
        if (!cond)
        {
            _failed++;
            if (!string.IsNullOrEmpty(detail)) Console.WriteLine("        说明：" + detail);
        }
    }

    private static async Task<int> Main()
    {
        Console.WriteLine("=== FocusCapture 同步检查点（原 QUEST-5 验收测试）===");

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
            TestUnit();
            TestChatAttachments();       // AI 附件：图片/文档/会话引用/孤儿清理（2026-09-14）
            TestFileRepository();        // 网盘文件仓库：句柄红线 / 合并规则 / 淘汰保护 / 数据目录校验（2026-09-16）
            await TestBucketSplitting();
            await TestFirstSyncDirMissing(); // 首配子目录缺失：PROPFIND 404 → 自动 MKCOL → 重试
            await TestDualDevice();      // 验收 C（含删除流程）+ D + 自愈 E
            await TestLineIdentityAsync();   // 行身份改造：未到期待办定位/删除/跨端一致（2026-09-12）
            await TestNetworkFailure();  // 验收 C-6/C-7
            await TestCursorProtectionAsync();   // 游标保护：解密失败时游标不该推进（2026-09-11）
        }
        finally
        {
            FocusCapturePaths.RootOverride = null;   // 还原默认，避免影响进程退出前的其他逻辑
            // 全绿则清理沙箱；有失败则保留现场供排查
            if (_failed == 0) { try { Directory.Delete(sandbox, true); } catch { } }
            else Console.WriteLine($"\n[有失败] 沙箱已保留供排查：{sandbox}");
        }
        Console.WriteLine(_failed == 0 ? "\n===== ALL TESTS PASSED =====" : $"\n===== {_failed} TEST(S) FAILED =====");
        return _failed == 0 ? 0 : 1;
    }

    // ══════════════════ 网盘文件仓库（2026-09-16） ══════════════════
    //
    // 本组守护三件最容易出大事的东西：
    //   ① 红线：AI 不能凭空构造牌号去读没被用户选过的文件（方案 §6.2）
    //   ② 合并：多设备合并规则（元数据近似只增不改，所以规则能这么简单，方案 §4.3）
    //   ③ 保护：还没传上去的文件绝不能被本地淘汰清掉 —— 那是真丢数据（方案 §5.4）
    // 全程跑在主流程已建好的临时沙箱里（RootOverride），绝不触碰真实数据目录。

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

        // ── ⑥ precreate 响应解析：读不懂也绝不许抛（2026-09-16 第二次实机事故的正面回归） ──
        // 事故原貌：响应里的 block_list 是「服务端已收下的分片序号」数字数组，代码把它当 md5 字符串读，
        // 每个文件都在这一步抛 InvalidOperationException → 上传 100% 失败、网盘恒空，
        // 而报错文本里一个字都没提"百度"（排查只能靠翻日志 + 读代码）。
        // 这组的契约：读不懂一律返回空集合（= 一片都不跳过 = 全量重传），绝不抛异常。
        // 方向必须错在"多传几片"，不能错在"传不上去"。
        static HashSet<int> ParseSlices(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return BaiduNetdiskClient.ParseExistingSlices(doc.RootElement);
        }

        Check(SetsEqual(ParseSlices("""{"errno":0,"return_type":1,"block_list":[0,1],"uploadid":"x"}"""), 0, 1),
              "数字数组（服务端真实口径）必须解析成序号集合，绝不抛异常");

        Check(ParseSlices("""{"errno":0,"block_list":[]}""").Count == 0,
              "空数组必须解析成空集合");

        Check(ParseSlices("""{"errno":0,"uploadid":"x"}""").Count == 0,
              "字段缺失必须解析成空集合（结果 = 不跳过任何分片）");

        Check(ParseSlices("""{"errno":0,"block_list":"1,2"}""").Count == 0,
              "字段类型完全不对时必须解析成空集合，不许抛异常");

        Check(ParseSlices("""{"errno":0,"block_list":[null,true,{"a":1},2]}""").Count == 1,
              "数组里混进无法识别的元素时必须忽略该元素、其余照常，不许整体失败");

        Check(SetsEqual(ParseSlices("""{"errno":0,"block_list":["3","1"]}"""), 1, 3),
              "字符串形式的序号也要认（服务端改类型时不至于再瘫一次）");

        Check(ParseSlices("""{"errno":0,"block_list":[-1,0]}""").Count == 1,
              "非法序号（负数）必须被忽略，只保留合法值");

        // ── ⑦ 重试上限必须留有出口 ──
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
            var badProvider = new WebDAVProvider("http://127.0.0.1:19999/", "u", "t");
            var ebBad = new SyncEngine(sb, nb, badProvider, Backoff);
            var unlockThrew = false;
            try { await ebBad.SetTokenKeyAsync("MasterPass123"); }
            catch (InvalidOperationException) { unlockThrew = true; }
            Check(unlockThrew, "C-6 断网时无法解锁（禁止生成冲突盐）");
            nb.SaveNote("断网期间本地新增");   // 本地 100% 可用
            var rBad = await ebBad.SyncNowAsync();
            Check(!rBad.Success && !string.IsNullOrEmpty(rBad.Error), "C-6 断网同步失败（不崩）");
            Check(nb.ReadAllLines().Any(x => x.Line.Contains("断网期间")), "C-6 断网时本地功能正常");

            // 恢复联网（正确 URL）→ 自动补齐
            var ebOk = new SyncEngine(sb, nb, new WebDAVProvider(server.BaseUrl, "u", "t"), Backoff);
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

        // H1 格式判定：PDF 本期明确不做，压缩包等二进制一律拒绝
        Check(ChatAttachmentService.IsSupported("a.png") && ChatAttachmentService.IsSupported("a.docx")
              && ChatAttachmentService.IsSupported("b.py") && !ChatAttachmentService.IsSupported("c.pdf")
              && !ChatAttachmentService.IsSupported("d.zip") && !ChatAttachmentService.IsSupported("e.exe"),
              "H1 格式判定：图片/文档/代码支持；PDF（本期不做）/压缩包/可执行文件拒绝");

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
