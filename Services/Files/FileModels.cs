using System.Text.Json.Serialization;

namespace FocusCapture.Services.Files;

/// <summary>
/// 文件类型。用字符串而非枚举是刻意的：元数据要跨设备、跨版本合并且**只增不改**，
/// 将来新增类型时旧客户端至少能原样保留这条记录，而不是反序列化直接抛。
/// </summary>
public static class FileTypes
{
    /// <summary>AI 产出（云端永久保留）</summary>
    public const string Artifact = "artifact";

    /// <summary>用户主动上传（云端永久保留）</summary>
    public const string Upload = "upload";

    /// <summary>对话附件（云端按 expireAt 到期清理）</summary>
    public const string Attachment = "attachment";

    public static readonly string[] All = { Artifact, Upload, Attachment };

    public static bool IsValid(string? type) => type != null && Array.IndexOf(All, type) >= 0;

    public static string Label(string? type) => type switch
    {
        Artifact => "AI 产出",
        Upload => "已上传",
        Attachment => "对话附件",
        _ => "文件",
    };
}

/// <summary>
/// 文件元数据（方案 §4.1）。**只记客观事实，不记任何设备状态** —— 这是整个设计的地基：
/// 元数据近似只增不改，多设备合并才降级成「按 id 求并集」这么简单。
/// 会变的状态（有没有本地副本、传到哪一步了）一律放 CacheEntry，那边只存本机、绝不上云。
/// </summary>
public sealed class FileMetadata
{
    /// <summary>稳定唯一 ID = SHA256(内容) 前 32 位。同内容天然同 id，是「MD5 查重」之外的第二层去重。</summary>
    public string Id { get; set; } = "";

    /// <summary>原始文件名（网盘侧也保留原名，便于用户人工辨认）。</summary>
    public string Name { get; set; } = "";

    /// <summary>网盘沙箱内路径，形如 /apps/FocusCapture/files/xxx.md</summary>
    public string NetPath { get; set; } = "";

    public long Size { get; set; }

    /// <summary>内容 MD5：触发百度秒传 + 上传前查重（防重复条目）。</summary>
    public string Md5 { get; set; } = "";

    /// <summary>见 <see cref="FileTypes"/>。</summary>
    public string Type { get; set; } = FileTypes.Upload;

    /// <summary>标签，供本地检索。</summary>
    public List<string> Tags { get; set; } = new();

    public DateTime CreatedAt { get; set; }

    /// <summary>最近修改（仅重命名 / 改标签时更新）。</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>**仅 attachment 有值**，到期清云端。</summary>
    public DateTime? ExpireAt { get; set; }

    /// <summary>
    /// 彻底删除墓碑：本地删了不算删，这里为 true 才代表「用户显式不要这份文件了」。
    /// 云端删除已入回收站，这是防旧记录复活的那道闸。
    /// </summary>
    public bool Deleted { get; set; }

    /// <summary>文件扩展名（小写，不含点）。</summary>
    [JsonIgnore]
    public string Extension
    {
        get
        {
            var idx = Name.LastIndexOf('.');
            return idx >= 0 && idx < Name.Length - 1 ? Name[(idx + 1)..].ToLowerInvariant() : "";
        }
    }

    public FileMetadata Clone() => new()
    {
        Id = Id, Name = Name, NetPath = NetPath, Size = Size, Md5 = Md5, Type = Type,
        Tags = new List<string>(Tags), CreatedAt = CreatedAt, UpdatedAt = UpdatedAt,
        ExpireAt = ExpireAt, Deleted = Deleted,
    };
}

/// <summary>本机缓存条目的来源（方案 §4.2 防线①的载体）。</summary>
public static class CacheOrigins
{
    /// <summary>本地新增，需要上传。</summary>
    public const string Local = "local";

    /// <summary>从网盘取回，**永不触发上传**（堵死「取回 → 自动上传 → 重复」的回环）。</summary>
    public const string Downloaded = "downloaded";
}

/// <summary>本机上传状态（只在本机记账，绝不写云端）。</summary>
public static class UploadStates
{
    public const string Pending = "pending";
    public const string Uploading = "uploading";
    public const string Uploaded = "uploaded";
    public const string Failed = "failed";
}

/// <summary>
/// 本机缓存账本条目（方案 §4.2）。**只存在本机，绝不上云** ——
/// 淘汰是设备级事件，写进云端就会把「自动过期」变成「永久删除」。
/// </summary>
public sealed class CacheEntry
{
    /// <summary>对应 FileMetadata.Id</summary>
    public string Id { get; set; } = "";

    /// <summary>本地文件路径（缓存账本记的是本机真实路径，元数据里不记 —— 那是设备状态）。</summary>
    public string LocalPath { get; set; } = "";

    /// <summary>何时到本机</summary>
    public DateTime CachedAt { get; set; }

    /// <summary>最后使用时间 —— **淘汰排序依据**</summary>
    public DateTime LastAccess { get; set; }

    /// <summary>见 <see cref="CacheOrigins"/>。</summary>
    public string Origin { get; set; } = CacheOrigins.Local;

    /// <summary>见 <see cref="UploadStates"/>。</summary>
    public string UploadState { get; set; } = UploadStates.Pending;

    /// <summary>重试次数，失败后界面提示用。</summary>
    public int UploadRetry { get; set; }

    /// <summary>
    /// 「优先淘汰」标记：云端附件到期清理后贴在这里，交淘汰器统一处理。
    /// 为什么不立刻删本地 —— 用户可能正在用；贴标记后走同一个删除出口，避免两套删除逻辑打架。
    /// </summary>
    public bool PendingEvict { get; set; }
}

/// <summary>文件模块专用序列化上下文（源生成，与主上下文隔离）。</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<FileMetadata>))]
[JsonSerializable(typeof(List<CacheEntry>))]
[JsonSerializable(typeof(FileMetadata))]
[JsonSerializable(typeof(CacheEntry))]
internal partial class FileJsonContext : JsonSerializerContext
{
}
