using System.Threading;
using FocusCapture.Models;

namespace FocusCapture.Services.Agent;

/// <summary>参数解析助手</summary>
public static class ToolArgs
{
    public static bool TryGetString(string json, string key, out string value)
    {
        value = "";
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(key, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                value = prop.GetString() ?? "";
                return value.Trim().Length > 0;
            }
        }
        catch (JsonException) { }
        return false;
    }

    public static bool TryGetInt(string json, string key, out int value)
    {
        value = 0;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(key, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value)) return true;
                if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out value)) return true;
            }
        }
        catch (JsonException) { }
        return false;
    }
}

/// <summary>按关键词搜索本地笔记（只读）。走 NoteService.LoadNotesSearch，每条截 200 字。</summary>
public class SearchNotesTool : AgentTool
{
    private readonly NoteService _noteService;
    public SearchNotesTool(NoteService noteService) => _noteService = noteService;

    public override string Name => "search_notes";
    public override string Description =>
        "按关键词搜索用户的本地笔记（不区分大小写，匹配内容/编辑内容/来源窗口）。" +
        "用户提到\"我记过的/本地的/之前记的\"类意图时使用。返回命中条目列表，可能为空。";
    public override string ParametersJson =>
        """{"type":"object","properties":{"query":{"type":"string","description":"搜索关键词"}},"required":["query"]}""";
    public override bool IsReadOnly => true;

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "query", out var query))
            return "错误：缺少参数 query（搜索关键词）。";

        var entries = await Task.Run(() => _noteService.LoadNotesSearch(query.Trim()), ct);
        if (entries.Count == 0) return $"未找到包含「{query}」的本地笔记。";

        var lines = entries
            .OrderByDescending(e => e.Timestamp)
            .Take(20)
            .Select(e => $"[{e.Timestamp:yyyy-MM-dd HH:mm}] {FormatContent(e)}");
        return $"共 {entries.Count} 条命中，显示最近 20 条：\n" + string.Join("\n", lines);
    }

    internal static string FormatContent(NoteEntry e)
    {
        var text = (e.EditedContent ?? e.Content).Replace("\u23CE", " ");
        text = text.Replace("\r\n", " ").Replace("\n", " ");
        if (text.Length > 200) text = text[..200] + "…";
        var prefix = e.Type == NoteType.Todo ? "【待办】" : "";
        return $"{prefix}{text}";
    }
}

/// <summary>列出未完成待办（只读）。全部 Open 状态，按提醒时间排序。</summary>
public class ListTodosTool : AgentTool
{
    private readonly NoteService _noteService;
    public ListTodosTool(NoteService noteService) => _noteService = noteService;

    public override string Name => "list_todos";
    public override string Description => "列出用户所有未完成的待办（含提醒时间），按提醒时间排序。";
    public override string ParametersJson => """{"type":"object","properties":{}}""";
    public override bool IsReadOnly => true;

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var todos = await Task.Run(() => _noteService.LoadAllEntries()
            .Where(e => e.Type == NoteType.Todo && e.TodoStatus == TodoStatus.Open)
            .OrderBy(e => e.DueTime ?? DateTime.MaxValue)
            .ToList(), ct);

        if (todos.Count == 0) return "当前没有未完成的待办。";

        var lines = todos.Take(30).Select(e =>
        {
            var due = e.DueTime.HasValue ? $" 提醒: {e.DueTime:yyyy-MM-dd HH:mm}" : "";
            var overdue = e.DueTime.HasValue && e.DueTime.Value < DateTime.Now ? "（已过期）" : "";
            return $"[{e.Timestamp:yyyy-MM-dd HH:mm}] {SearchNotesTool.FormatContent(e)}{due}{overdue}";
        });
        return $"共 {todos.Count} 条未完成待办：\n" + string.Join("\n", lines);
    }
}

/// <summary>记录一条灵感/笔记到本地（写，需确认）。走 NoteService.SaveAiNote。</summary>
public class SaveQuickNoteTool : AgentTool
{
    private readonly NoteService _noteService;
    public SaveQuickNoteTool(NoteService noteService) => _noteService = noteService;

    public override string Name => "save_quick_note";
    public override string Description =>
        "把一段内容保存为用户的本地笔记（灵感）。适合\"帮我记一下/存成笔记/记一条灵感\"类请求。" +
        "保存前会向用户弹确认。";
    public override string ParametersJson =>
        """{"type":"object","properties":{"content":{"type":"string","description":"笔记正文，可多行"}},"required":["content"]}""";
    public override bool IsReadOnly => false;

    public override string DescribeAction(string argumentsJson)
    {
        ToolArgs.TryGetString(argumentsJson, "content", out var content);
        var preview = content.Length > 40 ? content[..40] + "…" : content;
        return $"记录一条灵感到本地笔记：{preview}";
    }

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "content", out var content))
            return "错误：缺少参数 content（笔记正文）。";

        var entry = await Task.Run(() => _noteService.SaveAiNote(content), ct);
        return entry != null
            ? $"已保存到本地笔记（{entry.Timestamp:yyyy-MM-dd HH:mm}）。"
            : "错误：笔记写入失败。";
    }
}
