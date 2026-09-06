using System.Threading;
using FocusCapture.Services.AI;

namespace FocusCapture.Services.Agent;

/// <summary>
/// function calling 主循环：发消息（带 tools）→ 执行 tool_calls（写类先过确认闸）→ 结果回填 → 再发。
/// 最多 5 轮工具往返；带 tools 请求 4xx 时自动去 tools 降级为普通问答一次。
/// </summary>
public class AgentRunService
{
    private const int MaxToolRounds = 5;

    private readonly OpenAICompatibleProvider _provider;
    private readonly AgentToolRegistry _registry;
    private readonly ChatSessionService _session;

    /// <summary>确认闸回调（UI 弹窗实现）：返回 true = 用户确认执行。null 时非只读工具直接拒绝执行。</summary>
    public Func<string, Task<bool>>? ConfirmHandler { get; set; }

    /// <summary>状态回调（如"正在调用工具: xxx"），UI 用于更新气泡提示</summary>
    public Action<string>? StatusCallback { get; set; }

    public AgentRunService(OpenAICompatibleProvider provider, AgentToolRegistry registry, ChatSessionService session)
    {
        _provider = provider;
        _registry = registry;
        _session = session;
    }

    /// <summary>处理一条用户消息，返回最终答复文本（同时写入会话历史）</summary>
    public async Task<string> RunAsync(string userMessage, CancellationToken ct = default)
    {
        _session.AddUser(userMessage);

        for (var round = 0; round < MaxToolRounds; round++)
        {
            ChatWithToolsResult result;
            try
            {
                result = await _provider.ChatWithToolsAsync(_session.Messages, _registry.GetDefinitions(), ct);
            }
            catch (LlmRequestException ex) when (ex.StatusCode >= 400 && ex.StatusCode < 500)
            {
                return await FallbackPlainChatAsync("当前模型不支持工具调用，已切换普通问答模式。", ct);
            }

            if (result.ToolCalls.Count == 0)
            {
                var final = result.Content ?? "";
                _session.AddAssistant(final);
                return final;
            }

            // assistant 消息（含 tool_calls 原始 JSON，回传模型必需）
            var toolCallsJson = JsonSerializer.Serialize(result.ToolCalls.Select(tc => new
            {
                id = tc.Id,
                type = "function",
                function = new { name = tc.Name, arguments = tc.ArgumentsJson },
            }));
            _session.AddAssistantToolCall(result.Content, toolCallsJson);

            foreach (var call in result.ToolCalls)
            {
                StatusCallback?.Invoke($"正在调用工具: {call.Name}");
                var toolResult = await ExecuteToolWithGateAsync(call, ct);
                _session.AddToolResult(call.Id, toolResult);
            }
        }

        return await FallbackPlainChatAsync("（本轮工具调用已达上限）请基于以上工具结果直接给出最终回答。", ct);
    }

    private async Task<string> ExecuteToolWithGateAsync(ToolCallItem call, CancellationToken ct)
    {
        if (!_registry.TryGetTool(call.Name, out var tool))
            return $"错误：不存在名为「{call.Name}」的工具。可用工具：{_registry.DescribeAvailable()}";

        if (!tool.IsReadOnly)
        {
            if (ConfirmHandler == null)
                return "错误：该操作需要用户确认，但当前环境不支持确认交互，已取消。";
            var confirmed = await ConfirmHandler(tool.DescribeAction(call.ArgumentsJson));
            if (!confirmed)
                return "用户已取消该操作。请停止此动作，不要重复尝试。";
        }

        try
        {
            return await tool.ExecuteAsync(call.ArgumentsJson, ct);
        }
        catch (Exception ex)
        {
            return $"工具 {call.Name} 执行出错：{ex.Message}";
        }
    }

    /// <summary>降级/收尾：去掉 tools 与 tool 消息后普通补全一次（配对消息只存在于本回合上下文，过滤不影响历史）</summary>
    private async Task<string> FallbackPlainChatAsync(string notice, CancellationToken ct)
    {
        var plainMessages = _session.Messages
            .Where(m => m.Role != ChatRoles.Tool && m.ToolCallsJson == null)
            .ToList();

        string content;
        try
        {
            content = await _provider.CompleteAsync(plainMessages, ct);
        }
        catch (Exception ex)
        {
            content = $"（模型请求失败：{ex.Message}）";
        }

        var full = notice + "\n" + content;
        _session.AddAssistant(full);
        return full;
    }
}
