using System.Threading;

namespace FocusCapture.Services.Files;

/// <summary>
/// 对话附件到期清理（方案 §5.6，决策 10）。
///
/// 只管 <see cref="FileTypes.Attachment"/> 一类 —— AI 产出与用户主动上传的文件云端永久保留。
/// 这条分级是刻意的：一刀切清理会让网盘从「全量权威仓库」退化成「滚动窗口」，
/// 整个「淘汰只在本地记账、云端是最后安全网」的设计会一起失效。
///
/// 本地那份**不立刻删**：用户可能正开着它。做法是打「优先淘汰」标记，交淘汰器统一处理
/// —— 所有删本地文件的动作只走一个出口，才不会出现两套删除逻辑打架。
///
/// 副作用（已知并接受）：云端附件到期删除后，历史对话里的附件引用会变成死链。
/// </summary>
public static class AttachmentExpiryService
{
    /// <summary>跑一轮到期清理，返回清理的条数。</summary>
    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        List<FileMetadata> expired;
        try
        {
            expired = FileRepository.ExpiredAttachments(DateTime.Now);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Files", "扫描到期附件失败：" + ex.Message);
            return 0;
        }

        if (expired.Count == 0) return 0;
        if (FileRepository.Cloud?.IsReady != true)
        {
            // 没连网就不动：无法确认云端到底删没删，贸然打墓碑会让用户以为文件没了
            AppLog.Info("Files", $"有 {expired.Count} 个附件已到期，但当前未连接网盘，等下次再清");
            return 0;
        }

        var done = 0;
        foreach (var meta in expired)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await FileRepository.Cloud.DeleteAsync(new[] { meta.NetPath }, ct).ConfigureAwait(false);
                FileRepository.MarkDeleted(meta.Id);      // 云端确实没了 → 记录打墓碑
                FileRepository.MarkPendingEvict(meta.Id); // 本地那份贴标记，等淘汰器统一清
                done++;
            }
            catch (Exception ex)
            {
                // 删不掉就留着下轮再试。此处绝不能「先打墓碑、后删云端」—— 顺序颠倒会造出死链。
                AppLog.Warn("Files", $"附件「{meta.Name}」到期清理失败，下轮重试：{ex.Message}");
            }
        }

        if (done > 0)
            AppLog.Info("Files", $"附件到期清理：云端已清理 {done} 个（本地副本待淘汰）");
        return done;
    }
}
