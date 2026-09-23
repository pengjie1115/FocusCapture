using System.Text.Json.Nodes;
using FocusCapture.Models;

namespace FocusCapture.Services.Destinations.GetNote;

/// <summary>
/// 按钮路径推送编排：去重 → 包装台① → 确认弹窗 → 走 GetNoteDestination 的 getnote_save_note（卡车唯一通道）→ 记指纹。
/// 限流/失败重试：每篇最多 3 次尝试（1 + 重试 2 次），间隔 1 秒；部分成功不回滚已推篇目。
/// </summary>
public class GetNotePushService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly AppSettings _settings;
    private readonly GetNoteDestination _destination;
    private readonly GetNoteSyncState _syncState;

    /// <summary>发送前确认回调（UI 弹窗实现）：返回 true = 确认推送</summary>
    public Func<string, Task<bool>>? ConfirmHandler { get; set; }

    public GetNotePushService(AppSettings settings, GetNoteDestination destination, GetNoteSyncState syncState)
    {
        _settings = settings;
        _destination = destination;
        _syncState = syncState;
    }

    public sealed record PushOutcome(bool Success, bool Cancelled, int PushedEntries, int SkippedEntries, int NoteCount, string Message);

    public async Task<PushOutcome> PushAsync(IReadOnlyList<NoteEntry> entries, bool fromSelection, System.Threading.CancellationToken ct = default)
    {
        if (entries.Count == 0)
            return new PushOutcome(false, false, 0, 0, 0, "当前没有可上传的笔记。");

        if (string.IsNullOrWhiteSpace(_settings.GetNoteApiKey) || string.IsNullOrWhiteSpace(_settings.GetNoteClientId))
            return new PushOutcome(false, false, 0, 0, 0, "未配置得到大脑凭证（API Key / Client ID），请到设置 → AI 功能 → 得到大脑 配置。");

        // ── 去重：每条笔记一生只推一次 ──
        var pending = new List<NoteEntry>();
        var skipped = 0;
        foreach (var e in entries)
        {
            if (_syncState.TryGetNoteId(e, out _)) skipped++;
            else pending.Add(e);
        }
        if (pending.Count == 0)
            return new PushOutcome(true, false, 0, skipped, 0,
                $"这 {entries.Count} 条都已推送过得到大脑，本次 0 条新增。");

        // ── 包装台①：制式包装（超长自动分篇）──
        var chunks = GetNotePayloadBuilder.Build(pending, fromSelection);

        // ── 发送前确认 ──
        if (ConfirmHandler != null)
        {
            var topicName = string.IsNullOrWhiteSpace(_settings.GetNoteDefaultTopicName)
                ? "账号默认库" : _settings.GetNoteDefaultTopicName;
            var titles = string.Join("\n", chunks.Select((c, i) => $"  {i + 1}. 《{c.Title}》（{c.Entries.Count} 条）"));
            var desc = $"将推送 {chunks.Count} 篇到知识库【{topicName}】：\n{titles}\n" +
                       $"共含 {pending.Count} 条" + (skipped > 0 ? $"（另有 {skipped} 条已推送过，自动跳过）" : "") + "\n\n确认推送？";
            if (!await ConfirmHandler(desc))
            {
                AppLog.Info("GetNote", "按钮推送：用户取消");
                return new PushOutcome(false, true, 0, skipped, 0, "已取消推送。");
            }
        }

        // ── 逐篇推送（失败重试，部分成功不回滚）──
        var pushedEntries = 0;
        var errors = new List<string>();
        foreach (var chunk in chunks)
        {
            var result = await PushChunkAsync(chunk, ct);
            if (result.Success)
            {
                _syncState.Record(chunk.Entries, result.NoteId ?? "");
                pushedEntries += chunk.Entries.Count;
                AppLog.Info("GetNote", $"推送成功：《{chunk.Title}》{chunk.Entries.Count} 条，note_id={result.NoteId}");
            }
            else
            {
                errors.Add($"《{chunk.Title}》：{result.Message}");
                AppLog.Warn("GetNote", $"推送失败：《{chunk.Title}》：{result.Message}");
            }
        }

        if (errors.Count == 0)
        {
            var msg = $"已推送 {chunks.Count} 篇（{pushedEntries} 条）到得到大脑。";
            if (skipped > 0) msg += $"另有 {skipped} 条已推送过，自动跳过。";
            return new PushOutcome(true, false, pushedEntries, skipped, chunks.Count, msg);
        }

        var failMsg = $"推送完成 {pushedEntries} 条，失败 {pending.Count - pushedEntries} 条：\n" + string.Join("\n", errors);
        if (skipped > 0) failMsg += $"\n（另有 {skipped} 条已推送过，自动跳过）";
        return new PushOutcome(false, false, pushedEntries, skipped, chunks.Count - errors.Count, failMsg);
    }

    /// <summary>单篇推送：通过适配器统一执行入口（卡车），失败重试至 MaxAttempts 次</summary>
    private async Task<OutboundResult> PushChunkAsync(GetNotePayloadBuilder.PayloadChunk chunk, System.Threading.CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["title"] = chunk.Title,
            ["content"] = chunk.Content,
            ["tags"] = new JsonArray(chunk.Tags.Select(t => (JsonNode?)t).ToArray()),
        };
        if (!string.IsNullOrWhiteSpace(_settings.GetNoteDefaultTopicId))
            body["topic_id"] = _settings.GetNoteDefaultTopicId;

        OutboundResult result = new(false, "未执行");
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            result = await _destination.ExecuteAsync("getnote_save_note", body.ToJsonString(), ct);
            if (result.Success) break;
            if (attempt < MaxAttempts) await Task.Delay(RetryDelay, ct);
        }
        return result;
    }
}
