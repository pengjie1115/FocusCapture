using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FocusCapture.Models;

namespace FocusCapture.Services.Sync;

/// <summary>会话同步对账状态（本地 chat_sync_state.json）：已确认 Rev + 内容指纹 + 删除清单。</summary>
public class ChatSyncState
{
    /// <summary>已确认会话状态：会话 Id → (Rev, 本地文件内容规范化 JSON 的 SHA256)。
    /// 用于对账时判断"本地文件是否在上次确认后又被同 Rev 改写"（区分真已同步与未推送修改）。</summary>
    public Dictionary<string, ChatConfirmedEntry> Confirmed { get; set; } = new();

    /// <summary>已删除会话清单（闭环①：Id + 删除时间 + 设备 ID），随同步与云端清单合并。</summary>
    public List<ChatDeletionRecord> Deletions { get; set; } = new();
}

public class ChatConfirmedEntry
{
    public int Rev { get; set; }
    public string Hash { get; set; } = "";
}

/// <summary>已删除会话记录（云端密文同步；他端拉到后把本地文件移入会话回收站，不物理删除）</summary>
public class ChatDeletionRecord
{
    public string Id { get; set; } = "";
    public DateTime DeletedAt { get; set; }
    public string DeviceId { get; set; } = "";
}

/// <summary>云端会话文件包络：Id/Rev/DeviceId/SavedAtUtc 明文（对账依据，同 SyncNote 的 Id/UpdatedAt 明文先例），内容密文。</summary>
public class ChatSyncEnvelope
{
    public string Id { get; set; } = "";
    public int Rev { get; set; }
    public string DeviceId { get; set; } = "";
    public string SavedAtUtc { get; set; } = "";
    public string Data { get; set; } = "";   // SessionFile JSON 的 E2EE 密文（AES-256-GCM Base64）
}

/// <summary>
/// AI 会话同步引擎（2026-09 迭代，阶段一）：整文件粒度对账、LWW + Rev 冲突检测、E2EE 自持 DEK、删除清单闭环①。
/// - 不另起循环：搭 SyncEngine 周期（CycleCompleted hook）+ 本类防抖窗口（同"上传合并间隔"配置）；
/// - 与笔记同步共享同一把闸（SyncEngine.Gate），全局串行；本类任何失败只写自己的状态（ChatSyncResult），绝不影响笔记同步；
/// - E2EE：直接从 Sync 配置取授权码 + 盐自派生 DEK（不读 SyncEngine 私有 _dek）；云端只存密文；
/// - 盐未就绪（笔记同步尚未完成首配）时静默跳过 + 状态留痕"等待笔记同步完成首配"；
/// - 冲突策略：confirmed 基线之上的双端新增 = 冲突 → 阶段一无弹窗 UI，默认跳过上传 + 留痕（绝不静默覆盖云端新版）；
///   阶段二 UI 订阅 ConflictResolutionRequested 弹窗裁决（true = 用本地版覆盖上传）；
/// - 删除闭环①：MarkDeleted → 本地移入 trash + 清单上传；他端拉到后本地文件移入各自的会话回收站；
///   镜像目录 / Purged 跨端清空 / 恢复跨端传播延后 v2。
/// </summary>
public class ChatSyncEngine
{
    private const string ChatFilePrefix = "chat-";            // 云端会话文件名：chat-{Id}.json（与笔记 notes-* 前缀隔离）
    private const string CloudGroupsFile = "chat_groups.json";
    private const string CloudDeletionsFile = "chat_deletions.json";
    private const string TrashDirName = "trash";              // 本地会话回收站：chat_history\trash

    private static readonly string ChatHistoryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusCapture", "chat_history");
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusCapture", "chat_sync_state.json");

    private static readonly JsonSerializerOptions StateJsonOptions = new() { WriteIndented = true };

    private readonly AppSettings _settings;
    private readonly IFileStorageProvider _storage;
    private readonly SemaphoreSlim _gate;         // 与 SyncEngine 共享
    private readonly Timer _mergeTimer;           // 会话上传防抖窗口（时长 = Sync.MergeWindowSeconds）
    private ChatSyncState _state;

    private byte[]? _dek;
    private string _dekSalt = "";                 // 派生 _dek 时用的盐（笔记端盐变更后自动重派生）
    private volatile bool _dirty;

    /// <summary>
    /// 检测到会话版本冲突（双端在共同基线之上都有修改）时触发，阶段二 UI 订阅弹窗裁决。
    /// 参数 = 会话 Id；返回 true = 用本地版覆盖上传，false/异常 = 跳过并留痕。
    /// 无订阅者时默认策略：跳过上传该会话 + 状态留痕（绝不静默覆盖云端新版）。
    /// </summary>
    public event Func<string, Task<bool>>? ConflictResolutionRequested;

    /// <summary>会话同步状态变化（已 marshal 需求由订阅方负责；文案同步写入 Sync.ChatSyncResult 留痕）。</summary>
    public event Action<string>? StatusChanged;

    public ChatSyncEngine(AppSettings settings, IFileStorageProvider storage, SemaphoreSlim sharedGate)
    {
        _settings = settings;
        _storage = storage;
        _gate = sharedGate;
        _state = LoadState();
        var window = TimeSpan.FromSeconds(Math.Max(30, settings.Sync.MergeWindowSeconds));
        _mergeTimer = new Timer(_ => OnMergeWindowElapsed(), null, Timeout.Infinite, Timeout.Infinite);
    }

    // ── 触发入口 ──

    /// <summary>会话本地变更后调用（ChatSessionService.SessionChanged 订阅入口），启动/重置上传防抖窗口。</summary>
    public void NotifyLocalChange()
    {
        if (!_settings.Sync.ChatSyncEnabled) return;
        if (!EnsureDekCurrent()) return;   // 盐未就绪：静默跳过（状态已留痕）
        _dirty = true;
        var window = TimeSpan.FromSeconds(Math.Max(30, _settings.Sync.MergeWindowSeconds));
        _mergeTimer.Change(window, Timeout.InfiniteTimeSpan);
    }

    private void OnMergeWindowElapsed()
    {
        if (!_dirty) return;
        _dirty = false;
        _ = Task.Run(() => RunOnceAsync());
    }

    /// <summary>
    /// 跑一轮会话同步（对账：分组 → 删除清单 → 会话文件）。搭车 hook / 防抖到期 / 退出 flush 共用。
    /// 开关关闭时不跑；失败只写 ChatSyncResult 留痕，本地文件是事实源，下次对账天然补传（脏标记语义由对账保证）。
    /// </summary>
    public async Task RunOnceAsync()
    {
        if (!_settings.Sync.ChatSyncEnabled) return;
        if (!EnsureDekCurrent()) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _storage.EnsureDirectoryAsync(CancellationToken.None).ConfigureAwait(false);
            await SyncGroupsAsync().ConfigureAwait(false);
            await SyncDeletionsAsync().ConfigureAwait(false);
            await SyncSessionsAsync().ConfigureAwait(false);
            SetChatStatus("成功（会话）");
        }
        catch (Exception ex)
        {
            SetChatStatus("失败: " + ex.Message);
        }
        finally { _gate.Release(); }
    }

    /// <summary>阶段二删除 UI 调用：本地文件移入会话回收站 + 加入删除清单 + 触发上传。清单随下轮对账传播他端。</summary>
    public void MarkDeleted(string sessionId)
    {
        try
        {
            var file = FindLocalFile(sessionId);
            if (file != null)
            {
                var trashDir = Path.Combine(ChatHistoryDir, TrashDirName);
                Directory.CreateDirectory(trashDir);
                File.Move(file, Path.Combine(trashDir, Path.GetFileName(file)));
            }
            if (!_state.Deletions.Any(d => d.Id == sessionId))
            {
                _state.Deletions.Add(new ChatDeletionRecord
                {
                    Id = sessionId,
                    DeletedAt = DateTime.Now,
                    DeviceId = _settings.Sync.DeviceId,
                });
                SaveState();
            }
            NotifyLocalChange();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 会话移入回收站失败: {ex.Message}");
        }
    }

    // ── 会话文件对账（全量列目录对比，第一版策略，方案文档 §九-10） ──

    private async Task SyncSessionsAsync()
    {
        Directory.CreateDirectory(ChatHistoryDir);

        // 1) 本地集合：读全部顶层 JSON，缺 Id 的旧文件兜底生成 GUID 并回写（铁律：同步主键必须落盘）
        var local = new Dictionary<string, (string File, SessionFile Session)>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(ChatHistoryDir, "*.json"))
        {
            try
            {
                var session = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(file, Encoding.UTF8));
                if (session == null || string.IsNullOrEmpty(session.Mode)) continue;
                if (string.IsNullOrEmpty(session.Id))
                {
                    session.Id = Guid.NewGuid().ToString();
                    File.WriteAllText(file, JsonSerializer.Serialize(session, StateJsonOptions), Encoding.UTF8);
                }
                local[session.Id] = (file, session);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Debug.WriteLine($"[FocusCapture] 会话同步跳过损坏文件: {file}: {ex.Message}");
            }
        }

        // 2) 云端集合：列目录 → 逐个 GET 包络（chat- 前缀，与 notes-* 笔记桶天然隔离）
        var cloudFiles = (await _storage.ListFilesAsync(CancellationToken.None).ConfigureAwait(false))
            .Where(f => f.StartsWith(ChatFilePrefix, StringComparison.Ordinal) && f.EndsWith(".json", StringComparison.Ordinal))
            .ToList();
        var cloud = new Dictionary<string, (string File, ChatSyncEnvelope Envelope)>(StringComparer.Ordinal);
        foreach (var f in cloudFiles)
        {
            var json = await _storage.DownloadFileAsync(f, CancellationToken.None).ConfigureAwait(false);
            if (json == null) continue;
            try
            {
                var env = JsonSerializer.Deserialize<ChatSyncEnvelope>(json, StateJsonOptions);
                if (!string.IsNullOrEmpty(env?.Id)) cloud[env.Id] = (f, env);
            }
            catch (JsonException) { /* 包络损坏：跳过，等修复或被本地新版覆盖 */ }
        }

        // 3) 对账
        foreach (var (id, envPair) in cloud)
        {
            var env = envPair.Envelope;
            if (!local.TryGetValue(id, out var localPair))
            {
                // 云端新会话 → 落地 {Id}.json（真同步：密文解密写入本地文件，非内存比对）
                await LandSessionAsync(env).ConfigureAwait(false);
                continue;
            }

            var localSession = localPair.Session;
            var confirmed = _state.Confirmed.TryGetValue(id, out var c) ? c : null;
            var hasLocalNew = confirmed == null
                ? localSession.Rev > env.Rev            // 无基线（首见）：按 LWW 判方向
                : localSession.Rev > confirmed.Rev;     // 本地有未推送修改
            var hasCloudNew = confirmed == null
                ? env.Rev > localSession.Rev
                : env.Rev > confirmed.Rev;              // 云端有本机未见的更新

            if (hasLocalNew && hasCloudNew)
            {
                // 双端都有新修改 → 冲突：阶段一跳过 + 留痕；阶段二弹窗裁决
                await HandleConflictAsync(id).ConfigureAwait(false);
            }
            else if (hasLocalNew)
            {
                await PushSessionAsync(localPair.File, localSession).ConfigureAwait(false);
            }
            else if (hasCloudNew)
            {
                await LandSessionAsync(env).ConfigureAwait(false);
            }
            else
            {
                // 双端都无新修改。同 Rev 但本地文件内容 ≠ 上次确认指纹 → 本地被同 Rev 改写（异常/用户手改）→ 冲突路径
                if (confirmed != null && !IsConfirmedHash(localPair.File, confirmed))
                    await HandleConflictAsync(id).ConfigureAwait(false);
                // 真已同步：无操作
            }
        }

        // 4) 本地有、云端无 → push（新会话/首次同步；删除走清单闭环①，云端无≠已删）
        var cloudIds = new HashSet<string>(cloud.Keys, StringComparer.Ordinal);
        foreach (var (id, pair) in local)
        {
            if (!cloudIds.Contains(id))
                await PushSessionAsync(pair.File, pair.Session).ConfigureAwait(false);
        }

        SaveState();
    }

    /// <summary>push 本地会话：包络（Rev 明文 + 内容密文）PUT chat-{Id}.json；成功后更新确认指纹。</summary>
    private async Task PushSessionAsync(string file, SessionFile session)
    {
        var env = new ChatSyncEnvelope
        {
            Id = session.Id,
            Rev = session.Rev,
            DeviceId = _settings.Sync.DeviceId,
            SavedAtUtc = NoteService.ToUtcIsoString(session.SavedAt),
            Data = CryptoService.Encrypt(_dek!, JsonSerializer.Serialize(session, StateJsonOptions)),
        };
        await _storage.UploadFileAsync(ChatFilePrefix + session.Id + ".json",
            JsonSerializer.Serialize(env, StateJsonOptions), CancellationToken.None).ConfigureAwait(false);
        _state.Confirmed[session.Id] = new ChatConfirmedEntry
        {
            Rev = session.Rev,
            Hash = ComputeNormalizedHash(session),
        };
    }

    /// <summary>云端会话落地：解密写本地 {Id}.json（真写文件）；本地旧名文件（同 Id 时间戳文件）一并清理。</summary>
    private async Task LandSessionAsync(ChatSyncEnvelope env)
    {
        var json = CryptoService.Decrypt(_dek!, env.Data);
        var session = JsonSerializer.Deserialize<SessionFile>(json, StateJsonOptions);
        if (session == null || string.IsNullOrEmpty(session.Mode)) return;
        session.Id = env.Id;   // 包络 Id 为准
        session.Rev = env.Rev;

        var old = FindLocalFile(env.Id);
        var target = Path.Combine(ChatHistoryDir, env.Id + ".json");
        Directory.CreateDirectory(ChatHistoryDir);
        File.WriteAllText(target, JsonSerializer.Serialize(session, StateJsonOptions), Encoding.UTF8);
        if (old != null && !string.Equals(Path.GetFullPath(old), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Delete(old);

        _state.Confirmed[env.Id] = new ChatConfirmedEntry
        {
            Rev = env.Rev,
            Hash = ComputeNormalizedHash(session),
        };
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>冲突处理：有订阅者（阶段二弹窗）→ 裁决 true 则本地覆盖上传；否则默认跳过 + 留痕。</summary>
    private async Task HandleConflictAsync(string sessionId)
    {
        var handler = ConflictResolutionRequested;
        if (handler != null)
        {
            try
            {
                if (await handler(sessionId).ConfigureAwait(false))
                {
                    var file = FindLocalFile(sessionId);
                    if (file != null)
                    {
                        var session = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(file, Encoding.UTF8));
                        if (session != null) { await PushSessionAsync(file, session).ConfigureAwait(false); return; }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FocusCapture] 会话冲突裁决失败: {ex.Message}");
            }
        }
        SetChatStatus($"冲突：会话 {sessionId[..8]}… 在另一设备有更新，已跳过上传（等待处理）");
    }

    // ── 分组清单同步（阶段一：按 Id 并集；多端同名分组合并 + GroupId 重映射属分组管理功能，随阶段二 UI 交付） ──

    private async Task SyncGroupsAsync()
    {
        var localGroups = ChatGroupStore.Load();
        var localById = localGroups.GroupBy(g => g.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var cloudJson = await _storage.DownloadFileAsync(CloudGroupsFile, CancellationToken.None).ConfigureAwait(false);
        var merged = new Dictionary<string, ChatGroup>(StringComparer.Ordinal);
        if (cloudJson != null)
        {
            try
            {
                var cloudPlain = CryptoService.Decrypt(_dek!, cloudJson);
                var cloudGroups = JsonSerializer.Deserialize<List<ChatGroup>>(cloudPlain, StateJsonOptions) ?? new();
                foreach (var g in cloudGroups) merged[g.Id] = g;
            }
            catch (JsonException) { /* 云端分组文件损坏：按空处理，本地并集覆盖回去 */ }
        }
        foreach (var (id, g) in localById) merged[id] = g;   // 本地 wins 同 Id 冲突（无版本号，简单并集）

        // 多端同名分组合并（方案文档 4.4，阶段二）：按名称去重，胜出 = CreatedAt 最早（平局按 DeviceId 字典序 → Id 字典序），
        // 败者分组删除、引用败者的会话 GroupId 重映射到胜者（重映射走 Load→Save，Rev 自增随下轮推送）
        foreach (var g in merged.Values)
            if (string.IsNullOrEmpty(g.DeviceId)) g.DeviceId = _settings.Sync.DeviceId;

        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sameName in merged.Values.GroupBy(g => g.Name, StringComparer.Ordinal).Where(gr => gr.Count() > 1))
        {
            var winner = sameName.OrderBy(g => g.CreatedAt)
                .ThenBy(g => g.DeviceId, StringComparer.Ordinal)
                .ThenBy(g => g.Id, StringComparer.Ordinal)
                .First();
            foreach (var loser in sameName.Where(g => !string.Equals(g.Id, winner.Id, StringComparison.Ordinal)))
            {
                merged.Remove(loser.Id);
                remap[loser.Id] = winner.Id;
            }
        }
        if (remap.Count > 0) RemapLocalGroupIds(remap);

        if (merged.Count > 0)
        {
            var cipher = CryptoService.Encrypt(_dek!, JsonSerializer.Serialize(merged.Values.ToList(), StateJsonOptions));
            await _storage.UploadFileAsync(CloudGroupsFile,
                JsonSerializer.Serialize(cipher, StateJsonOptions), CancellationToken.None).ConfigureAwait(false);
        }

        // 本地清单对齐合并结果：云端有本地缺的分组落地（他端新建）；同名合并删掉的败者从清单移除
        if (merged.Values.Any(g => !localById.ContainsKey(g.Id)) || remap.Count > 0)
            ChatGroupStore.Save(merged.Values.ToList());
    }

    /// <summary>同名分组合并后重映射：把本地会话文件中引用败者 GroupId 的改为胜者（Load → 改 → Save，
    /// Rev 自增 + SessionChanged 防抖窗口，重映射结果随下轮推送他端）。</summary>
    private void RemapLocalGroupIds(Dictionary<string, string> remap)
    {
        try
        {
            Directory.CreateDirectory(ChatHistoryDir);
            foreach (var file in Directory.EnumerateFiles(ChatHistoryDir, "*.json"))
            {
                try
                {
                    var svc = ChatSessionService.Load(file);
                    if (svc == null || string.IsNullOrEmpty(svc.GroupId)) continue;
                    if (!remap.TryGetValue(svc.GroupId, out var target)) continue;
                    svc.GroupId = target;
                    svc.Save();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FocusCapture] 分组重映射跳过文件: {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 分组重映射失败: {ex.Message}");
        }
    }

    // ── 删除清单同步（闭环①） ──

    private async Task SyncDeletionsAsync()
    {
        var merged = new Dictionary<string, ChatDeletionRecord>(StringComparer.Ordinal);
        foreach (var d in _state.Deletions) merged[d.Id] = d;

        var cloudJson = await _storage.DownloadFileAsync(CloudDeletionsFile, CancellationToken.None).ConfigureAwait(false);
        if (cloudJson != null)
        {
            try
            {
                var cloudPlain = CryptoService.Decrypt(_dek!, cloudJson);
                var cloudList = JsonSerializer.Deserialize<List<ChatDeletionRecord>>(cloudPlain, StateJsonOptions) ?? new();
                foreach (var d in cloudList)
                {
                    if (!merged.TryGetValue(d.Id, out var exist) || d.DeletedAt < exist.DeletedAt)
                        merged[d.Id] = d;
                }
            }
            catch (JsonException) { /* 云端清单损坏：按空处理，本地清单覆盖回去 */ }
        }

        // 他端删除落地：本地文件 SavedAt 早于删除时间 → 移入本地回收站；本机删除后又聊过（SavedAt 更新）→ 本机 wins
        Directory.CreateDirectory(ChatHistoryDir);
        foreach (var d in merged.Values.Where(d => d.DeviceId != _settings.Sync.DeviceId))
        {
            var file = FindLocalFile(d.Id);
            if (file == null) continue;
            try
            {
                var session = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(file, Encoding.UTF8));
                if (session != null && session.SavedAt >= d.DeletedAt) continue;   // 本机 wins
                var trashDir = Path.Combine(ChatHistoryDir, TrashDirName);
                Directory.CreateDirectory(trashDir);
                File.Move(file, Path.Combine(trashDir, Path.GetFileName(file)));
                _state.Confirmed.Remove(d.Id);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FocusCapture] 他端删除落地失败: {d.Id}: {ex.Message}");
            }
        }

        // 合并清单回写本地 + 加密上传（清单保留，供新设备/后续拉取；v2 Purged 机制再瘦身）
        _state.Deletions = merged.Values.OrderBy(d => d.DeletedAt).ToList();
        var cipher = CryptoService.Encrypt(_dek!, JsonSerializer.Serialize(_state.Deletions, StateJsonOptions));
        await _storage.UploadFileAsync(CloudDeletionsFile,
            JsonSerializer.Serialize(cipher, StateJsonOptions), CancellationToken.None).ConfigureAwait(false);
    }

    // ── 内部帮助 ──

    /// <summary>确保 DEK 就绪且与当前盐一致（笔记端密钥重置换盐后自动重派生）。盐未就绪返回 false 并留痕。</summary>
    private bool EnsureDekCurrent()
    {
        var salt = _settings.Sync.E2eeSalt;
        if (_dek != null && salt == _dekSalt) return true;
        if (string.IsNullOrEmpty(salt))
        {
            SetChatStatus("等待笔记同步完成首配（E2EE 盐未就绪）");
            return false;
        }
        var token = Models.SyncSettings.UnprotectToken(_settings.Sync.WebDavToken);
        if (string.IsNullOrEmpty(token))
        {
            SetChatStatus("未配置坚果云授权码，会话同步跳过");
            return false;
        }
        _dek = CryptoService.DeriveKey(token, Convert.FromBase64String(salt));
        _dekSalt = salt;
        return true;
    }

    /// <summary>按 Id 找本地会话文件（GUID 文件名为主，兼容未回写的旧时间戳文件名）。</summary>
    private static string? FindLocalFile(string sessionId)
    {
        var byId = Path.Combine(ChatHistoryDir, sessionId + ".json");
        if (File.Exists(byId)) return byId;
        if (!Directory.Exists(ChatHistoryDir)) return null;
        foreach (var file in Directory.EnumerateFiles(ChatHistoryDir, "*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(file, Encoding.UTF8));
                if (s?.Id == sessionId) return file;
            }
            catch (JsonException) { /* 损坏文件跳过 */ }
        }
        return null;
    }

    /// <summary>规范化内容指纹：反序列化→再序列化（无缩进）→ SHA256。同内容必同指纹，格式差异不影响。</summary>
    private static string ComputeNormalizedHash(SessionFile session)
    {
        var normalized = JsonSerializer.Serialize(session, new JsonSerializerOptions());
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }

    private static bool IsConfirmedHash(string file, ChatConfirmedEntry confirmed)
    {
        try
        {
            var session = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(file, Encoding.UTF8));
            return session != null && ComputeNormalizedHash(session) == confirmed.Hash;
        }
        catch { return false; }
    }

    private static ChatSyncState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<ChatSyncState>(File.ReadAllText(StatePath, Encoding.UTF8), StateJsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Debug.WriteLine($"[FocusCapture] 会话同步状态读取失败（按空状态处理）: {ex.Message}");
        }
        return new ChatSyncState();
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(_state, StateJsonOptions), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 会话同步状态保存失败: {ex.Message}");
        }
    }

    /// <summary>状态留痕：写 Sync.ChatSyncResult（设置页云同步区展示，失败/冲突持续可见，不允许无声丢失）。值变化才写盘。</summary>
    private void SetChatStatus(string status)
    {
        var text = $"{status}（{DateTime.Now:yyyy-MM-dd HH:mm}）";
        if (_settings.Sync.ChatSyncResult == text) return;
        _settings.Sync.ChatSyncResult = text;
        _settings.Sync.ChatSyncAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        _settings.Save();
        StatusChanged?.Invoke(text);
    }
}
