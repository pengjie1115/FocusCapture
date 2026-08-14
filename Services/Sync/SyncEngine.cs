using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FocusCapture.Models;

namespace FocusCapture.Services.Sync;

/// <summary>同步执行结果（UI 展示用）。</summary>
public record SyncResult(bool Success, string? Error)
{
    public static SyncResult SuccessResult => new(true, null);
    public static SyncResult NotConfigured => new(false, "未配置同步（请先配置 WebDAV 与主密码）");
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
        _mergeTimer = new Timer(_ => OnMergeWindowElapsed(), null, Timeout.Infinite, Timeout.Infinite);
        _pollTimer = new Timer(_ => OnPollElapsed(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>同步状态变化（"成功"/"失败: 原因"/限流重试提示等）。订阅方负责 marshal 到 UI 线程。</summary>
    public event Action<string>? StatusChanged;

    public string ProviderName => _provider.Name;
    public string LastSyncResult => _settings.Sync.LastSyncResult;
    public string LastSyncAt => _settings.Sync.LastSyncAt;
    public bool IsMasterPasswordSet => _dek != null;

    // ── 主密码（E2EE） ──

    /// <summary>
    /// 设置主密码并派生 DEK（仅内存，不落盘）。
    /// 盐优先级：本地缓存 → 云端 sync_meta.json → 生成新盐（首配设备，标记待上传到云端）。
    /// </summary>
    public async Task SetMasterPasswordAsync(string password, CancellationToken ct = default)
    {
        string saltBase64;
        if (!string.IsNullOrEmpty(_settings.Sync.E2eeSalt))
        {
            saltBase64 = _settings.Sync.E2eeSalt;
        }
        else
        {
            try
            {
                var meta = await _provider.GetMetaAsync(ct).ConfigureAwait(false);
                saltBase64 = meta?.SaltBase64 ?? "";
            }
            catch
            {
                saltBase64 = "";
            }
            if (string.IsNullOrEmpty(saltBase64))
            {
                saltBase64 = Convert.ToBase64String(CryptoService.GenerateSalt());
                _saltNeedsUpload = true;
            }
            _settings.Sync.E2eeSalt = saltBase64;
            _settings.Save();
        }

        _dek = CryptoService.DeriveKey(password, Convert.FromBase64String(saltBase64));
        _dekSaltBase64 = saltBase64;
    }

    /// <summary>密钥重置：新主密码 → 新盐 → 新 DEK，全量重传（覆盖云端旧密文）。恢复码校验由 UI 层完成。</summary>
    public async Task<SyncResult> ResetMasterPasswordAsync(string newPassword, CancellationToken ct = default)
    {
        var newSalt = Convert.ToBase64String(CryptoService.GenerateSalt());
        _dek = CryptoService.DeriveKey(newPassword, Convert.FromBase64String(newSalt));
        _dekSaltBase64 = newSalt;
        _saltNeedsUpload = true;
        _settings.Sync.E2eeSalt = newSalt;
        _settings.Save();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PushSaltIfNeededAsync(ct).ConfigureAwait(false);
            await PushFlowAsync(ct).ConfigureAwait(false);   // 用本地明文重新加密全量重传（PushAsync 全量合并时本机 wins）
            _settings.Sync.LastSyncResult = "成功（密钥已重置并全量重传）";
            _settings.Sync.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            _settings.Save();
            StatusChanged?.Invoke("密钥已重置并全量重传");
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
                        // 云端盐已变更（他端密钥重置）→ 本机 DEK 已失效，中止 push 防止旧 DEK 密文污染云端，等重输主密码
                        _settings.Sync.LastSyncResult = "失败: 云端密钥已重置，请重新输入主密码";
                        _settings.Sync.LastSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                        _settings.Save();
                        StatusChanged?.Invoke(_settings.Sync.LastSyncResult);
                        return SyncResult.Failed("云端密钥已重置，请重新输入主密码");
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
                        ? "坚果云授权码无效，请在坚果云网页端『安全-第三方应用管理』重新生成"
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

    // ── 拉取侧：对账合并（回声识别 / 软删落地 / 密钥重置盐比对） ──

    /// <summary>返回 true = 云端盐已变更（他端密钥重置），调用方必须中止 push 并提示重输主密码。</summary>
    private async Task<bool> PullFlowAsync()
    {
        // 密钥重置检测：云端盐 ≠ 本地会话盐 → 刷新本地缓存盐（云端为权威，盐由最新重置者写入），
        // 中止本次同步并提示重输主密码后用新盐派生 DEK（QUEST-5 审查补充，不得静默当数据损坏）
        var cloudSalt = await GetCloudSaltAsync().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(cloudSalt) && cloudSalt != _dekSaltBase64)
        {
            _settings.Sync.E2eeSalt = cloudSalt;
            _settings.Save();
            StatusChanged?.Invoke("云端密钥可能已被重置，请重新输入主密码刷新会话");
            return true;
        }

        var since = string.IsNullOrEmpty(_settings.Sync.LastCursor) ? null : _settings.Sync.LastCursor;
        var pull = await _provider.PullAsync(since, CancellationToken.None).ConfigureAwait(false);

        var localLines = new Dictionary<string, (string RelativePath, string Line, NoteEntry Entry)>(StringComparer.Ordinal);
        foreach (var x in _noteService.ReadAllLines())
            localLines[SyncNote.ComputeId(x.RelativePath, x.Line)] = x;

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
                StatusChanged?.Invoke("云端数据解密失败，可能主密码不正确或云端密钥已重置");
                continue;
            }

            if (cloud.Deleted)
            {
                // 软删落地：本地有对应行 → 移入回收站（先写回收站成功再删行，QUEST-5 §2 铁律）
                if (localLines.TryGetValue(cloud.Id, out var local))
                {
                    if (_noteService.RecycleBin.Add(local.RelativePath, new[] { local.Line }))
                        _noteService.RemoveLines(local.RelativePath, new HashSet<string> { local.Line });
                }
            }
            else
            {
                if (!localLines.ContainsKey(cloud.Id))
                {
                    // 云端新行（他端新增）→ 按 Tags/灵感日规则写回原文件（保持原始行格式不变）
                    var relativePath = ResolveRelativePath(cloud);
                    _noteService.AppendLine(relativePath, content);
                }
                // 本地已有同 ID 行：同 ID = 同内容，无需操作（本地事实源 wins）
            }
        }

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
        foreach (var (rel, line, entry) in _noteService.ReadAllLines())
        {
            var ts = NoteService.ToUtcIsoString(entry.Timestamp);
            localNotes.Add(new SyncNote
            {
                Id = SyncNote.ComputeId(rel, line),
                Content = CryptoService.Encrypt(_dek!, line),
                Tags = string.IsNullOrEmpty(entry.Tag) ? [] : new[] { entry.Tag },
                CreatedAt = ts,
                UpdatedAt = ts,
                DeviceId = _settings.Sync.DeviceId,
            });
        }

        // 2) 合并：本机 + PendingDeletes（本机软删）+ 云端他端（保留，不丢数据）
        var byId = new Dictionary<string, SyncNote>(StringComparer.Ordinal);
        foreach (var n in localNotes) byId[n.Id] = n;
        foreach (var d in _settings.Sync.PendingDeletes)
        {
            d.DeviceId = _settings.Sync.DeviceId;
            byId[d.Id] = d;
        }

        var cloudAll = await _provider.FullAsync(ct).ConfigureAwait(false);
        foreach (var cloud in cloudAll)
        {
            if (byId.TryGetValue(cloud.Id, out var mine))
            {
                // 同 ID 状态分歧（极罕见：本地行 Deleted=false vs 云端软删 Deleted=true）→ updatedAt 大者胜，被覆盖方进 PrevContent
                if (mine.Deleted != cloud.Deleted)
                {
                    if (string.CompareOrdinal(cloud.UpdatedAt, mine.UpdatedAt) > 0)
                    {
                        mine.PrevContent = mine.Content;      // 被覆盖方快照（密文原样，内容不外泄）
                        byId[cloud.Id] = cloud;
                    }
                    else
                    {
                        cloud.PrevContent = cloud.Content;
                        byId[cloud.Id] = mine;
                    }
                }
                // 状态一致：内容同 ID 必相同，本机版本即可
                continue;
            }
            if (cloud.DeviceId == _settings.Sync.DeviceId)
            {
                // 本机推过、本机本地文件已无此行 = 本机删除过且回收站未清空 → 云端保留原版本。
                // 软删标记只来自清空回收站的 PendingDeletes（§5.0.6：未清空前云端无软删标记），
                // 本地删除不主动从云端抹除——否则 B 拉取时行"失踪"而非可恢复。
                byId[cloud.Id] = cloud;
                continue;
            }
            byId[cloud.Id] = cloud;                        // 他端笔记保留（全量合并基础修正点）
        }

        // 3) 整桶覆盖上传（Provider 内分桶 PUT + 孤儿桶清理 + meta 更新）
        var result = await _provider.PushAsync(byId.Values.ToList(), _settings.Sync.LastCursor, ct).ConfigureAwait(false);

        // 4) 成功后清 PendingDeletes + 推进游标
        _settings.Sync.PendingDeletes.Clear();
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
    /// 清空回收站联动（QUEST-5 第五步 2）：把全部被清空记录转为 Deleted=true 的 SyncNote 压入 PendingDeletes。
    /// Content 用完整原始行密文（与普通行一致）；Tags 规则同 ReadAllLines（灵感_*.md → []，其余 → [文件名]）。
    /// </summary>
    public void QueueRecycleBinPurge(List<RecycleBinEntry> purgedEntries)
    {
        if (_dek == null) return;
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
                    UpdatedAt = NoteService.ToUtcIsoString(ts),
                    Deleted = true,
                    DeviceId = _settings.Sync.DeviceId,
                });
            }
        }
        _settings.Save();
        NotifyLocalChange();
    }

    private static DateTime ParseLineTimestamp(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line, @"- \[(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2})\]");
        return m.Success && DateTime.TryParse($"{m.Groups[1].Value} {m.Groups[2].Value}", out var ts)
            ? ts
            : DateTime.Now;
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
