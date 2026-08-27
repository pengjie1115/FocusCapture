using System.Text.RegularExpressions;
using System.Threading;
using FocusCapture.Models;
using FocusCapture.Services.AI;

namespace FocusCapture.Services;

/// <summary>
/// 编辑待办公共服务（v3.5）：两条保存路径（行内 SaveEditNote / 全屏 NoteEditWindow.Save）共用，
/// 防逻辑漂移。
/// - SaveEdited：待办内容变化 → UpdateTodo 原地改行（红线 2 例外，禁止追加【编辑】行）；
///               普通笔记走原 AppendEdit（现状不变）。
/// - DetectDueAsync：时间识别，本地规则（TimeParser）优先，规则未命中才调 LLM 兜底；
///               未配 Key / 调用异常 → 优雅降级返回 null（不弹建议、不崩溃）。
/// </summary>
public static class TodoEditService
{
    /// <summary>编辑保存：待办原地改行 / 笔记 AppendEdit。返回是否保存成功。</summary>
    public static bool SaveEdited(NoteService notes, NoteEntry entry, string newContent)
    {
        var content = newContent.Trim();
        if (string.IsNullOrEmpty(content)) return false;
        if (entry.Type == NoteType.Todo)
            return notes.UpdateTodo(entry, newContent: content);
        return notes.AppendEdit(entry, content);
    }

    /// <summary>
    /// 时间识别：TimeParser 本地规则优先，规则未命中才调 LLM 兜底（返回唯一提醒时间，无则 null）。
    /// 异常（未配 Key / 网络失败）一律捕获降级返回 null。
    /// </summary>
    public static async Task<DateTime?> DetectDueAsync(string text, IChatProvider? llm, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 规则优先：能本地规则解决绝不调 LLM（输入框场景直接禁止 LLM）
        if (TimeParser.TryParse(text, out var due)) return due;

        // 规则未命中 → LLM 兜底；未注入 provider（未配 Key）→ 优雅降级不弹建议不崩溃
        if (llm == null) return null;
        try
        {
            var messages = PromptBuilder.BuildTimeParseMessages(text);
            var result = await llm.CompleteAsync(messages, ct);
            var json = result.Trim();
            // 只认 has_time=true 的输出；time 字段解析失败/已过 → null
            if (!Regex.IsMatch(json, "\"has_time\"\\s*:\\s*true", RegexOptions.IgnoreCase)) return null;
            var m = Regex.Match(json, "\"time\"\\s*:\\s*\"([^\"]+)\"");
            if (!m.Success) return null;
            if (DateTime.TryParse(m.Groups[1].Value, out var t) && t > DateTime.Now) return t;
            return null;
        }
        catch
        {
            // 未配 Key（空 key）/ 网络失败 / 超时 → 降级不弹建议，编辑保存不受影响
            return null;
        }
    }
}