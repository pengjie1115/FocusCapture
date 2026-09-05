using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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

    /// <summary>流式补全：SSE 逐行解析 data: {...}，choices[0].delta.content 逐块产出</summary>
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = BuildRequest(messages, stream: true);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"LLM 流式请求失败: HTTP {(int)response.StatusCode} {response.StatusCode}\n{Truncate(errorBody)}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") yield break;

            var content = TryParseDeltaContent(data);
            if (!string.IsNullOrEmpty(content)) yield return content;
        }
    }

    /// <summary>解析 SSE data 中的 choices[0].delta.content；非 JSON/心跳行返回 null</summary>
    private static string? TryParseDeltaContent(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
            if (delta.TryGetProperty("content", out var contentProp) &&
                contentProp.ValueKind == JsonValueKind.String)
            {
                return contentProp.GetString();
            }
        }
        catch (JsonException)
        {
            // SSE 中偶发的非 JSON 心跳/注释行直接跳过
        }
        return null;
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
