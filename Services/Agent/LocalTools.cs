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

/// <summary>
/// 条目定位：按 ref_time（[yyyy-MM-dd HH:mm]，工具列表输出里的时间戳）+ 可选内容关键词
/// 在全部条目中精确找到唯一一条。找不到/有歧义时返回错误说明，让模型向用户澄清。
/// </summary>
public static class EntryResolver
{
    public static bool TryResolve(IReadOnlyList<NoteEntry> entries, string argumentsJson,
        bool todoOnly, out NoteEntry? entry, out string error)
    {
        entry = null;
        error = "";

        if (!ToolArgs.TryGetString(argumentsJson, "ref_time", out var refTime))
        {
            error = "错误：缺少参数 ref_time（格式 yyyy-MM-dd HH:mm，来自笔记/待办列表输出中方括号里的时间戳）。";
            return false;
        }
        if (!DateTime.TryParseExact(refTime.Trim(), "yyyy-MM-dd HH:mm", null,
                System.Globalization.DateTimeStyles.None, out var ts))
        {
            error = $"错误：ref_time「{refTime}」格式不对，必须是 yyyy-MM-dd HH:mm。";
            return false;
        }

        ToolArgs.TryGetString(argumentsJson, "content_hint", out var hint);
        var tsKey = ts.ToString("yyyy-MM-dd HH:mm");

        var candidates = entries
            .Where(e => (!todoOnly || e.Type == NoteType.Todo) &&
                        e.Timestamp.ToString("yyyy-MM-dd HH:mm") == tsKey &&
                        (string.IsNullOrEmpty(hint) ||
                         (e.EditedContent ?? e.Content).Contains(hint, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        switch (candidates.Count)
        {
            case 1:
                entry = candidates[0];
                return true;
            case 0:
                error = $"错误：找不到 {ts:yyyy-MM-dd HH:mm} 对应的{(todoOnly ? "待办" : "条目")}"
                    + (hint == "" ? "" : $"（含「{hint}」）") + "。请先用列表工具核实时间戳与内容后再试。";
                return false;
            default:
                var list = string.Join("\n", candidates.Take(5)
                    .Select(c => $"- [{c.Timestamp:yyyy-MM-dd HH:mm}] {SearchNotesTool.FormatContent(c)}"));
                error = $"错误：{ts:yyyy-MM-dd HH:mm} 命中 {candidates.Count} 条，请补充参数 content_hint（内容关键词）消歧：\n{list}";
                return false;
        }
    }
}

/// <summary>创建一条待办（写，需确认）。due_time 可选；不传时由本地时间规则从正文识别（如"明天中午12点"）。</summary>
public class CreateTodoTool : AgentTool
{
    private readonly NoteService _noteService;
    public CreateTodoTool(NoteService noteService) => _noteService = noteService;

    public override string Name => "create_todo";
    public override string Description =>
        "为用户创建一条待办。适合\"建个待办/提醒我xx/记个待办\"类请求。" +
        "due_time 可选（格式 yyyy-MM-dd HH:mm）；用户没明确给时间时不要编造，留空即可——" +
        "系统会自动从正文识别时间（如\"明天中午12点吃蛋\"会自动设提醒）。";
    public override string ParametersJson =>
        """{"type":"object","properties":{"content":{"type":"string","description":"待办正文"},"due_time":{"type":"string","description":"提醒时间，可选，格式 yyyy-MM-dd HH:mm；仅在用户明确说了时间且无法从正文自然表达时才传"}},"required":["content"]}""";
    public override bool IsReadOnly => false;

    public override string DescribeAction(string argumentsJson)
    {
        ToolArgs.TryGetString(argumentsJson, "content", out var content);
        var due = ToolArgs.TryGetString(argumentsJson, "due_time", out var d) ? $"（提醒 {d}）" : "";
        var preview = content.Length > 40 ? content[..40] + "…" : content;
        return $"创建待办：{preview}{due}";
    }

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "content", out var content))
            return "错误：缺少参数 content（待办正文）。";

        DateTime? due = null;
        if (ToolArgs.TryGetString(argumentsJson, "due_time", out var dueStr))
        {
            if (!DateTime.TryParse(dueStr, out var parsed))
                return $"错误：due_time「{dueStr}」无法解析，请用 yyyy-MM-dd HH:mm 格式。";
            due = parsed;
        }

        var entry = await Task.Run(() => _noteService.SaveNote(content, "AI 对话", NoteType.Todo, due), ct);
        if (entry == null) return "错误：待办写入失败。";

        return entry.DueTime.HasValue
            ? $"已创建待办：{entry.Content}（提醒时间 {entry.DueTime:yyyy-MM-dd HH:mm}）"
            : $"已创建待办：{entry.Content}（无提醒时间）";
    }
}

/// <summary>把待办标记为已办（写，需确认）。</summary>
public class CompleteTodoTool : AgentTool
{
    private readonly NoteService _noteService;
    public CompleteTodoTool(NoteService noteService) => _noteService = noteService;

    public override string Name => "complete_todo";
    public override string Description =>
        "把用户的一条待办标记为已办。ref_time 用列表工具输出中方括号里的时间戳。";
    public override string ParametersJson =>
        """{"type":"object","properties":{"ref_time":{"type":"string","description":"待办时间戳，格式 yyyy-MM-dd HH:mm"},"content_hint":{"type":"string","description":"内容关键词，可选，同分钟多条时用于消歧"}},"required":["ref_time"]}""";
    public override bool IsReadOnly => false;

    public override string DescribeAction(string argumentsJson)
        => $"把待办 [{GetRef(argumentsJson)}] 标记为已办";

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var todos = await Task.Run(() => _noteService.LoadAllEntries()
            .Where(e => e.Type == NoteType.Todo).ToList(), ct);
        if (!EntryResolver.TryResolve(todos, argumentsJson, todoOnly: true, out var entry, out var error))
            return error;

        var ok = await Task.Run(() => _noteService.UpdateTodo(entry!, status: TodoStatus.Done), ct);
        return ok
            ? $"已标记为已办：{entry!.Content}"
            : "错误：待办状态更新失败（文件写入出错或条目已被其他操作改动）。";
    }

    internal static string GetRef(string argumentsJson)
        => ToolArgs.TryGetString(argumentsJson, "ref_time", out var t) ? t : "?";
}

/// <summary>把已办待办改回待办（写，需确认）。</summary>
public class ReopenTodoTool : AgentTool
{
    private readonly NoteService _noteService;
    public ReopenTodoTool(NoteService noteService) => _noteService = noteService;

    public override string Name => "reopen_todo";
    public override string Description =>
        "把用户一条已办的待办改回未完成（待办）状态。ref_time 用列表工具输出中方括号里的时间戳。";
    public override string ParametersJson =>
        """{"type":"object","properties":{"ref_time":{"type":"string","description":"待办时间戳，格式 yyyy-MM-dd HH:mm"},"content_hint":{"type":"string","description":"内容关键词，可选，同分钟多条时用于消歧"}},"required":["ref_time"]}""";
    public override bool IsReadOnly => false;

    public override string DescribeAction(string argumentsJson)
        => $"把待办 [{CompleteTodoTool.GetRef(argumentsJson)}] 改回待办";

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var todos = await Task.Run(() => _noteService.LoadAllEntries()
            .Where(e => e.Type == NoteType.Todo).ToList(), ct);
        if (!EntryResolver.TryResolve(todos, argumentsJson, todoOnly: true, out var entry, out var error))
            return error;

        if (entry!.TodoStatus == TodoStatus.Open)
            return $"该条目本来就是待办状态：{entry.Content}";

        var ok = await Task.Run(() => _noteService.UpdateTodo(entry, status: TodoStatus.Open), ct);
        return ok
            ? $"已改回待办：{entry.Content}"
            : "错误：待办状态更新失败（文件写入出错或条目已被其他操作改动）。";
    }
}

/// <summary>删除一条笔记或待办（写，需确认）。走回收站，可恢复。</summary>
public class DeleteNoteTool : AgentTool
{
    private readonly NoteService _noteService;
    public DeleteNoteTool(NoteService noteService) => _noteService = noteService;

    public override string Name => "delete_note";
    public override string Description =>
        "删除用户的一条笔记或待办（移入回收站，可恢复）。ref_time 用列表/搜索工具输出中方括号里的时间戳。" +
        "仅适合删除单条明确指定的条目；批量删除必须逐条向用户确认。";
    public override string ParametersJson =>
        """{"type":"object","properties":{"ref_time":{"type":"string","description":"条目时间戳，格式 yyyy-MM-dd HH:mm"},"content_hint":{"type":"string","description":"内容关键词，可选，同分钟多条时用于消歧"}},"required":["ref_time"]}""";
    public override bool IsReadOnly => false;

    public override string DescribeAction(string argumentsJson)
        => $"删除条目 [{CompleteTodoTool.GetRef(argumentsJson)}]（移入回收站）";

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var entries = await Task.Run(() => _noteService.LoadAllEntries(), ct);
        if (!EntryResolver.TryResolve(entries, argumentsJson, todoOnly: false, out var entry, out var error))
            return error;

        var ok = await Task.Run(() => _noteService.DeleteNote(entry!), ct);
        return ok
            ? $"已删除（可在回收站恢复）：{SearchNotesTool.FormatContent(entry!)}"
            : "错误：删除失败（回收站写入出错或条目已被其他操作改动），原条目保留。";
    }
}
