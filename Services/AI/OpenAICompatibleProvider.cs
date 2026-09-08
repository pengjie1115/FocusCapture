using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace FocusCapture.Services.AI;

/// <summary>
/// OpenAI 兼容 Chat Completions 协议实现（Agnes / Hunyuan 等兼容节点通用）。
/// 请求：POST {BaseUrl}/chat/completions，Bearer 认证，stream=true/false。
/// </summary>
public class OpenAICompatibleProvider : IChatProvider
{
    private static readonly HttpClient HttpClient = new();

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;

    public string Model => _model;
    public string BaseUrl => _baseUrl;
    public string ApiKey => _apiKey;

    public OpenAICompatibleProvider(string baseUrl, string apiKey, string model)
    {
        _baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        _apiKey = apiKey ?? "";
        _model = model ?? "";   // 不再 fallback 到固定模型；请求时由 BuildRequest 校验空值
    }

    /// <summary>非流式补全：解析 choices[0].message.content</summary>
    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        using var request = BuildRequest(messages, stream: false);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"LLM 请求失败: HTTP {(int)response.StatusCode} {response.StatusCode}\n{Truncate(body)}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            return content ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"LLM 响应格式异常\n{Truncate(body)}", ex);
        }
    }

    /// <summary>流式补全：SSE 逐行解析，只取正文增量（思考内容等事件由 StreamChatWithToolsAsync/ReadSseAsync 提供）</summary>
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var ev in StreamChatWithToolsAsync(messages, tools: null, ct).ConfigureAwait(false))
        {
            if (ev is StreamChatEvent.ContentDelta delta)
                yield return delta.Text;
        }
    }

    /// <summary>
    /// 流式补全（可选带工具）：SSE 逐行解析 delta.content / delta.reasoning_content / delta.tool_calls。
    /// tools 为 null 时不带 tools 字段（普通流式问答）；带 tools 时模型请求工具会在流结束时以
    /// ToolCalls 事件产出一次（arguments 已按 index 拼接完整）。
    /// HTTP 非 2xx 抛 LlmRequestException（含 StatusCode，供 Agent 循环识别 4xx 降级）。
    /// </summary>
    public async IAsyncEnumerable<StreamChatEvent> StreamChatWithToolsAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_model))
            throw new InvalidOperationException("未配置模型名称，请在设置 → AI 模型中填写模型名称。");

        var payload = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = true,
            ["messages"] = BuildMessagesArray(messages),
        };
        if (tools != null && tools.Count > 0)
            payload["tools"] = BuildToolsArray(tools);

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions");
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            AppLog.Error("AI", $"流式请求失败: HTTP {(int)response.StatusCode}，模型 {_model}，tools={tools?.Count ?? 0}，响应 {Truncate(errorBody, 300)}");
            throw new LlmRequestException(
                $"LLM 流式请求失败: HTTP {(int)response.StatusCode} {response.StatusCode}\n{Truncate(errorBody)}",
                (int)response.StatusCode);
        }

        await foreach (var ev in ReadSseAsync(response, ct).ConfigureAwait(false))
            yield return ev;
    }

    /// <summary>SSE 核心读取循环：逐行解析 data: {...}，产出正文/思考增量；工具调用片段按 index 增量拼接</summary>
    private static async IAsyncEnumerable<StreamChatEvent> ReadSseAsync(
        System.Net.Http.HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // index → (id, name, arguments 增量缓冲)；流结束时统一产出
        var callsByIndex = new SortedDictionary<int, (string? Id, string Name, StringBuilder Args)>();
        bool sawToolCallField = false;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;

            string? content = null, reasoning = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    continue;
                var choice = choices[0];

                if (choice.TryGetProperty("delta", out var delta))
                {
                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        content = c.GetString();
                    // 思考内容：DeepSeek 系用 reasoning_content，部分节点用 reasoning
                    if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                        reasoning = rc.GetString();
                    else if (delta.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String)
                        reasoning = r.GetString();

                    if (delta.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                    {
                        sawToolCallField = true;
                        foreach (var item in tc.EnumerateArray())
                        {
                            var index = item.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Number
                                ? idx.GetInt32() : callsByIndex.Count;
                            if (!callsByIndex.TryGetValue(index, out var slot))
                            {
                                slot = (null, "", new StringBuilder());
                                callsByIndex[index] = slot;
                            }
                            if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                                slot.Id = idProp.GetString();
                            if (item.TryGetProperty("function", out var fn))
                            {
                                if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(n.GetString()))
                                    slot.Name = n.GetString()!;
                                if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                                    slot.Args.Append(a.GetString());
                            }
                            callsByIndex[index] = slot;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // SSE 中偶发的非 JSON 心跳/注释行直接跳过
                continue;
            }

            if (!string.IsNullOrEmpty(reasoning)) yield return new StreamChatEvent.ReasoningDelta(reasoning);
            if (!string.IsNullOrEmpty(content)) yield return new StreamChatEvent.ContentDelta(content);
        }

        if (sawToolCallField && callsByIndex.Count > 0)
        {
            var calls = callsByIndex.Select(kv => new ToolCallItem(
                kv.Value.Id ?? $"call_{kv.Key}",
                kv.Value.Name,
                kv.Value.Args.Length == 0 ? "{}" : kv.Value.Args.ToString())).ToList();
            yield return new StreamChatEvent.ToolCalls(calls);
        }
    }

    /// <summary>连接测试：最小请求（max_tokens=1），HTTP 2xx 且能解析出 content 即成功</summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        var messages = new[] { new ChatMessage(ChatRoles.User, "hi") };
        using var request = BuildRequest(messages, stream: false, maxTokens: 1);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"连接测试失败: HTTP {(int)response.StatusCode} {response.StatusCode}\n{Truncate(body)}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content");
            return content.ValueKind == JsonValueKind.String;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"连接测试失败: 响应格式异常\n{Truncate(body)}", ex);
        }
    }

    /// <summary>
    /// 带工具的补全（Agent 循环用，非流式）。
    /// assistant 消息可携带 ToolCallsJson（tool_calls 原样回传），role=tool 消息带 tool_call_id。
    /// HTTP 非 2xx 抛 LlmRequestException（含 StatusCode，供调用方识别 4xx 降级）。
    /// </summary>
    public async Task<ChatWithToolsResult> ChatWithToolsAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_model))
            throw new InvalidOperationException("未配置模型名称，请在设置 → AI 模型中填写模型名称。");

        var payload = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = false,
            ["messages"] = BuildMessagesArray(messages),
            ["tools"] = BuildToolsArray(tools)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions");
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Error("AI", $"ChatWithTools 请求失败: HTTP {(int)response.StatusCode}，模型 {_model}，响应 {Truncate(body, 300)}");
            throw new LlmRequestException(
                $"LLM 请求失败: HTTP {(int)response.StatusCode} {response.StatusCode}\n{Truncate(body)}",
                (int)response.StatusCode);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");

            string? content = null;
            if (message.TryGetProperty("content", out var contentProp) &&
                contentProp.ValueKind == JsonValueKind.String)
                content = contentProp.GetString();

            var toolCalls = new List<ToolCallItem>();
            if (message.TryGetProperty("tool_calls", out var callsProp) && callsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in callsProp.EnumerateArray())
                {
                    var function = call.GetProperty("function");
                    toolCalls.Add(new ToolCallItem(
                        call.GetProperty("id").GetString() ?? "",
                        function.GetProperty("name").GetString() ?? "",
                        function.TryGetProperty("arguments", out var args) ? args.GetString() ?? "{}" : "{}"));
                }
            }

            return new ChatWithToolsResult(content, toolCalls);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            AppLog.Error("AI", $"ChatWithTools 响应格式异常: {Truncate(body, 300)}");
            throw new InvalidOperationException($"LLM 响应格式异常\n{Truncate(body)}", ex);
        }
    }

    /// <summary>tools 数组序列化（流式/非流式带工具请求共用）</summary>
    private static JsonArray BuildToolsArray(IReadOnlyList<ToolDefinition> tools)
    {
        return new JsonArray(tools.Select(t => new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = JsonNode.Parse(string.IsNullOrWhiteSpace(t.ParametersJson) ? "{}" : t.ParametersJson)
            }
        }).ToArray<JsonNode?>());
    }

    /// <summary>Agent 消息序列化：普通消息 {role,content}；assistant 带 tool_calls；tool 消息带 tool_call_id</summary>
    private static JsonArray BuildMessagesArray(IReadOnlyList<ChatMessage> messages)    {
        var array = new JsonArray();
        foreach (var m in messages)
        {
            var node = new JsonObject { ["role"] = m.Role };
            if (m.Role == ChatRoles.Assistant && !string.IsNullOrEmpty(m.ToolCallsJson))
            {
                node["content"] = m.Content ?? "";
                node["tool_calls"] = JsonNode.Parse(m.ToolCallsJson) ?? new JsonArray();
            }
            else if (m.Role == ChatRoles.Tool)
            {
                node["content"] = m.Content ?? "";
                node["tool_call_id"] = m.ToolCallId ?? "";
            }
            else
            {
                node["content"] = m.Content ?? "";
            }
            array.Add(node);
        }
        return array;
    }

    private HttpRequestMessage BuildRequest(IReadOnlyList<ChatMessage> messages, bool stream, int? maxTokens = null)
    {
        if (string.IsNullOrWhiteSpace(_model))
            throw new InvalidOperationException("未配置模型名称，请在设置 → AI 模型中填写模型名称。");

        var payload = new
        {
            model = _model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            stream,
            max_tokens = maxTokens
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions");
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        return request;
    }

    /// <summary>截断响应体，避免异常消息过长</summary>
    private static string Truncate(string text, int maxLength = 200)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "…";
    }
}
