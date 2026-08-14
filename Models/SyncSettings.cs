using System;
using System.Collections.Generic;

namespace FocusCapture.Models;

/// <summary>
/// 云同步配置（QUEST-5，进 settings.json，随 AppSettings 源生成序列化）。
/// WebDavToken 为 DPAPI 密文（ProtectedData.CurrentUser），RecoveryCodeHash 为加盐哈希；
/// 主密码 / 派生密钥 / 恢复码明文任何位置都不存。
/// </summary>
public class SyncSettings
{
    /// <summary>本机设备 ID（GUID，首次启动生成并持久化）——回声识别依据（§5.0.4）。</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>渠道名："WebDAV" / "Server"（引擎不得按名分支逻辑）。</summary>
    public string ProviderName { get; set; } = "";

    public string WebDavUrl { get; set; } = "https://dav.jianguoyun.com/dav/FocusCapture/";
    public string WebDavUser { get; set; } = "";

    /// <summary>坚果云授权码，DPAPI 加密后存（Base64）；解密失败兜底为空并提示重新配置，不崩。</summary>
    public string WebDavToken { get; set; } = "";

    /// <summary>同步游标（云端最新 updatedAt，UTC ISO 字符串）。</summary>
    public string LastCursor { get; set; } = "";

    public bool AutoSyncEnabled { get; set; } = false;

    /// <summary>E2EE 盐（Base64）。首配设备生成、明文存云端 sync_meta.json；本地缓存供离线派生（跨设备一致）。</summary>
    public string E2eeSalt { get; set; } = "";

    public string RecoveryCodeHash { get; set; } = "";   // 恢复码加盐哈希（Base64）
    public string RecoveryCodeSalt { get; set; } = "";   // 恢复码哈希盐（Base64，同存防暴力枚举）

    /// <summary>待推软删清单（清空回收站时压入 Deleted=true 的笔记，推送成功后清空）。</summary>
    public List<SyncNote> PendingDeletes { get; set; } = new();

    /// <summary>上次同步结果展示（"成功" / "失败: 原因" / ""）。</summary>
    public string LastSyncResult { get; set; } = "";

    /// <summary>上次同步时间（本地时间字符串，仅展示）。</summary>
    public string LastSyncAt { get; set; } = "";

    /// <summary>确保 DeviceId 存在（无则生成 GUID；调用方负责 Save 持久化）。</summary>
    public void EnsureDeviceId()
    {
        if (string.IsNullOrWhiteSpace(DeviceId))
            DeviceId = Guid.NewGuid().ToString("N");
    }

    // ── WebDAV 授权码 DPAPI 保护（Windows 原生 crypt32.dll，P/Invoke 封装见 Services/Sync/Dpapi.cs；
    //    环境无外网装不了官方 ProtectedData 包，改用等价原生 API，2026-08-13 审查调整） ──

    public static string ProtectToken(string plainToken)
        => Services.Sync.Dpapi.Protect(plainToken);

    /// <summary>解密失败兜底返回 null（调用方提示重新配置，不崩）。</summary>
    public static string? UnprotectToken(string protectedBase64)
        => Services.Sync.Dpapi.Unprotect(protectedBase64);
}
