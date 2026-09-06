namespace FocusCapture.Services.AI;

/// <summary>
/// 对话消息。ToolCallId / ToolCallsJson 仅供 Agent 工具循环使用：
/// - ToolCallsJson：assistant 消息携带的 tool_calls 原始 JSON（原样回传模型必需）
/// - ToolCallId：role=tool 的结果消息对应的调用 id
/// 普通问答路径两字段恒为 null，两参构造与历史会话 JSON 完全兼容。
/// </summary>
public sealed record ChatMessage(
    string Role,
    string Content,
    string? ToolCallId = null,
    string? ToolCallsJson = null);

public static class ChatRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

public enum ExplainMode { Translate, Search, Ask }

/// <summary>工具定义（发给模型的 tools 数组条目）。ParametersJson 为 JSON Schema 字符串。</summary>
public sealed record ToolDefinition(string Name, string Description, string ParametersJson);

/// <summary>模型请求的一次工具调用。</summary>
public sealed record ToolCallItem(string Id, string Name, string ArgumentsJson);

/// <summary>ChatWithToolsAsync 的响应：content 与 tool_calls 至少一个非空。</summary>
public sealed record ChatWithToolsResult(string? Content, IReadOnlyList<ToolCallItem> ToolCalls);

/// <summary>带 StatusCode 的 LLM 请求异常，供 Agent 循环识别 4xx 降级。</summary>
public class LlmRequestException : InvalidOperationException
{
    public int StatusCode { get; }
    public LlmRequestException(string message, int statusCode) : base(message) => StatusCode = statusCode;
}
