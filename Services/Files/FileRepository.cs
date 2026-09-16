using System.Security.Cryptography;
using System.Threading;
using FocusCapture.Services.AI;

namespace FocusCapture.Services.Files;

/// <summary>本地检索条件（方案 §6.3：纯离线查元数据，不联网、不消耗网盘额度）。</summary>
public sealed class FileQuery
{
    /// <summary>文件名模糊匹配（不区分大小写）</summary>
    public string? Keyword { get; set; }

    /// <summary>类型过滤：artifact / upload / attachment；也可传「图片」「文档」「文本」这类粗分类</summary>
    public string? Type { get; set; }

    /// <summary>标签过滤</summary>
    public string? Tag { get; set; }

    /// <summary>时间下界（含），按 CreatedAt</summary>
    public DateTime? From { get; set; }

    /// <summary>时间上界（含），按 CreatedAt</summary>
    public DateTime? To { get; set; }

    /// <summary>最近 N 天（与 From/To 二选一，From/To 优先）</summary>
    public int? RecentDays { get; set; }

    /// <summary>按标签/类型过滤后，是否按标签权重排序 —— 这里只需分页</summary>
    public int Limit { get; set; } = 50;
}

/// <summary>
/// 文件仓库（方案 §4 / §5，本次更新的地基）。
///
/// 「本地当工作区，网盘当仓库」：检索、筛选、分析全在本地完成（零成本、瞬时），文件本体按需取回。
///
/// 三条铁律：
/// 1. <b>先本地后云端</b> —— 文件一律先落本地再排队上传，绝不阻塞用户操作
/// 2. <b>取回的文件永不触发上传</b> —— 账本 origin=downloaded 就是这道闸（防「取回→上传→重复」回环）
/// 3. <b>淘汰只删本地</b> —— 元数据不动、云端不动，云端才是权威副本
///
/// 元数据与账本分离（前者上云、后者只存本机）的理由见 <see cref="FileMetadata"/> / <see cref="CacheEntry"/>。
/// </summary>
public static class FileRepository
{
    private static readonly object Gate = new();
    private static List<FileMetadata>? _metadata;
    private static List<CacheEntry>? _ledger;

    /// <summary>云仓库（由装配处注入；未注入 = 只本地可用，一切功能降级但不报错）。</summary>
    public static ICloudStorage? Cloud { get; set; }

    /// <summary>
    /// 元数据发生变化（新登记 / 彻底删除 / 改名）。
    /// 装配处订阅它去踢一脚同步引擎 —— 否则「这台存完、那台要等半小时才看见」，体验差得莫名其妙。
    /// </summary>
    public static event Action? MetadataChanged;

    /// <summary>广播元数据变更（订阅方异常不影响业务动作本身）。</summary>
    private static void RaiseMetadataChanged()
    {
        try { MetadataChanged?.Invoke(); }
        catch (Exception ex) { AppLog.Warn("Files", "元数据变更通知失败：" + ex.Message); }
    }

    /// <summary>文件区（正式文件：AI 产出 + 用户主动上传，云端永久保留）。</summary>
    public static string FilesDir => FocusCapturePaths.Combine("files");

    /// <summary>对话附件区（沿用既有位置，零迁移风险）。</summary>
    public static string AttachmentsDir => ChatAttachmentService.Dir;

    /// <summary>元数据文件（随笔记同步到坚果云，跨设备共享）。</summary>
    public static string MetaPath => FocusCapturePaths.Combine("files_meta.json");

    /// <summary>本机缓存账本（只存本机，绝不上云）。</summary>
    public static string LedgerPath => FocusCapturePaths.Combine("files_ledger.json");

    /// <summary>当前云仓库是否可用于取回/上传（凭据齐 + 已授权）。</summary>
    public static bool CloudReady => Cloud?.IsReady == true;

    /// <summary>云端目录（附件与正式文件共用一个根下的两个子目录）。</summary>
    public static string NetFilesDir => $"{Cloud?.NetRoot ?? "/apps/FocusCapture"}/files";

    public static string NetAttachmentsDir => $"{Cloud?.NetRoot ?? "/apps/FocusCapture"}/attachments";

    // ══════════════════ 读写（内存缓存 + 变更即落盘） ══════════════════

    public static List<FileMetadata> AllMetadata(bool includeDeleted = false)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _metadata!
                .Where(m => includeDeleted || !m.Deleted)
                .Select(m => m.Clone())
                .ToList();
        }
    }

    public static List<CacheEntry> AllCache()
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _ledger!.Select(Clone).ToList();
        }
    }

    public static CacheEntry? FindCache(string id)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var e = _ledger!.FirstOrDefault(x => x.Id == id);
            return e == null ? null : Clone(e);
        }
    }

    public static FileMetadata? FindMetadata(string id)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _metadata!.FirstOrDefault(m => m.Id == id)?.Clone();
        }
    }

    /// <summary>把云同步合并后的清单整体写回（内存 + 磁盘）。</summary>
    public static void ApplyMergedMetadata(IReadOnlyList<FileMetadata> merged)
    {
        lock (Gate)
        {
            EnsureLoaded();
            _metadata = merged.Select(m => m.Clone()).ToList();
            PersistMetadataLocked();
        }
    }

    /// <summary>重载（云同步落地后调用，把外来的合并结果吃进来）。</summary>
    public static void Reload()
    {
        lock (Gate)
        {
            _metadata = ReadMetadataFile();
            _ledger = ReadLedgerFile();
        }
    }

    private static void EnsureLoaded()
    {
        _metadata ??= ReadMetadataFile();
        _ledger ??= ReadLedgerFile();
    }

    private static List<FileMetadata> ReadMetadataFile()
    {
        try
        {
            var path = MetaPath;
            if (!File.Exists(path)) return new List<FileMetadata>();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, FileJsonContext.Default.ListFileMetadata) ?? new List<FileMetadata>();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Files", "元数据读取失败，按空处理：" + ex.Message);
            return new List<FileMetadata>();
        }
    }

    private static List<CacheEntry> ReadLedgerFile()
    {
        try
        {
            var path = LedgerPath;
            if (!File.Exists(path)) return new List<CacheEntry>();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, FileJsonContext.Default.ListCacheEntry) ?? new List<CacheEntry>();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Files", "缓存账本读取失败，按空处理：" + ex.Message);
            return new List<CacheEntry>();
        }
    }

    /// <summary>落盘（调用方需已持有 Gate，或其自身即为唯一写入者）。</summary>
    private static void PersistMetadataLocked()
    {
        try
        {
            Directory.CreateDirectory(FocusCapturePaths.Root);
            File.WriteAllText(MetaPath,
                JsonSerializer.Serialize(_metadata ?? new List<FileMetadata>(), FileJsonContext.Default.ListFileMetadata),
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppLog.Error("Files", "元数据写入失败：" + ex.Message);
        }
    }

    private static void PersistLedgerLocked()
    {
        try
        {
            Directory.CreateDirectory(FocusCapturePaths.Root);
            File.WriteAllText(LedgerPath,
                JsonSerializer.Serialize(_ledger ?? new List<CacheEntry>(), FileJsonContext.Default.ListCacheEntry),
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppLog.Error("Files", "缓存账本写入失败：" + ex.Message);
        }
    }

    private static CacheEntry Clone(CacheEntry e) => new()
    {
        Id = e.Id, LocalPath = e.LocalPath, CachedAt = e.CachedAt, LastAccess = e.LastAccess,
        Origin = e.Origin, UploadState = e.UploadState, UploadRetry = e.UploadRetry, PendingEvict = e.PendingEvict,
    };

    // ══════════════════ 登记：文件进入仓库 ══════════════════

    /// <summary>
    /// 登记一个**已在本机**的文件。
    /// <paramref name="type"/> 见 <see cref="FileTypes"/>；<paramref name="attachExisting"/> =
    /// true 表示文件已在文件区/附件区，不再复制（对话附件走这条）。
    /// </summary>
    public static (FileMetadata? Meta, string? Error) RegisterLocalFile(
        string localPath, string type, string? displayName = null, bool attachExisting = false,
        IEnumerable<string>? tags = null)
    {
        try
        {
            if (!File.Exists(localPath)) return (null, "文件不存在：" + localPath);
            var info = new FileInfo(localPath);
            if (info.Length == 0) return (null, "空文件没有上传的意义，已跳过。");

            var md5 = ComputeMd5(localPath);

            // ── 防线②：MD5 查重 ──
            // 网盘里已经有同内容 → 只登记本地副本，不发上传。
            // 为什么要这道？上游的 origin 标记只管得住「本应用取回的」文件；用户把取回的文件又拖到别处
            // 再丢回文件区，origin 就看不出来了 —— 只有内容哈希认得出来。
            // 顺带说明：不要指望百度的「秒传」兜底，它省的是流量，不防重复条目（同内容不同路径照样两条记录）。
            lock (Gate)
            {
                EnsureLoaded();
                var existing = _metadata!.FirstOrDefault(m => !m.Deleted && m.Md5 == md5);
                if (existing != null)
                {
                    var fileName = displayName ?? Path.GetFileName(localPath);
                    var storedPath = attachExisting ? localPath : CopyIntoArea(localPath, fileName, type);
                    if (storedPath == null) return (null, "复制到文件区失败。");

                    // ⚠️ 这里曾经传 markUploaded: CloudReady —— 那是错的。
                    // CloudReady 只说明「配了凭据 + 本机有令牌」，跟云端到底有没有这个文件毫无关系。
                    // 后果：从没传上去的文件被记成「已上传」→ 队列再也不碰它 → 网盘空的、本机显示已上传
                    // （2026-09-16 实测踩到）。传 false = 保留原有状态；而用户重新选了同一个文件，
                    // 本身就是「再传一次」的明确意图，顺带把重试计数清零。
                    UpsertLedgerLocked(existing.Id, storedPath, CacheOrigins.Local, markUploaded: false);
                    var entry = _ledger!.FirstOrDefault(x => x.Id == existing.Id);
                    if (entry != null && entry.UploadState != UploadStates.Uploaded) entry.UploadRetry = 0;
                    PersistLedgerLocked();
                    AppLog.Info("Files", $"MD5 命中已有记录，仅登记本地副本：{existing.Name}");
                    return (existing.Clone(), null);
                }
            }

            var finalName = displayName ?? Path.GetFileName(localPath);
            var stored = attachExisting ? localPath : CopyIntoArea(localPath, finalName, type);
            if (stored == null) return (null, "复制到文件区失败。");

            var meta = new FileMetadata
            {
                Id = ComputeId(localPath),
                Name = Path.GetFileName(finalName),
                NetPath = BuildNetPath(finalName, type),
                Size = new FileInfo(stored).Length,
                Md5 = md5,
                Type = type,
                Tags = tags?.ToList() ?? new List<string>(),
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                ExpireAt = ComputeExpireAt(type),
            };

            lock (Gate)
            {
                EnsureLoaded();
                // 同一 id 上如果压着历史墓碑（用户删过、现在又传回来）→ 清墓碑。
                // 墓碑不是永久的，它只表达「当时不要了」；用户主动重新上传就是更新的意图。
                var tombstone = _metadata!.FirstOrDefault(m => m.Id == meta.Id);
                if (tombstone != null) _metadata!.Remove(tombstone);
                _metadata!.Add(meta);
                UpsertLedgerLocked(meta.Id, stored, CacheOrigins.Local, markUploaded: false);
                PersistMetadataLocked();
                PersistLedgerLocked();
            }

            // 措辞守则：**登记 ≠ 已上传**。日志里既要说明分类（这文件怎么进来的），
            // 也要把"还没传上去"写明白 —— 早先这里打的是「已上传」，读日志的人会以为传成功了
            // （2026-09-16 实测，连排查的人都被它骗过一轮）。
            AppLog.Info("Files",
                $"已登记到本机文件区：{meta.Name}（{FileTypes.Label(type)} / {RootMigrationService.FormatSize(meta.Size)}）—— 等待后台上传");
            return (meta.Clone(), null);
        }
        catch (Exception ex)
        {
            AppLog.Error("Files", "登记文件失败", ex);
            return (null, "登记失败：" + ex.Message);
        }
    }

    /// <summary>登记内存里的一段内容（链路 A：AI 生成的内容直接存网盘，不必先落盘到别处）。</summary>
    public static (FileMetadata? Meta, string? Error) RegisterBytes(
        byte[] data, string fileName, string type = FileTypes.Artifact, IEnumerable<string>? tags = null)
    {
        if (data.Length == 0) return (null, "内容为空，未创建文件。");
        try
        {
            var dir = type == FileTypes.Attachment ? AttachmentsDir : FilesDir;
            Directory.CreateDirectory(dir);
            var target = UniquePath(dir, SafeFileName(fileName));
            File.WriteAllBytes(target, data);
            return RegisterLocalFile(target, type, fileName, attachExisting: true, tags);
        }
        catch (Exception ex)
        {
            return (null, "保存内容失败：" + ex.Message);
        }
    }

    /// <summary>登记一段文本内容（AI 产出最常见的形态）。</summary>
    public static (FileMetadata? Meta, string? Error) RegisterText(
        string content, string fileName, string type = FileTypes.Artifact, IEnumerable<string>? tags = null)
        => RegisterBytes(new UTF8Encoding(false).GetBytes(content), fileName, type, tags);

    // ══════════════════ 检索（链路 D 的入口） ══════════════════

    public static List<FileMetadata> Search(FileQuery query)
    {
        var all = AllMetadata();
        IEnumerable<FileMetadata> q = all;

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword.Trim();
            q = q.Where(m => m.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)
                             || m.Tags.Any(t => t.Contains(kw, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            var t = query.Type.Trim();
            if (t is "图片" or "image" or "图片类")
                q = q.Where(m => ImageExtensions.Contains("." + m.Extension));
            else if (t is "文档" or "文本" or "document" or "doc")
                q = q.Where(m => !ImageExtensions.Contains("." + m.Extension));
            else
                q = q.Where(m => string.Equals(m.Type, t, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tag = query.Tag.Trim();
            q = q.Where(m => m.Tags.Any(t => t.Contains(tag, StringComparison.OrdinalIgnoreCase)));
        }

        if (query.RecentDays is > 0 && query.From == null && query.To == null)
        {
            var since = DateTime.Now.Date.AddDays(-query.RecentDays.Value);
            q = q.Where(m => m.CreatedAt >= since);
        }
        if (query.From.HasValue) q = q.Where(m => m.CreatedAt >= query.From.Value.Date);
        if (query.To.HasValue) q = q.Where(m => m.CreatedAt < query.To.Value.Date.AddDays(1));

        var limit = query.Limit <= 0 ? 50 : query.Limit;
        return q.OrderByDescending(m => m.CreatedAt).Take(limit).ToList();
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff",
    };

    // ══════════════════ 取用（链路 D/E 的核心：按需下载） ══════════════════

    /// <summary>
    /// 保证文件在本机可用：账本里已有就直接返回路径（零成本、不联网）；
    /// 没有才按需从网盘取回，并标记 <see cref="CacheOrigins.Downloaded"/>（永不触发上传）。
    /// </summary>
    public static async Task<(string? LocalPath, string? Error)> EnsureLocalAsync(
        string id, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var meta = FindMetadata(id);
        if (meta == null) return (null, "找不到这个文件记录，可能已被彻底删除。");

        var cached = FindCache(id);
        if (cached != null && File.Exists(cached.LocalPath))
        {
            MarkAccess(id);
            return (cached.LocalPath, null);
        }

        if (!CloudReady)
            return (null, "该文件不在本机，且尚未连接网盘（请到「设置 → 文件与网盘」完成百度网盘授权）。");

        try
        {
            var dir = meta.Type == FileTypes.Attachment ? AttachmentsDir : FilesDir;
            Directory.CreateDirectory(dir);
            var target = UniquePath(dir, SafeFileName(meta.Name));

            await Cloud!.EnsureDirectoryAsync(meta.Type == FileTypes.Attachment ? NetAttachmentsDir : NetFilesDir, ct)
                .ConfigureAwait(false);

            var ok = await Cloud.DownloadAsync(meta.NetPath, target, progress, ct).ConfigureAwait(false);
            if (!ok) return (null, $"云端没有找到「{meta.Name}」，可能已被清理（对话附件默认保留 30 天）。");

            lock (Gate)
            {
                EnsureLoaded();
                UpsertLedgerLocked(id, target, CacheOrigins.Downloaded, markUploaded: true);
                PersistLedgerLocked();
            }
            AppLog.Info("Files", $"已按需取回：{meta.Name}");
            return (target, null);
        }
        catch (Exception ex)
        {
            AppLog.Error("Files", "按需取回失败", ex);
            return (null, "取回失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 取回并读出文本内容（链路 E）。**本期只支持文本类**（md/txt/代码），
    /// PDF 解析本项目从未实现，遇到二进制一律给出明确说明而不是丢一堆乱码给模型。
    /// </summary>
    public static async Task<(string? Text, string? Error)> ReadTextAsync(
        string id, int maxChars = 20000, CancellationToken ct = default)
    {
        var meta = FindMetadata(id);
        if (meta == null) return (null, "找不到这个文件记录。");

        var ext = "." + meta.Extension;
        if (!TextExtensions.Contains(ext))
            return (null, $"「{meta.Name}」不是文本类文件（当前只能直接读取文本、Markdown 与代码文件）。" +
                          "可以先用「取回文件」把它放到本机，再用系统程序打开。");

        var (path, error) = await EnsureLocalAsync(id, null, ct).ConfigureAwait(false);
        if (path == null) return (null, error);

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(StripBom(bytes));
            if (text.Length > maxChars) text = text[..maxChars] + $"\n\n…（内容过长，仅取前 {maxChars} 字）";
            return (text, null);
        }
        catch (DecoderFallbackException)
        {
            return (null, $"「{meta.Name}」不是 UTF-8 编码的文本，无法直接读取。");
        }
        catch (Exception ex)
        {
            return (null, "读取失败：" + ex.Message);
        }
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".log", ".json", ".xml", ".csv", ".tsv", ".yml", ".yaml", ".toml", ".ini",
        ".cs", ".py", ".js", ".ts", ".java", ".kt", ".cpp", ".c", ".h", ".hpp", ".go", ".rs", ".php", ".rb",
        ".swift", ".html", ".htm", ".css", ".sql", ".sh", ".bat", ".ps1", ".cfg", ".conf",
    };

    // ══════════════════ 状态维护 ══════════════════

    /// <summary>记一次使用（淘汰排序依据）。</summary>
    public static void MarkAccess(string id)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var e = _ledger!.FirstOrDefault(x => x.Id == id);
            if (e == null) return;
            e.LastAccess = DateTime.Now;
            PersistLedgerLocked();
        }
    }

    /// <summary>记录一次上传结果。</summary>
    public static void RecordUploadResult(string id, bool ok, string? error)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var e = _ledger!.FirstOrDefault(x => x.Id == id);
            if (e == null) return;
            if (ok)
            {
                e.UploadState = UploadStates.Uploaded;
                e.UploadRetry = 0;
            }
            else
            {
                e.UploadState = UploadStates.Failed;
                e.UploadRetry++;
            }
            PersistLedgerLocked();
        }
        if (!ok && !string.IsNullOrEmpty(error))
            AppLog.Warn("Files", $"上传失败（{id}）：{error}");
    }

    /// <summary>标记为「上传中」（防同一文件被重复排队）。</summary>
    public static bool TryBeginUpload(string id)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var e = _ledger!.FirstOrDefault(x => x.Id == id);
            if (e == null) return false;
            if (e.UploadState == UploadStates.Uploading) return false;
            e.UploadState = UploadStates.Uploading;
            PersistLedgerLocked();
            return true;
        }
    }

    /// <summary>待上传清单（只认 origin=local 的 —— 取回的文件永远不会出现在这里，这就是防线①）。</summary>
    public static List<string> PendingUploadIds()
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _ledger!
                .Where(e => e.Origin == CacheOrigins.Local
                            && e.UploadState != UploadStates.Uploaded
                            && e.UploadState != UploadStates.Uploading)
                .Select(e => e.Id)
                .ToList();
        }
    }

    /// <summary>已到期的对话附件（云端清理器的输入）。</summary>
    public static List<FileMetadata> ExpiredAttachments(DateTime now)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _metadata!
                .Where(m => !m.Deleted && m.Type == FileTypes.Attachment
                            && m.ExpireAt.HasValue && m.ExpireAt.Value <= now)
                .Select(m => m.Clone())
                .ToList();
        }
    }

    /// <summary>打墓碑（云端那份已经没了 / 用户显式彻底删除）。</summary>
    public static void MarkDeleted(string id)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var m = _metadata!.FirstOrDefault(x => x.Id == id);
            if (m == null) return;
            m.Deleted = true;
            m.UpdatedAt = DateTime.Now;
            PersistMetadataLocked();
        }
    }

    /// <summary>
    /// 贴「优先淘汰」标记：云端那份已经清掉了，本地这份不立刻删（用户可能正在用），
    /// 交给淘汰器下一轮统一处理 —— 所有删本地文件的动作只走同一个出口。
    /// </summary>
    public static void MarkPendingEvict(string id)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var e = _ledger!.FirstOrDefault(x => x.Id == id);
            if (e == null) return;
            e.PendingEvict = true;
            PersistLedgerLocked();
        }
    }

    /// <summary>更新元数据（重命名 / 改标签）。</summary>
    public static bool UpdateMetadata(string id, string? newName = null, IEnumerable<string>? tags = null)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var m = _metadata!.FirstOrDefault(x => x.Id == id);
            if (m == null) return false;
            if (!string.IsNullOrWhiteSpace(newName)) m.Name = newName.Trim();
            if (tags != null) m.Tags = tags.ToList();
            m.UpdatedAt = DateTime.Now;
            PersistMetadataLocked();
        }
        RaiseMetadataChanged();
        return true;
    }

    /// <summary>
    /// 连续失败已达重试上限的上传数。
    /// 界面必须把它显示出来 —— 「有文件一直传不上去」如果不可见，就等于一个无声黑洞，
    /// 用户会以为存成功了，直到某天想取回才发现云端根本没有。
    /// </summary>
    public static int StuckUploadCount(int maxRetry)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _ledger!.Count(e => e.UploadState == UploadStates.Failed && e.UploadRetry >= maxRetry);
        }
    }

    /// <summary>
    /// 把「失败待重传」的清零重试计数、放回队列，返回被重置的条数。
    ///
    /// <b>为什么必须有这个出口</b>：重试有上限是为了不无限骚扰平台，但一旦撞上上限，
    /// 文件就永远躺在失败态 —— 用户后来把配置改对了，它也不会自己再试。而「永久卡死」
    /// 没有任何可见出口，比「反复重试」更难排查。所以把这个动作挂在**用户修配置成功**
    /// 的节点上（测试连接通过 / 重新授权），让修复动作自动带来重试。
    /// 删除、清空这类破坏性动作则不在此列 —— 那不给 AI，也不自动做。
    /// </summary>
    public static int ResetFailedUploads()
    {
        lock (Gate)
        {
            EnsureLoaded();
            var n = 0;
            foreach (var e in _ledger!)
            {
                if (e.UploadState != UploadStates.Failed) continue;
                e.UploadState = UploadStates.Pending;
                e.UploadRetry = 0;
                n++;
            }
            if (n > 0)
            {
                PersistLedgerLocked();
                AppLog.Info("Files", $"已把 {n} 个失败的上传放回队列重试");
            }
            return n;
        }
    }

    // ══════════════════ 删除（两个动作必须分开） ══════════════════

    /// <summary>
    /// 彻底删除：云端删（入回收站）+ 元数据打墓碑 + 清本地副本与账本。
    /// 与「清本地（淘汰）」的区别见方案 §5.5：淘汰只删本地、元数据与云端都留着。
    /// </summary>
    public static async Task<(bool Ok, string Message)> DeletePermanentlyAsync(string id, CancellationToken ct = default)
    {
        var meta = FindMetadata(id);
        if (meta == null) return (false, "找不到这个文件记录。");

        var cloudNote = "";
        if (CloudReady)
        {
            try
            {
                await Cloud!.DeleteAsync(new[] { meta.NetPath }, ct).ConfigureAwait(false);
                cloudNote = "云端已删除（可在网盘回收站找回）";
            }
            catch (Exception ex)
            {
                cloudNote = $"云端删除失败：{ex.Message}；已仅在本机记账，稍后可重试";
                AppLog.Warn("Files", "云端删除失败：" + ex.Message);
            }
        }
        else
        {
            cloudNote = "未连接网盘，仅在本机记账";
        }

        lock (Gate)
        {
            EnsureLoaded();
            var m = _metadata!.FirstOrDefault(x => x.Id == id);
            if (m != null)
            {
                m.Deleted = true;
                m.UpdatedAt = DateTime.Now;
            }
            var e = _ledger!.FirstOrDefault(x => x.Id == id);
            if (e != null)
            {
                TryDeleteFile(e.LocalPath);
                _ledger!.Remove(e);
            }
            PersistMetadataLocked();
            PersistLedgerLocked();
        }

        AppLog.Info("Files", $"已彻底删除：{meta.Name}（{cloudNote}）");
        RaiseMetadataChanged();
        return (true, $"已删除「{meta.Name}」。{cloudNote}。");
    }

    /// <summary>清本地副本（= 淘汰动作），元数据与云端都保留。</summary>
    internal static bool DropLocal(string id, string? reason = null)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var e = _ledger!.FirstOrDefault(x => x.Id == id);
            if (e == null) return false;
            var ok = TryDeleteFile(e.LocalPath);
            _ledger!.Remove(e);
            PersistLedgerLocked();
            if (ok)
                AppLog.Info("Files", $"已释放本地缓存：{id}{(reason == null ? "" : $"（{reason}）")}");
            return ok;
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return true;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Files", $"本地文件删除失败（{path}）：{ex.Message}");
            return false;
        }
    }

    // ══════════════════ 内部工具 ══════════════════

    private static void UpsertLedgerLocked(string id, string localPath, string origin, bool markUploaded)
    {
        var e = _ledger!.FirstOrDefault(x => x.Id == id);
        if (e == null)
        {
            e = new CacheEntry { Id = id, CachedAt = DateTime.Now };
            _ledger!.Add(e);
        }
        e.LocalPath = localPath;
        e.Origin = origin;
        e.LastAccess = DateTime.Now;
        if (e.CachedAt == default) e.CachedAt = DateTime.Now;
        if (markUploaded)
        {
            e.UploadState = UploadStates.Uploaded;
            e.UploadRetry = 0;
        }
        else if (e.UploadState != UploadStates.Uploaded)
        {
            e.UploadState = UploadStates.Pending;
        }
    }

    private static string? CopyIntoArea(string sourcePath, string displayName, string type)
    {
        try
        {
            var dir = type == FileTypes.Attachment ? AttachmentsDir : FilesDir;
            Directory.CreateDirectory(dir);
            var target = UniquePath(dir, SafeFileName(displayName));
            File.Copy(sourcePath, target, overwrite: false);
            return target;
        }
        catch (Exception ex)
        {
            AppLog.Error("Files", "复制到文件区失败", ex);
            return null;
        }
    }

    private static string BuildNetPath(string displayName, string type)
    {
        var dir = type == FileTypes.Attachment ? NetAttachmentsDir : NetFilesDir;
        var name = SafeFileName(displayName);
        // 云端同名会被覆盖：加上 id 前 8 位做后缀，保证「同内容同 id → 同路径」（去重），
        // 而不同内容即使同名也不会互相冲掉。
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        return $"{dir}/{stem}-{ShortId(displayName)}{ext}";
    }

    /// <summary>网盘路径里用文件名派生的短后缀（不用内容 id，避免调用顺序耦合）。</summary>
    private static string ShortId(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed + "|" + Guid.NewGuid().ToString("N")[..4]));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// 内容 ID = SHA256(内容) 前 32 位。同内容天然同 id —— 这是「同一份文件在网盘里只有一条记录」的根基。
    /// </summary>
    private static string ComputeId(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(fs))[..32].ToLowerInvariant();
    }

    public static string ComputeMd5(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        return Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant();
    }

    public static string ComputeMd5(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    private static DateTime? ComputeExpireAt(string type)
    {
        if (type != FileTypes.Attachment) return null;   // 只清对话附件：一刀切会毁掉「全量仓库」的根基
        var days = AttachmentRetentionDays;
        return days <= 0 ? null : DateTime.Now.AddDays(days);
    }

    /// <summary>对话附件云端保留天数（0 = 不自动清理）；由设置驱动。</summary>
    public static int AttachmentRetentionDays { get; set; } = 30;

    private static string SafeFileName(string name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) trimmed = "未命名";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed) sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        var result = sb.ToString().Trim().TrimEnd('.');
        if (result.Length == 0) result = "未命名";

        if (result.Length > 100)
        {
            var ext = Path.GetExtension(result);
            var stem = Path.GetFileNameWithoutExtension(result);
            var keep = Math.Max(1, 100 - ext.Length);
            result = stem[..Math.Min(keep, stem.Length)] + ext;
        }
        return result;
    }

    private static string UniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    private static byte[] StripBom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes[3..] : bytes;
}
