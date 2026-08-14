using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusCapture.Models;

/// <summary>
/// 云端同步数据模型（第二层契约：与云端桶 JSON 字段完全一致，camelCase）。
/// Content/Tags 在云端为 E2EE 密文（由 CryptoService 负责），本地为明文；
/// Id/SchemaVersion/CreatedAt/UpdatedAt/Deleted/DeviceId 恒为明文（引擎对账需要）。
/// [JsonPropertyName] 显式锁定 camelCase 契约：桶文件（SyncJson.Options）与
/// settings.json 内 PendingDeletes（AppJsonContext 源生成，默认 PascalCase）两条序列化路径命名一致。
/// </summary>
public class SyncNote
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;   // 格式演进预留
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";          // 确定性哈希
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";     // 云端=密文；本地=明文
    [JsonPropertyName("tags")]
    public string[] Tags { get; set; } = [];      // 云端=密文数组；本地=明文
    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";   // ISO 8601 UTC，明文（不敏感）
    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = "";   // ISO 8601 UTC，明文（对账需要）
    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";    // 最后修改设备
    [JsonPropertyName("prevContent")]
    public string? PrevContent { get; set; }      // 冲突被覆盖方快照（本地留存）

    /// <summary>
    /// 确定性 ID：SHA256(相对路径 + "|" + 完整行内容) 前 16 字节 hex（小写）。
    /// 同一行在任何设备生成相同 ID → 天然幂等、天然去重、无需索引表。
    /// 相对路径 = 相对 NotesPath 的路径（如 "灵感_2026-08-12.md"），保证双机路径一致 → ID 一致。
    /// </summary>
    public static string ComputeId(string relativePath, string lineContent)
    {
        var raw = relativePath + "|" + lineContent;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    public string ToJson() => JsonSerializer.Serialize(this, SyncJson.Options);

    public static SyncNote? FromJson(string json)
        => JsonSerializer.Deserialize<SyncNote>(json, SyncJson.Options);
}

/// <summary>
/// 云端桶文件结构：{ "bucket": "notes-2026-W33-1", "notes": [SyncNote...] }。
/// 打包存储（每桶 ≤200 条），禁止每条笔记一个文件。
/// </summary>
public class SyncBucket
{
    [JsonPropertyName("bucket")]
    public string Bucket { get; set; } = "";
    [JsonPropertyName("notes")]
    public List<SyncNote> Notes { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, SyncJson.Options);

    public static SyncBucket? FromJson(string json)
        => JsonSerializer.Deserialize<SyncBucket>(json, SyncJson.Options);
}

/// <summary>同步层统一序列化配置：camelCase，对齐 PRD §5.0.3 契约（WebDAV 与将来 Server 共用同一份）。</summary>
internal static class SyncJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
