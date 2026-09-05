using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using FocusCapture.Models;

namespace FocusCapture.Services.Sync;

/// <summary>
/// 坚果云 WebDAV Provider（QUEST-5 任务7）：PROPFIND/PUT/GET/DELETE/MKCOL + sync_meta.json + 桶清单 + 401/503 区分。
/// - 整桶 PUT 覆盖天然幂等（重复推送同一桶结果一致）；
/// - 请求频率由引擎按 Limits 控制（30s 合并窗口天然限流），本类不做 sleep；
/// - 桶规则：ISO 周分桶（updatedAt UTC），≤Limits.MaxBatchSize 条/桶，文件名 notes-{yyyy-Www}-{seq}.json（§5.0.3）。
/// </summary>
public class WebDAVProvider : ISyncProvider
{
    private const string DavNs = "{DAV:}";
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _user;

    public string Name => "WebDAV";
    public SyncLimits Limits { get; } = new(30, 200, 500);   // 坚果云实测红线：30min ≤600 请求 / 单次 >200 文件 503

    public WebDAVProvider(string baseUrl, string user, string token)
    {
        _baseUrl = baseUrl.TrimEnd('/') + "/";
        _user = user;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);
    }

    // ── ISyncProvider 实现 ──

    public async Task<SyncPullResult> PullAsync(string? since, CancellationToken ct)
    {
        var notes = await FullAsync(ct).ConfigureAwait(false);
        // since 语义 = 桶内 UpdatedAt >= since（含边界，分钟精度防同分钟变更漏一轮；重复拉取靠确定性 ID 幂等去重，无害）
        var filtered = string.IsNullOrEmpty(since)
            ? notes
            : notes.Where(n => string.CompareOrdinal(n.UpdatedAt, since) >= 0).ToList();
        // ISO 8601 UTC 字符串序 = 时间序（string 默认比较器即 ordinal）
        var newest = notes.Count > 0 ? notes.Max(n => n.UpdatedAt) : null;
        return new SyncPullResult(filtered, newest);
    }

    public async Task<SyncPushResult> PushAsync(IReadOnlyList<SyncNote> changes, string? lastCursor, CancellationToken ct)
    {
        await EnsureDirectoryAsync(ct).ConfigureAwait(false);
        var meta = await GetMetaAsync(ct).ConfigureAwait(false) ?? new SyncMeta();

        // 分桶：ISO 周 + ≤MaxBatchSize/桶
        var targetBuckets = Bucketize(changes);

        // 整桶 PUT：仅当内容与云端现有桶不同才写（幂等 + 省请求）
        foreach (var pair in targetBuckets)
        {
            string? existing = null;
            if (meta.Buckets.Contains(pair.Key))
                existing = await GetFileAsync(pair.Key, ct).ConfigureAwait(false);
            if (existing == pair.Value) continue;
            await PutFileAsync(pair.Key, pair.Value, ct).ConfigureAwait(false);
        }

        // 孤儿桶清理：目标清单外、云端旧清单里有的 → DELETE（防已删笔记经旧桶复活，§2 桶清单铁律）
        var targetNames = new HashSet<string>(targetBuckets.Keys);
        foreach (var old in meta.Buckets)
        {
            if (!targetNames.Contains(old))
                await DeleteFileAsync(old, ct).ConfigureAwait(false);
        }

        // 更新 meta：桶清单 = 目标桶；游标 = 最新 updatedAt（只进不退）
        meta.Buckets = targetBuckets.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (changes.Count > 0)
        {
            var newest = changes.Max(n => n.UpdatedAt) ?? "";
            if (string.IsNullOrEmpty(meta.Cursor) || string.CompareOrdinal(newest, meta.Cursor) > 0)
                meta.Cursor = newest;
        }
        await PutMetaAsync(meta, ct).ConfigureAwait(false);

        return new SyncPushResult(meta.Cursor);
    }

    public async Task<List<SyncNote>> FullAsync(CancellationToken ct)
    {
        // 拉取路径同样确保目录存在（修复换账号/云端目录缺失时首次拉取 PROPFIND 404 直接报错，2026-09-05）
        await EnsureDirectoryAsync(ct).ConfigureAwait(false);
        var files = await ListFilesAsync(ct).ConfigureAwait(false);
        var notes = new List<SyncNote>();
        foreach (var f in files.Where(f => f.StartsWith("notes-", StringComparison.Ordinal) &&
                                           f.EndsWith(".json", StringComparison.Ordinal)))
        {
            try
            {
                var json = await GetFileAsync(f, ct).ConfigureAwait(false);
                var bucket = SyncBucket.FromJson(json);
                if (bucket?.Notes != null) notes.AddRange(bucket.Notes);
            }
            catch (SyncProviderException ex) when (ex.StatusCode == 404)
            {
                // 桶被并发删除：跳过（§6 未知处理 5：桶损坏/缺失跳过继续）
            }
            catch (JsonException)
            {
                // 桶 JSON 解析失败：跳过该桶，继续其他桶，不中断全量同步（§6 未知处理 5）
            }
        }
        return notes;
    }

    public async Task<SyncMeta?> GetMetaAsync(CancellationToken ct)
    {
        try
        {
            var json = await GetFileAsync("sync_meta.json", ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SyncMeta>(json, SyncJson.Options);
        }
        catch (SyncProviderException ex) when (ex.StatusCode == 404)
        {
            return null;   // 首配设备：云端无 meta
        }
        catch (JsonException)
        {
            return null;   // meta 损坏：当首配处理（新写覆盖），本地盐缓存仍可用
        }
    }

    public async Task SaveSaltAsync(string saltBase64, CancellationToken ct)
    {
        await EnsureDirectoryAsync(ct).ConfigureAwait(false);
        var meta = await GetMetaAsync(ct).ConfigureAwait(false) ?? new SyncMeta();
        meta.SaltBase64 = saltBase64;
        await PutMetaAsync(meta, ct).ConfigureAwait(false);
    }

    // ── WebDAV 方言 ──

    /// <summary>首次同步前确保 Base URL 目录存在：PROPFIND 404/405/409 → MKCOL（坚果云自定义子目录不会自动存在，QUEST-5 审查补充）。</summary>
    private async Task EnsureDirectoryAsync(CancellationToken ct)
    {
        var (exists, status) = await PropFindAsync(ct).ConfigureAwait(false);
        if (exists) return;
        if (status == 404 || status == 405 || status == 409)
            await MkColAsync(ct).ConfigureAwait(false);
        else
            throw new SyncProviderException(status, $"WebDAV 目录探测失败 (HTTP {status})");
    }

    private async Task<(bool Exists, int Status)> PropFindAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), _baseUrl);
        req.Headers.Add("Depth", "0");
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return (true, (int)resp.StatusCode);
            return (false, (int)resp.StatusCode);
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            throw new SyncProviderException(0, $"网络错误：{ex.Message}");
        }
    }

    private async Task<List<string>> ListFilesAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), _baseUrl);
        req.Headers.Add("Depth", "1");
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new SyncProviderException((int)resp.StatusCode, $"列目录失败 (HTTP {(int)resp.StatusCode})");
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var doc = XDocument.Parse(body);
            var names = new List<string>();
            foreach (var respEl in doc.Descendants(DavNs + "response"))
            {
                var href = respEl.Element(DavNs + "href")?.Value;
                if (string.IsNullOrEmpty(href)) continue;
                var name = href.TrimEnd('/').Split('/').LastOrDefault();
                if (!string.IsNullOrEmpty(name) && name != _baseUrl.TrimEnd('/').Split('/').LastOrDefault())
                    names.Add(name);
            }
            return names.Distinct().ToList();
        }
        catch (SyncProviderException) { throw; }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            throw new SyncProviderException(0, $"网络错误：{ex.Message}");
        }
        catch (Exception ex)
        {
            throw new SyncProviderException(0, $"PROPFIND 响应解析失败：{ex.Message}");
        }
    }

    private Task<string> GetFileAsync(string fileName, CancellationToken ct)
        => SendAsync(new HttpRequestMessage(HttpMethod.Get, _baseUrl + fileName), "读取", fileName, ct);

    private Task PutFileAsync(string fileName, string content, CancellationToken ct)
        => SendAsync(new HttpRequestMessage(HttpMethod.Put, _baseUrl + fileName) { Content = new StringContent(content, Encoding.UTF8, "application/json") }, "上传", fileName, ct);

    private Task DeleteFileAsync(string fileName, CancellationToken ct)
        => SendAsync(new HttpRequestMessage(HttpMethod.Delete, _baseUrl + fileName), "删除", fileName, ct);

    private Task MkColAsync(CancellationToken ct)
        => SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), _baseUrl), "创建目录", _baseUrl, ct);

    /// <summary>统一发送并做 401/503/网络错误分类（QUEST-5 §7 第七步 5）。</summary>
    private async Task<string> SendAsync(HttpRequestMessage req, string action, string target, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return body;
            }
            var code = (int)resp.StatusCode;
            var msg = code switch
            {
                401 => "坚果云授权码无效，请在坚果云客户端（或手机 APP）『设置 → 第三方应用管理』重新生成",
                503 or 429 => $"坚果云限流 (HTTP {code})",
                _ => $"{action}失败 (HTTP {code})：{target}"
            };
            throw new SyncProviderException(code, msg);
        }
        catch (SyncProviderException) { throw; }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            throw new SyncProviderException(0, $"网络错误：{ex.Message}");
        }
    }

    private static bool IsNetworkError(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException;

    /// <summary>更新云端 sync_meta.json（保留云端盐——本机不生成盐，盐由首配设备生成；本类只透传）。</summary>
    private Task PutMetaAsync(SyncMeta meta, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(meta, SyncJson.Options);
        return PutFileAsync("sync_meta.json", json, ct);
    }

    // ── 桶拆分（第二层契约：WebDAV 与将来 Server 两端一致的存储格式） ──

    private Dictionary<string, string> Bucketize(IReadOnlyList<SyncNote> notes)
    {
        var result = new Dictionary<string, string>();
        var groups = notes.GroupBy(n => GetIsoWeekKey(n.UpdatedAt));
        foreach (var group in groups)
        {
            var ordered = group.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
            var seq = 1;
            for (int i = 0; i < ordered.Count; i += Limits.MaxBatchSize)
            {
                var chunk = ordered.Skip(i).Take(Limits.MaxBatchSize).ToList();
                var fileName = $"notes-{group.Key}-{seq}.json";
                var bucket = new SyncBucket { Bucket = fileName[..^".json".Length], Notes = chunk };
                result[fileName] = bucket.ToJson();
                seq++;
            }
        }
        return result;
    }

    /// <summary>ISO 周键（yyyy-Www）：确定性分桶依据（同款代码双机一致即可，不要求与严格 ISO 8601 完全对齐）。</summary>
    private static string GetIsoWeekKey(string updatedAtUtc)
    {
        if (!DateTimeOffset.TryParse(updatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            dto = DateTimeOffset.UtcNow;
        var dt = dto.UtcDateTime;
        var cal = CultureInfo.InvariantCulture.Calendar;
        var week = cal.GetWeekOfYear(dt, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        var isoYear = dt.Year;
        if (dt.Month == 1 && week >= 52) isoYear--;
        if (dt.Month == 12 && week == 1) isoYear++;
        return $"{isoYear}-W{week:D2}";
    }
}
