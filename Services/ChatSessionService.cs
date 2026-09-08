using System.Text.Json;
using FocusCapture.Services.AI;

namespace FocusCapture.Services;

/// <summary>AI 对话会话：消息列表 + 裁剪 + 持久化（JSON 文件）</summary>
public class ChatSessionService
{
    private const int MaxMessages = 20; // 保留最近 20 条（约 10 轮）

    private readonly List<ChatMessage> _messages;
    private string _sessionFile;     // Load 历史会话时重指向原文件（非 readonly）
    private string _systemPrompt;    // Load 时取文件中保存的值
    private readonly ExplainMode _mode;
    private readonly int _toolResultLimit;  // 工具结果单条截断阈值（AppSettings.AiToolResultLimit，默认 8000）

    public ChatSessionService(ExplainMode mode, string? noteContext = null, string? noteContent = null, int toolResultLimit = 8000)
    {
        _mode = mode;
        _systemPrompt = BuildSystemPrompt(mode, noteContext, noteContent);
        _toolResultLimit = toolResultLimit > 0 ? toolResultLimit : 8000;
        _messages = new List<ChatMessage> { new(ChatRoles.System, _systemPrompt) };

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FocusCapture", "chat_history");
        Directory.CreateDirectory(dir);
        var sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _sessionFile = Path.Combine(dir, $"{sessionId}.json");
    }

    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>会话所属模式（历史会话回看时 UI 用它还原标题）</summary>
    public ExplainMode Mode => _mode;

    /// <summary>向首条 system 消息追加规则文本（Agent 模式防幻觉红线用）</summary>
    public void AppendSystemRules(string rules)
    {
        if (_messages.Count == 0 || _messages[0].Role != ChatRoles.System) return;
        _messages[0] = _messages[0] with { Content = _messages[0].Content + "\n\n" + rules };
    }

    public void AddUser(string content)
    {
        _messages.Add(new ChatMessage(ChatRoles.User, content));
        Trim();
    }

    public void AddAssistant(string content)
    {
        _messages.Add(new ChatMessage(ChatRoles.Assistant, content));
        Trim();
    }

    /// <summary>Agent 循环：assistant 消息携带 tool_calls（原始 JSON 原样保存，回传模型必需）</summary>
    public void AddAssistantToolCall(string? content, string toolCallsJson)
    {
        _messages.Add(new ChatMessage(ChatRoles.Assistant, content ?? "", ToolCallsJson: toolCallsJson));
        Trim();
    }

    /// <summary>Agent 循环：工具执行结果（超长截断，防止单条工具结果撑爆上下文）</summary>
    public void AddToolResult(string toolCallId, string content)
    {
        if (content.Length > _toolResultLimit) content = content[.._toolResultLimit] + "…（已截断）";
        _messages.Add(new ChatMessage(ChatRoles.Tool, content, ToolCallId: toolCallId));
        Trim();
    }

    /// <summary>裁剪：只保留最近 MaxMessages 条非 system 消息；system 永远保留在第一位。
    /// assistant(tool_calls) 与其后紧邻的 tool 结果消息整组同进退，绝不拆散配对。</summary>
    private void Trim()
    {
        var systemCount = 0;
        while (systemCount < _messages.Count && _messages[systemCount].Role == ChatRoles.System)
            systemCount++;

        var nonSystemCount = _messages.Count - systemCount;
        if (nonSystemCount <= MaxMessages) return;

        var removeCount = nonSystemCount - MaxMessages;

        // 截断点若落在配对组内部（或起点是孤儿 tool 消息），整组一并移除
        var cut = systemCount + removeCount;
        while (cut < _messages.Count)
        {
            var m = _messages[cut];
            if (m.Role == ChatRoles.Tool) { cut++; continue; }
            if (m.ToolCallsJson != null)
            {
                var end = cut + 1;
                while (end < _messages.Count && _messages[end].Role == ChatRoles.Tool) end++;
                if (end > systemCount + removeCount) { cut = end; break; } // 组跨越截断点 → 整组丢弃
            }
            break;
        }
        removeCount = cut - systemCount;

        var kept = _messages.Skip(systemCount + removeCount).ToList();
        for (var i = 0; i < kept.Count; i++)
        {
            if (kept[i].Role == ChatRoles.User && kept[i].Content.Length > 2000)
                kept[i] = kept[i] with { Content = kept[i].Content[..2000] };
        }

        var system = _messages.Take(systemCount).ToList();
        _messages.Clear();
        _messages.AddRange(system);
        _messages.AddRange(kept);
    }

    /// <summary>持久化到 chat_history/{sessionId}.json</summary>
    public void Save()
    {
        try
        {
            var payload = new SessionFile
            {
                Mode = _mode.ToString(),
                SystemPrompt = _systemPrompt,
                Messages = _messages.ToList(),
                SavedAt = DateTime.Now,
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_sessionFile, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 对话历史保存失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 读取历史会话。加载后 _sessionFile 保持指向原文件（继续对话 Save 回写原文件，不产生副本），
    /// _systemPrompt 取文件中保存的值（构造函数按 mode 生成的默认值仅是占位）。
    /// </summary>
    public static ChatSessionService? Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var json = File.ReadAllText(filePath, Encoding.UTF8);
            var payload = JsonSerializer.Deserialize<SessionFile>(json);
            if (payload == null) return null;

            if (!Enum.TryParse<ExplainMode>(payload.Mode, out var mode)) return null;
            var svc = new ChatSessionService(mode);
            svc._sessionFile = filePath;
            svc._systemPrompt = payload.SystemPrompt ?? "";
            svc._messages.Clear();
            svc._messages.AddRange(payload.Messages ?? new List<ChatMessage>());
            return svc;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 对话历史读取失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>扫描 chat_history 目录生成会话摘要列表（最新在前）。损坏/无法解析的文件跳过。</summary>
    public static IReadOnlyList<SessionSummary> ListSessions()
    {
        var result = new List<SessionSummary>();
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FocusCapture", "chat_history");
            if (!Directory.Exists(dir)) return result;

            // 文件名即 yyyyMMdd_HHmmss，按名称倒序 = 时间倒序
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderByDescending(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var payload = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(file, Encoding.UTF8));
                    if (payload == null || !Enum.TryParse<ExplainMode>(payload.Mode, out _)) continue;

                    var firstUser = payload.Messages?.FirstOrDefault(m => m.Role == ChatRoles.User)?.Content ?? "";
                    result.Add(new SessionSummary(
                        file,
                        payload.SavedAt,
                        payload.Mode,
                        firstUser.Length > 40 ? firstUser[..40] + "…" : firstUser,
                        payload.Messages?.Count(m => m.Role != ChatRoles.System) ?? 0));
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    // 单个文件损坏不影响整体列表
                    Debug.WriteLine($"[FocusCapture] 会话文件解析跳过: {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 会话目录扫描失败: {ex.Message}");
        }
        return result;
    }

    private static string BuildSystemPrompt(ExplainMode mode, string? noteContext, string? noteContent)
    {
        var basePrompt = mode switch
        {
            ExplainMode.Translate =>
                "你是翻译与释义助手。输入为单词/短语时，输出：词性、释义、常见搭配、例句；输入为段落时，输出整段中文翻译并简要解释。**严禁使用 Markdown 格式**：不要用 **加粗**、# 标题、列表、代码块、分隔线等任何标记；只用普通段落文字回复，必要时换行即可。",
            ExplainMode.Search =>
                "你是笔记解释助手。解释用户笔记中的关键概念、术语或背景。**严禁使用 Markdown 格式**：不要用 **加粗**、# 标题、列表、代码块、分隔线等任何标记；只用普通段落文字回复，必要时换行即可。",
            _ => "你是用户的 AI 助手。用自然、口语化的中文直接回答问题，像和朋友聊天一样。**严禁使用 Markdown 格式**：不要用 **加粗**、# 标题、列表、代码块、分隔线等任何标记；只用普通段落文字回复，必要时换行即可。",
        };

        if (!string.IsNullOrWhiteSpace(noteContext) && !string.IsNullOrWhiteSpace(noteContent))
        {
            basePrompt += $"\n当前笔记内容：{noteContent}";
        }
        return basePrompt;
    }
}

/// <summary>历史会话列表条目摘要（抽屉列表用，FilePath 用于 Load 还原完整会话）</summary>
public sealed record SessionSummary(
    string FilePath,
    DateTime SavedAt,
    string Mode,
    string Preview,
    int MessageCount);

/// <summary>对话历史 JSON 文件结构</summary>
public class SessionFile
{
    public string Mode { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = new();
    public DateTime SavedAt { get; set; }
}
