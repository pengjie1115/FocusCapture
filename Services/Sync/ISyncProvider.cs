using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FocusCapture.Services.Sync;

/// <summary>
/// 渠道频率/容量上报：引擎按 Limits 自动选择合并窗口与批大小（可插拔架构三件套之"频率策略参数化"）。
/// WebDAV 上报 30/200/500（坚果云实测红线）；将来 Server 上报 3/500/10000 —— 引擎零改动。
/// </summary>
public record SyncLimits(int MinIntervalSeconds = 30, int MaxBatchSize = 200, int MaxRequestsPerWindow = 500);

/// <summary>增量拉取结果：Notes = since 之后的变更；NewSince = 当前扫描到的云端最新 updatedAt（UTC ISO 字符串）。</summary>
public record SyncPullResult(List<SyncNote> Notes, string? NewSince);

/// <summary>批量推送结果：NewSince = 推送后云端最新 updatedAt（UTC ISO 字符串）。</summary>
public record SyncPushResult(string? NewSince);

/// <summary>
/// 同步渠道契约（可插拔架构：同步引擎与 UI 只认这层，WebDAV / 将来 Server 只是实现）。
/// since = 上次同步游标（云端最新 updatedAt，UTC ISO 字符串）。
/// </summary>
public interface ISyncProvider
{
    /// <summary>渠道名："WebDAV" / "Server"（仅展示用，引擎不得按名分支逻辑）。</summary>
    string Name { get; }

    /// <summary>频率/容量上报（渠道属性）。</summary>
    SyncLimits Limits { get; }

    /// <summary>
    /// 增量拉取：返回 since 之后的变更 + 最新时间。
    /// WebDAV 无服务端游标：语义 = 拉取全部桶后按 UpdatedAt &gt; since 客户端过滤（含边界，见 QUEST-5 §7 第二步 5），
    /// NewSince = 当前扫描到的最新时间。
    /// </summary>
    Task<SyncPullResult> PullAsync(string? since, CancellationToken ct);

    /// <summary>
    /// 批量推送：changes = 引擎已按"sync_meta 桶清单全量拉取 + 本机变更合并"后的完整目标笔记集
    /// （应存在于云端的全部笔记，含软删标记；Quest-5 §7 第六步 3"push 合并基础 = 全量"）。
    /// 实现负责：按 ISO 周分桶（≤Limits.MaxBatchSize 条/桶）→ 整桶 PUT 覆盖 → 按桶清单 diff 删除孤儿桶
    /// → 更新 sync_meta.json 与游标。整桶覆盖天然幂等——重复推送同一目标集结果一致。
    /// </summary>
    Task<SyncPushResult> PushAsync(IReadOnlyList<SyncNote> changes, string? lastCursor, CancellationToken ct);

    /// <summary>全量导出（新设备首次 / 密钥重置后全量重传）：返回云端全部笔记（密文，未解密）。</summary>
    Task<List<SyncNote>> FullAsync(CancellationToken ct);
}
