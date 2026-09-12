using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FocusCapture;
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
        Console.WriteLine("=== FocusCapture 同步检查点（原 QUEST-5 验收测试）===");

        // ── 数据隔离（2026-09-11）──
        // 全程改道到临时沙箱，绝不触碰用户真实 %AppData%\FocusCapture
        //（settings.json / deleted.json / chat_history / logs 等随之全部改道）。
        // 取代旧版「备份-恢复真实 settings.json」策略 —— 旧策略若测试中途失败会留下脏数据。
        var sandbox = Path.Combine(Path.GetTempPath(), "fc-sync-sandbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        FocusCapturePaths.RootOverride = sandbox;
        Console.WriteLine($"数据沙箱（测试结束后可手动删除）：{sandbox}");
        Console.WriteLine();

        try
        {
            TestUnit();
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
