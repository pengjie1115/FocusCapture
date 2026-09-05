using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FocusCapture.Models;
using FocusCapture.Services;

namespace FocusCapture.Services.Sync;

/// <summary>同步执行结果（UI 展示用）。</summary>
public record SyncResult(bool Success, string? Error)
{
    public static SyncResult SuccessResult => new(true, null);
    public static SyncResult NotConfigured => new(false, "未配置同步（请先配置 WebDAV 与授权码）");
    public static SyncResult Failed(string error) => new(false, error);
}

/// <summary>
/// 核心同步引擎（QUEST-5 任务6）：游标 / 回声识别 / push 全量合并 / 冲突 LWW+PrevContent / 30s 合并窗口 /
/// 自愈重置 / 指数退避 / PendingDeletes。
/// - 线程：后台执行（Timer 回调 → Task.Run），SemaphoreSlim(1) 防并发；UI 更新由订阅方 marshal 到 Dispatcher；
/// - 本机明文是唯一事实源，云端只是镜像（§5.0.4）；
/// - Content 字段承载【完整原始 MD 行】的密文（含 `- [yyyy-MM-dd HH:mm] ` 前缀，保持行格式不被破坏）；
///   ID = SHA256(相对路径 | 完整原始行)（2026-08-13 审查修正：必须用原始行，防同文件同内容撞 ID）。
/// </summary>
public class SyncEngine
{
    private readonly AppSettings _settings;
    private readonly NoteService _noteService;
    private readonly ISyncProvider _provider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _mergeTimer;   // 30s 合并窗口
    private readonly Timer _pollTimer;    // 30min 轮询
    private volatile bool _dirty;

    private byte[]? _dek;                 // 会话内派生 DEK（仅内存，不落盘）
    private string _dekSaltBase64 = "";   // 会话盐（Base64）
    private bool _saltNeedsUpload;        // 首配生成的新盐待上传

    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);
    private static readonly int[] DefaultBackoffSeconds = { 30, 120, 600 };   // 指数退避 30s/2min/10min，3 档
    private readonly int[] _backoffSeconds;

    public SyncEngine(AppSettings settings, NoteService noteService, ISyncProvider provider, int[]? backoffSeconds = null)
    {
        _settings = settings;
        _noteService = noteService;
        _provider = provider;
        _backoffSeconds = backoffSeconds ?? DefaultBackoffSeconds;
        _settings.Sync.EnsureDeviceId();
        _noteService.LinesDeleted += OnLinesDeleted;   // 删除即同步：移入回收站时生成删除标记（不再等清空回收站）
        _mergeTimer = new Timer(_ => OnMergeWindowElapsed(), null, Timeout.Infinite, Timeout.Infinite);
        _pollTimer = new Timer(_ => OnPollElapsed(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>同步状态变化（"成功"/"失败: 原因"/限流重试提示等）。订阅方负责 marshal 到 UI 线程。</summary>
    public event Action<string>? StatusChanged;

    public string ProviderName => _provider.Name;
    public string LastSyncResult => _settings.Sync.LastSyncResult;
    public string LastSyncAt => _settings.Sync.LastSyncAt;
    public bool IsMasterPasswordSet => _dek != null;

    // ── 密钥（E2EE，方案 A：授权码即钥匙，自动解锁） ──
    // v3.0 简化（2026-08-15）：废弃"用户主密码 + 恢复码"，改为用坚果云授权码派生 DEK。
    // 同账号授权码各设备一致 → 跨设备天然同 DEK（原主密码的跨设备一致性由"用户记同一密码"保证，现在由"同一授权码"保证）。
    // 授权码 DPAPI 密文存 settings.json，启动自动解密解锁；云端仍只存密文，持有授权码者才能解。

    /// <summary>旧版主密码配置检测：旧版首配强制生成恢复码（RecoveryCodeHash 必有），新版永不生成。</summary>
    public bool IsLegacyMasterPasswordMode => !string.IsNullOrEmpty(_settings.Sync.RecoveryCodeHash);

    /// <summary>
    /// 用授权码派生 DEK（仅内存，不落盘）。
    /// 盐优先级：本地缓存 → 云端 sync_meta.json → 生成新盐（仅当确认云端无 meta = 真首配设备）。
    /// 网络失败且本地无盐时**禁止生成新盐**（会与云端盐冲突，恢复联网后必失败），直接报错。
    /// </summary>
    public async Task SetTokenKeyAsync(string token, CancellationToken ct = default)
    {
        string saltBase64;
        if (!string.IsNullOrEmpty(_settings.Sync.E2eeSalt))
        {
            saltBase64 = _settings.Sync.E2eeSalt;
        }
        else
        {
            var metaFetched = true;
            string? cloudSalt = null;
            try
            {
                var meta = await _provider.GetMetaAsync(ct).ConfigureAwait(false);
                cloudSalt = meta?.SaltBase64;
            }
            catch
            {
                metaFetched = false;   // 网络错误/其他：无法确认云端状态
            }

            if (metaFetched)
            {
                if (!string.IsNullOrEmpty(cloudSalt))
                    saltBase64 = cloudSalt;
                else
                {
                    saltBase64 = Convert.ToBase64String(CryptoService.GenerateSalt());   // 真首配：云端无 meta
                    _saltNeedsUpload = true;
                }
            }
            else
            {
                throw new InvalidOperationException("无法连接云端获取 E2EE 盐，请检查网络后重试");
            }
            _settings.Sync.E2eeSalt = saltBase64;
            _settings.Save();
        }

        _dek = CryptoService.DeriveKey(token, Convert.FromBase64String(saltBase64));
        _dekSaltBase64 = saltBase64;
    }

    /// <summary>
    /// 启动自动解锁（纯本地、不联网）：DPAPI 解密已存授权码 → 派生 DEK。
    /// 本地有盐才可解锁（有授权码必有盐）；旧版主密码配置不解锁（走设置页一键升级）。
    /// </summary>
    public bool TryUnlockWithStoredToken()
    {
        if (_dek != null) return true;
        if (IsLegacyMasterPasswordMode) return false;                       // 旧版：待一键升级（MigrateFromLegacyAsync）
        if (string.IsNullOrEmpty(_settings.Sync.WebDavToken)) return false;
        var token = Models.SyncSettings.UnprotectToken(_settings.Sync.WebDavToken);
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(_settings.Sync.E2eeSalt)) return false;
        _dek = CryptoService.DeriveKey(token, Convert.FromBase64String(_settings.Sync.E2eeSalt));
        _dekSaltBase64 = _settings.Sync.E2eeSalt;
        return true;
    }

    /// <summary>
    /// 旧版一键升级（本地明文兜底，无需原主密码）：新盐 + 授权码派生新 DEK + 全量重传 + 清除旧版恢复码标记。
    /// 升级后云端密文全部用新 DEK 重写，其他旧版设备拉取到盐变更会自动刷新会话（新版）或提示重配（旧版）。
    /// </summary>
    public async Task<SyncResult> MigrateFromLegacyAsync(CancellationToken ct = default)
    {
        var token = Models.SyncSettings.UnprotectToken(_settings.Sync.WebDavToken);
        if (string.IsNullOrEmpty(token)) return SyncResult.Failed("授权码未保存，请重新填写授权码");

        var newSalt = Convert.ToBase64String(CryptoService.GenerateSalt());
        _dek = CryptoService.DeriveKey(token, Convert.FromBase64String(newSalt));
        _dekSaltBase64 = newSalt;
        _saltNeedsUpload = true;
        _settings.Sync.E2eeSalt = newSalt;
        _settings.Sync.RecoveryCodeHash = "";
        _settings.Sync.RecoveryCodeSalt = "";
        _settings.Save();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PushSaltIfNeededAsync(ct).ConfigureAwait(false);
            await PushFlowAsync(ct).ConfigureAwait(false);   // 本地明文重新加密全量重传（本地事实源 wins）
            _settings.Sync.LastSyncResult = "成功（已升级自动解锁模式）";
            _settings.Sync.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            _settings.Save();
            StatusChanged?.Invoke("已升级到自动解锁模式并全量重传");
            return SyncResult.SuccessResult;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 本机变更 → 30s 合并窗口 ──

    /// <summary>本机笔记变更后调用（NoteService.NotesChanged 订阅），启动/重置 30s 合并窗口。</summary>
    public void NotifyLocalChange()
    {
        if (_dek == null) return;                      // 未配置主密码：不自动同步
        if (!_settings.Sync.AutoSyncEnabled) return;   // 自动同步关：等手动"立即同步"（SyncNowAsync 手动路径不受此开关限制）
        _dirty = true;
        _mergeTimer.Change(MergeWindow, Timeout.InfiniteTimeSpan);
    }

    private void OnMergeWindowElapsed()
    {
        if (!_dirty) return;
        _dirty = false;
        _ = Task.Run(() => SyncNowAsync(auto: true));
    }

    // ── 同步主流程 ──

    /// <summary>
    /// 立即同步：push 盐（首配）→ pull（对账合并）→ push（全量合并上传）。
    /// auto=true（合并窗口/轮询触发）时遇限流/网络错误指数退避 30s/2min/10min，连续 3 次失败停止自动重试；
    /// auto=false（手动"立即同步"）失败直接返回原因，等用户再点。
    /// </summary>
    public async Task<SyncResult> SyncNowAsync(bool auto = false)
    {
        if (_dek == null) return SyncResult.NotConfigured;
        if (!LicenseGate.IsAllowed(LicenseGate.FeatureSync))
            return SyncResult.Failed("同步是 FocusCapture 专业版功能，购买后即可使用");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var attempts = 0;
            while (true)
            {
                attempts++;
                try
                {
                    await PushSaltIfNeededAsync(CancellationToken.None).ConfigureAwait(false);
                    if (await PullFlowAsync().ConfigureAwait(false))
                    {
                        // 云端盐已变更（他端升级/重置密钥）→ 新版：用本地授权码 + 新盐自动刷新会话，继续 push；
                        // 无授权码（未配置）→ 中止并提示重配
                        if (!TryRefreshSessionFromCloudSalt())
                            return SyncResult.Failed("云端密钥已重置，请重新配置授权码");
                    }
                    await PushFlowAsync().ConfigureAwait(false);
                    _settings.Sync.LastSyncResult = "成功";
                    _settings.Sync.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    _settings.Save();
                    StatusChanged?.Invoke("成功");
                    return SyncResult.SuccessResult;
                }
                catch (SyncProviderException ex) when (auto && (ex.IsRateLimit || ex.IsNetwork) && attempts <= _backoffSeconds.Length)
                {
                    var delay = TimeSpan.FromSeconds(_backoffSeconds[attempts - 1]);
                    StatusChanged?.Invoke($"同步失败，{delay.TotalSeconds:0}s 后重试（{attempts}/{_backoffSeconds.Length}）：{ex.Message}");
                    await Task.Delay(delay).ConfigureAwait(false);
                }
                catch (SyncProviderException ex)
                {
                    var msg = ex.IsAuth
                        ? "坚果云授权码无效，请在坚果云客户端（或手机 APP）『设置 → 第三方应用管理』重新生成"
                        : ex.Message;
                    return Fail(msg);
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 只拉取（不推送）：把云端变更合并到本地。灵感速览面板的"↓ 下载"按钮专用入口。
    /// 与 SyncNowAsync 流程一致但跳过 PushFlowAsync——避免用户点下载时把本地未确认内容顺手推上云端。
    /// </summary>
    public async Task<SyncResult> PullOnlyAsync(CancellationToken ct = default)
    {
        if (_dek == null) return SyncResult.NotConfigured;
        if (!LicenseGate.IsAllowed(LicenseGate.FeatureSync))
            return SyncResult.Failed("同步是 FocusCapture 专业版功能，购买后即可使用");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await PushSaltIfNeededAsync(ct).ConfigureAwait(false);
            if (await PullFlowAsync().ConfigureAwait(false))
            {
                // 云端盐已变更（他端升级/重置密钥）→ 用本地授权码 + 新盐自动刷新会话
                if (!TryRefreshSessionFromCloudSalt())
                    return SyncResult.Failed("云端密钥已重置，请重新配置授权码");
            }
            _settings.Sync.LastSyncResult = "成功（仅拉取）";
            _settings.Sync.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            _settings.Save();
            StatusChanged?.Invoke("成功（仅拉取）");
            return SyncResult.SuccessResult;
        }
        catch (SyncProviderException ex)
        {
            var msg = ex.IsAuth
                ? "坚果云授权码无效，请在坚果云客户端（或手机 APP）『设置 → 第三方应用管理』重新生成"
                : ex.Message;
            return Fail(msg);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 拉取侧：对账合并（回声识别 / 软删落地 / 密钥重置盐比对） ──

    /// <summary>返回 true = 云端盐已变更（他端密钥重置），调用方必须中止 push 并提示重输主密码。</summary>
    private async Task<bool> PullFlowAsync()
    {
        // 密钥重置检测：云端盐 ≠ 本地会话盐 → 刷新本地缓存盐（云端为权威，盐由最新重置者写入），
        // 返回 true 由调用方刷新会话（自动解锁模式下用授权码+新盐自动刷新，无需用户干预）
        var cloudSalt = await GetCloudSaltAsync().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(cloudSalt) && cloudSalt != _dekSaltBase64)
        {
            _settings.Sync.E2eeSalt = cloudSalt;
            _settings.Save();
            return true;
        }

        var since = string.IsNullOrEmpty(_settings.Sync.LastCursor) ? null : _settings.Sync.LastCursor;
        var pull = await _provider.PullAsync(since, CancellationToken.None).ConfigureAwait(false);

        var localLines = new Dictionary<string, (string RelativePath, string Line, NoteEntry Entry)>(StringComparer.Ordinal);
        foreach (var x in _noteService.ReadAllLines())
            localLines[SyncNote.ComputeId(x.RelativePath, x.Line)] = x;

        // 回收站清理 / 恢复传播的批量收集（单次扫描目录，避免逐条重复 IO）
        var binRemovals = new List<(string RelativePath, string Line)>();

        foreach (var cloud in pull.Notes)
        {
            if (cloud.DeviceId == _settings.Sync.DeviceId) continue;   // 回声识别：自己推的不拉回

            string content;
            try
            {
                content = CryptoService.Decrypt(_dek!, cloud.Content);
            }
            catch (CryptographicException)
            {
                // 密钥不对/数据损坏：跳过该条，不崩溃、不改动本地（§6 未知处理 4）
                StatusChanged?.Invoke("云端数据解密失败，可能授权码不正确或云端密钥已重置");
                continue;
            }

            // 本机刚恢复过的行：云端旧删除/清空标记作废（push 会以活行覆盖墓碑）
            if (_settings.Sync.PendingRestores.Contains(cloud.Id)) continue;

            if (cloud.Purged)
            {
                // 彻底删除传播（他端已清空回收站）：清本机回收站对应记录；本机还有活行则直接删（不入回收站）
                var relPurged = ResolveRelativePath(cloud);
                binRemovals.Add((relPurged, content));
                if (localLines.TryGetValue(cloud.Id, out var live))
                    _noteService.RemoveLines(live.RelativePath, new HashSet<string> { live.Line });
                continue;
            }

            if (cloud.Deleted)
            {
                // 软删落地：本地有对应行 → 移入回收站（先写回收站成功再删行，QUEST-5 §2 铁律）
                if (localLines.TryGetValue(cloud.Id, out var local))
                {
                    // 本机行时间戳 ≥ 删除时间 → 本机是删除后重新录入的同内容行，本机 wins（push 覆盖墓碑）
                    var tombLocal = ParseLocalDateTime(cloud.UpdatedAt);
                    var lineTs = TryParseLineTimestamp(local.Line);
                    if (lineTs.HasValue && lineTs.Value >= tombLocal) continue;
                    if (_noteService.RecycleBin.Add(local.RelativePath, new[] { local.Line }))
                        _noteService.RemoveLines(local.RelativePath, new HashSet<string> { local.Line });
                }
                else
                {
                    // 本机没有该行（如 B 从未见过这条）：仅落回收站，供查看/恢复（回收站双向同步）
                    _noteService.RecycleBin.Add(ResolveRelativePath(cloud), new[] { content });
                }
            }
            else
            {
                if (!localLines.ContainsKey(cloud.Id))
                {
                    // 云端新行（他端新增/他端恢复）→ 按 Tags/灵感日规则写回原文件（保持原始行格式不变）
                    var relativePath = ResolveRelativePath(cloud);
                    _noteService.AppendLine(relativePath, content);
                    binRemovals.Add((relativePath, content));   // 他端恢复传播：清掉本机回收站的对应删除记录
                }
                // 本地已有同 ID 行：同 ID = 同内容，无需操作（本地事实源 wins）
            }
        }

        if (binRemovals.Count > 0)
            _noteService.RecycleBin.RemoveMatchingBatch(binRemovals);

        // 游标推进（只进不退）
        if (!string.IsNullOrEmpty(pull.NewSince))
            _settings.Sync.LastCursor = pull.NewSince;
        _settings.Save();
        return false;
    }

    // ── 推送侧：全量合并 + 整桶覆盖（Quest-5 审查修正：合并基础 = 云端全量，防覆盖抹掉他端数据） ──

    private async Task PushFlowAsync(CancellationToken ct = default)
    {
        // 1) 本机集合：明文行 → SyncNote（Content=完整原始行密文；Tags 从文件名；ID 用原始行哈希）
        var localNotes = new List<SyncNote>();
        var localTsById = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var (rel, line, entry) in _noteService.ReadAllLines())
        {
            var ts = NoteService.ToUtcIsoString(entry.Timestamp);
            var id = SyncNote.ComputeId(rel, line);
            localNotes.Add(new SyncNote
            {
                Id = id,
                Content = CryptoService.Encrypt(_dek!, line),
                Tags = string.IsNullOrEmpty(entry.Tag) ? [] : new[] { entry.Tag },
                CreatedAt = ts,
                UpdatedAt = ts,
                DeviceId = _settings.Sync.DeviceId,
            });
            localTsById[id] = entry.Timestamp;
        }

        // 2) 合并（2026-08-13 审查修正，deviceId 稳定版）：
        //    - 云端全量保留（含他端行、本机旧推的行、软删驻留行）——本机推过的行不重复生成，
        //      否则 DeviceId 被改写、每次同步云端桶都变（回声识别失效）；
        //    - 本机新行（云端没有该 ID 的）TryAdd 加入——MD 只增不减：修改=追加新行（新 ID），同 ID 行内容必相同；
        //    - 本机活行 vs 云端墓碑：恢复清单内、或行时间戳晚于删除时间（删除后重录）→ 覆盖墓碑
        //      （UpdatedAt=now 保证他端增量拉取可见），否则墓碑保持（本机推过的删除不复活）；
        //    - PendingDeletes 覆盖（本机软删 wins），被覆盖的云端版本进 PrevContent 快照（密文）。
        var byId = new Dictionary<string, SyncNote>(StringComparer.Ordinal);
        var cloudAll = await _provider.FullAsync(ct).ConfigureAwait(false);
        foreach (var cloud in cloudAll) byId[cloud.Id] = cloud;
        foreach (var n in localNotes)
        {
            if (byId.TryGetValue(n.Id, out var existing) && existing.Deleted)
            {
                var tombLocal = ParseLocalDateTime(existing.UpdatedAt);
                var isRestore = _settings.Sync.PendingRestores.Contains(n.Id);
                var isRecreated = localTsById.TryGetValue(n.Id, out var lineTs) && lineTs >= tombLocal;
                if (isRestore || isRecreated)
                {
                    n.UpdatedAt = NoteService.ToUtcIsoString(DateTime.Now);   // 视为最新变更，他端增量拉取可见
                    byId[n.Id] = n;
                }
                continue;   // 墓碑仍新 → 保持墓碑，不复活
            }
            byId.TryAdd(n.Id, n);
        }
        foreach (var d in _settings.Sync.PendingDeletes)
        {
            if (byId.TryGetValue(d.Id, out var old) && old.Deleted != d.Deleted)
                d.PrevContent = old.Content;   // 被覆盖方快照（密文原样，内容不外泄）
            byId[d.Id] = d;
        }

        // 3) 整桶覆盖上传（Provider 内分桶 PUT + 孤儿桶清理 + meta 更新）
        var result = await _provider.PushAsync(byId.Values.ToList(), _settings.Sync.LastCursor, ct).ConfigureAwait(false);

        // 4) 成功后清 PendingDeletes / PendingRestores + 推进游标
        _settings.Sync.PendingDeletes.Clear();
        _settings.Sync.PendingRestores.Clear();
        if (!string.IsNullOrEmpty(result.NewSince))
            _settings.Sync.LastCursor = result.NewSince;
        _settings.Save();
    }

    // ── 自愈 / 自动轮询 / 清空回收站联动 ──

    /// <summary>回收站清空后调用：本机软删进 PendingDeletes，随下次 push 传播。</summary>
    public void QueuePendingDelete(SyncNote deletedNote)
    {
        deletedNote.DeviceId = _settings.Sync.DeviceId;
        _settings.Sync.PendingDeletes.Add(deletedNote);
        _settings.Save();
        NotifyLocalChange();
    }

    /// <summary>
    /// 清空回收站联动（QUEST-5 第五步 2）：把全部被清空记录转为 Deleted=true + Purged=true 的 SyncNote 压入 PendingDeletes。
    /// Content 用完整原始行密文（与普通行一致）；Tags 规则同 ReadAllLines（灵感_*.md → []，其余 → [文件名]）。
    /// CreatedAt 用原始行时间戳；**UpdatedAt 必须用软删发生时间（Now）**——否则增量拉取
    /// （UpdatedAt &gt;= since 过滤）会把软删标记漏掉（原行时间戳 ≤ 他端游标）。
    /// Purged=true 表示"彻底删除"：他端删除本地行并清除回收站记录（回收站双向同步），不再入回收站。
    /// </summary>
    public void QueueRecycleBinPurge(List<RecycleBinEntry> purgedEntries)
    {
        if (_dek == null) return;
        var nowUtc = NoteService.ToUtcIsoString(DateTime.Now);
        foreach (var entry in purgedEntries)
        {
            foreach (var line in entry.Lines)
            {
                var ts = ParseLineTimestamp(line);
                var tag = GetTagFromFileName(entry.RelativePath);
                _settings.Sync.PendingDeletes.Add(new SyncNote
                {
                    Id = SyncNote.ComputeId(entry.RelativePath, line),
                    Content = CryptoService.Encrypt(_dek, line),
                    Tags = string.IsNullOrEmpty(tag) ? [] : new[] { tag },
                    CreatedAt = NoteService.ToUtcIsoString(ts),
                    UpdatedAt = nowUtc,
                    Deleted = true,
                    Purged = true,
                    DeviceId = _settings.Sync.DeviceId,
                });
            }
        }
        _settings.Save();
        NotifyLocalChange();
    }

    /// <summary>
    /// 回收站恢复联动：把恢复的行 ID 压入 PendingRestores，push 时以活行覆盖云端删除标记
    /// （防止恢复的行被云端旧墓碑再次"删掉"），并触发同步传播到其他设备。
    /// </summary>
    public void QueuePendingRestore(string relativePath, IEnumerable<string> lines)
    {
        var added = false;
        foreach (var line in lines)
        {
            var id = SyncNote.ComputeId(relativePath, line);
            if (!_settings.Sync.PendingRestores.Contains(id))
            {
                _settings.Sync.PendingRestores.Add(id);
                added = true;
            }
        }
        if (!added) return;
        _settings.Save();
        NotifyLocalChange();
    }

    /// <summary>
    /// 删除即同步（NoteService.LinesDeleted 订阅入口）：移入回收站的行立即生成 Deleted=true 墓碑
    /// 压入 PendingDeletes，随下次 push 以覆盖形式上传——云端活动记录只剩未删的行。
    /// </summary>
    private void OnLinesDeleted(string relativePath, IReadOnlyList<string> lines)
    {
        if (_dek == null || lines.Count == 0) return;   // 未配置同步：无云端可传播
        var nowUtc = NoteService.ToUtcIsoString(DateTime.Now);
        var tag = GetTagFromFileName(relativePath);
        foreach (var line in lines)
        {
            _settings.Sync.PendingDeletes.Add(new SyncNote
            {
                Id = SyncNote.ComputeId(relativePath, line),
                Content = CryptoService.Encrypt(_dek, line),
                Tags = string.IsNullOrEmpty(tag) ? [] : new[] { tag },
                CreatedAt = NoteService.ToUtcIsoString(ParseLineTimestamp(line)),
                UpdatedAt = nowUtc,
                Deleted = true,
                DeviceId = _settings.Sync.DeviceId,
            });
        }
        _settings.Save();
        NotifyLocalChange();
    }

    private static DateTime ParseLineTimestamp(string line)
    {
        return TryParseLineTimestamp(line) ?? DateTime.Now;
    }

    /// <summary>解析 MD 行的本地时间戳（分钟精度）；解析失败返回 null（供"删除后重录"新旧判定用，不兜底 Now）。</summary>
    private static DateTime? TryParseLineTimestamp(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line, @"- \[(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2})\]");
        return m.Success && DateTime.TryParse($"{m.Groups[1].Value} {m.Groups[2].Value}", out var ts)
            ? ts
            : null;
    }

    private static string GetTagFromFileName(string relativePath)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        return name.StartsWith("灵感_", StringComparison.Ordinal) ? "" : name;
    }

    /// <summary>
    /// 自愈重置：清空本地游标 + 清空云端全部桶（PushAsync 空集 → 孤儿桶全删）→ 全量重新上传。
    /// UI 二次确认由调用方负责（QUEST-5 第七步 6）。
    /// </summary>
    public async Task<SyncResult> ResetSyncAsync(CancellationToken ct = default)
    {
        if (_dek == null) return SyncResult.NotConfigured;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _provider.PushAsync(Array.Empty<SyncNote>(), null, ct).ConfigureAwait(false);   // 清空云端桶
            _settings.Sync.LastCursor = "";
            _settings.Sync.PendingDeletes.Clear();
            _settings.Sync.PendingRestores.Clear();
            _settings.Save();
            await PushFlowAsync(ct).ConfigureAwait(false);   // 全量重传
            _settings.Sync.LastSyncResult = "成功（已重置并全量重传）";
            _settings.Sync.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            _settings.Save();
            StatusChanged?.Invoke("同步状态已重置，全量重传完成");
            return SyncResult.SuccessResult;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>启动自动同步：立即拉一次 + 30 分钟轮询（AutoSyncEnabled 时由启动侧调用）。</summary>
    public void StartAutoSync()
    {
        _pollTimer.Change(PollInterval, PollInterval);
        _ = Task.Run(() => SyncNowAsync(auto: true));
    }

    public void StopAutoSync() => _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);

    private void OnPollElapsed()
    {
        if (_settings.Sync.AutoSyncEnabled && _dek != null)
            _ = Task.Run(() => SyncNowAsync(auto: true));
    }

    // ── 内部帮助 ──

    private async Task PushSaltIfNeededAsync(CancellationToken ct)
    {
        if (!_saltNeedsUpload) return;
        await _provider.SaveSaltAsync(_dekSaltBase64, ct).ConfigureAwait(false);
        _saltNeedsUpload = false;
    }

    private async Task<string?> GetCloudSaltAsync()
    {
        try
        {
            var meta = await _provider.GetMetaAsync(CancellationToken.None).ConfigureAwait(false);
            return meta?.SaltBase64;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>云端盐已变更（他端升级/重置）→ 用本地授权码 + 新盐（_settings.Sync.E2eeSalt 已由 PullFlow 刷新）重派生 DEK。</summary>
    private bool TryRefreshSessionFromCloudSalt()
    {
        if (string.IsNullOrEmpty(_settings.Sync.WebDavToken) || string.IsNullOrEmpty(_settings.Sync.E2eeSalt))
            return false;
        var token = Models.SyncSettings.UnprotectToken(_settings.Sync.WebDavToken);
        if (string.IsNullOrEmpty(token)) return false;
        try
        {
            _dek = CryptoService.DeriveKey(token, Convert.FromBase64String(_settings.Sync.E2eeSalt));
            _dekSaltBase64 = _settings.Sync.E2eeSalt;
            StatusChanged?.Invoke("云端密钥已变更，已自动刷新解锁");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>云端行写回本地时的相对路径：有 Tag → {Tag}.md；灵感行 → 灵感_{CreatedAt 本地日期}.md。</summary>
    private static string ResolveRelativePath(SyncNote cloud)
    {
        if (cloud.Tags is { Length: > 0 } && !string.IsNullOrWhiteSpace(cloud.Tags[0]))
            return $"{cloud.Tags[0]}.md";
        var local = ParseLocalDateTime(cloud.CreatedAt);
        return $"灵感_{local:yyyy-MM-dd}.md";
    }

    private static DateTime ParseLocalDateTime(string isoUtc)
    {
        if (DateTimeOffset.TryParse(isoUtc, out var dto))
            return dto.ToLocalTime().DateTime;
        return DateTime.Now;
    }

    private SyncResult Fail(string error)
    {
        _settings.Sync.LastSyncResult = "失败: " + error;
        _settings.Sync.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        _settings.Save();
        StatusChanged?.Invoke(_settings.Sync.LastSyncResult);
        return SyncResult.Failed(error);
    }
}
