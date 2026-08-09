using System.Text.Json;
using FocusCapture.Services.AI;

namespace FocusCapture.Services;

/// <summary>AI 对话会话：消息列表 + 裁剪 + 持久化（JSON 文件）</summary>
public class ChatSessionService
{
    private const int MaxMessages = 20; // 保留最近 20 条（约 10 轮）

    private readonly List<ChatMessage> _messages;
    private readonly string _sessionFile;
    private readonly string _systemPrompt;
    private readonly ExplainMode _mode;

    public ChatSessionService(ExplainMode mode, string? noteContext = null, string? noteContent = null)
    {
        _mode = mode;
        _systemPrompt = BuildSystemPrompt(mode, noteContext, noteContent);
        _messages = new List<ChatMessage> { new(ChatRoles.System, _systemPrompt) };

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FocusCapture", "chat_history");
        Directory.CreateDirectory(dir);
        var sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _sessionFile = Path.Combine(dir, $"{sessionId}.json");
    }

    public IReadOnlyList<ChatMessage> Messages => _messages;

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

    /// <summary>裁剪：只保留最近 MaxMessages 条非 system 消息；system 永远保留在第一位</summary>
    private void Trim()
    {
        var system = _messages.TakeWhile(m => m.Role == ChatRoles.System).ToList();

        var nonSystem = _messages.Skip(system.Count).ToList();
        if (nonSystem.Count <= MaxMessages) return;

        // 丢弃最老的（首条优先截断，避免单条超长笔记撑爆上下文）
        var dropped = nonSystem.Count - MaxMessages;
        var kept = nonSystem.Skip(dropped).ToList();
        for (var i = 0; i < kept.Count; i++)
        {
            if (kept[i].Role == ChatRoles.User && kept[i].Content.Length > 2000)
                kept[i] = new ChatMessage(ChatRoles.User, kept[i].Content[..2000]);
        }

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

    /// <summary>读取历史会话（Phase 2 新建即可，读历史留接口）</summary>
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

    private static string BuildSystemPrompt(ExplainMode mode, string? noteContext, string? noteContent)
    {
        var basePrompt = mode switch
        {
            ExplainMode.Translate =>
                "你是翻译与释义助手。输入为单词/短语时，输出：词性、释义、常见搭配、例句；输入为段落时，输出整段中文翻译并简要解释。",
            ExplainMode.Search =>
                "你是笔记解释助手。解释用户笔记中的关键概念、术语或背景。",
            _ => "你是 AI 助手，回答用户的问题。",
        };

        if (!string.IsNullOrWhiteSpace(noteContext) && !string.IsNullOrWhiteSpace(noteContent))
        {
            basePrompt += $"\n当前笔记内容：{noteContent}";
        }
        return basePrompt;
    }
}

/// <summary>对话历史 JSON 文件结构</summary>
public class SessionFile
{
    public string Mode { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = new();
    public DateTime SavedAt { get; set; }
}
