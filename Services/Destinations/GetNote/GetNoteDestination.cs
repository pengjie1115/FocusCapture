using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using FocusCapture.Models;
using FocusCapture.Services.Agent;

namespace FocusCapture.Services.Destinations;

/// <summary>
/// 得到大脑（Get笔记）开放平台适配器：首个 IOutboundDestination 实现。
/// 能力目录见 AGENT_IMPLEMENTATION.md 3.3；新增能力 = Capabilities 加一条 + ExecuteAsync 加一个 case。
/// 鉴权：header Authorization + X-Client-ID（实测自 WorkBuddy getnote-sync 脚本）。
/// recall / note/detail 的响应结构未经本机实测，采用原样透传 data（截断）策略，实测有出入只改本文件。
/// </summary>
public class GetNoteDestination : IOutboundDestination
{
    private const string BaseUrl = "https://openapi.biji.com/open/api/v1";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly AppSettings _settings;

    public GetNoteDestination(AppSettings settings) => _settings = settings;

    public string Name => "得到大脑";

    public IReadOnlyList<OutboundCapability> Capabilities { get; } = new List<OutboundCapability>
    {
        new("getnote_save_note",
            "把一篇整理好的笔记保存到得到大脑（云端知识库）。标题与正文由你按用户对话约定组织，" +
            "整理/合并/跳过了哪些来源内容必须在答复中明示。需要用户确认。凭证未配置时不可用。",
            """{"type":"object","properties":{"title":{"type":"string","description":"笔记标题"},"content":{"type":"string","description":"正文，Markdown 纯文本"},"tags":{"type":"array","items":{"type":"string"},"description":"可选，标签列表"},"topic_id":{"type":"string","description":"可选，知识库 id（8 位字母数字）；不填则用账号默认"}},"required":["title","content"]}""",
            IsReadOnly: false),
        new("getnote_search",
            "语义搜索得到大脑云端已有笔记（向量召回，非关键词硬匹配）。" +
            "仅当用户明确提到\"得到大脑/云上/云端/以前存到云端的笔记\"时才调用。" +
            "注意：云端笔记内容会发送给模型服务方。返回条目含 id，读取全文须用本工具返回的 id。",
            """{"type":"object","properties":{"query":{"type":"string","description":"搜索内容描述"}},"required":["query"]}""",
            IsReadOnly: true),
        new("getnote_read_note",
            "读取得到大脑云端单篇笔记全文。id 必须来自 getnote_search 的返回结果，禁止编造。全文超长时截断。",
            """{"type":"object","properties":{"id":{"type":"string","description":"getnote_search 返回的笔记 id"}},"required":["id"]}""",
            IsReadOnly: true),
        new("getnote_list_notebooks",
            "列出得到大脑账号下的知识库（名称与 topic_id）。用户问\"我有哪些知识库\"或推送前需要确定 topic_id 时使用。",
            """{"type":"object","properties":{"page":{"type":"integer","description":"页码，默认 1"}}}""",
            IsReadOnly: true),
    };

    public async Task<OutboundResult> ExecuteAsync(string capabilityName, string argumentsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.GetNoteApiKey) || string.IsNullOrWhiteSpace(_settings.GetNoteClientId))
            return new OutboundResult(false, "未配置得到大脑凭证（API Key / Client ID），无法执行。请在得到大脑开放平台创建应用获取凭证并配置到应用设置。");

        try
        {
            return capabilityName switch
            {
                "getnote_save_note" => await SaveNoteAsync(argumentsJson, ct),
                "getnote_search" => await SearchAsync(argumentsJson, ct),
                "getnote_read_note" => await ReadNoteAsync(argumentsJson, ct),
                "getnote_list_notebooks" => await ListNotebooksAsync(argumentsJson, ct),
                _ => new OutboundResult(false, $"未知能力: {capabilityName}"),
            };
        }
        catch (HttpRequestException ex)
        {
            return new OutboundResult(false, $"网络请求失败：{ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return new OutboundResult(false, "请求超时（30 秒），请稍后重试。");
        }
    }

    // ── 各能力实现 ──

    private async Task<OutboundResult> SaveNoteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "title", out var title))
            return new OutboundResult(false, "缺少参数 title。");
        if (!ToolArgs.TryGetString(argumentsJson, "content", out var content))
            return new OutboundResult(false, "缺少参数 content。");

        var body = new JsonObject
        {
            ["note_type"] = "plain_text",
            ["title"] = title,
            ["content"] = content,
        };
        ToolArgs.TryGetString(argumentsJson, "topic_id", out var topicId);
        if (!string.IsNullOrWhiteSpace(topicId)) body["topic_id"] = topicId;

        var tags = ExtractTags(argumentsJson);
        if (tags.Count > 0) body["tags"] = new JsonArray(tags.Select(t => (JsonNode?)t).ToArray());

        var json = await SendAsync(HttpMethod.Post, "/resource/note/save", body, ct);
        if (!json.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
            return new OutboundResult(false, DescribeApiError(json));

        var data = json.TryGetProperty("data", out var d) ? d : default;
        var noteId = data.ValueKind == JsonValueKind.Object
            ? (data.TryGetProperty("id", out var id) ? id.ToString() : "")
            : "";
        return new OutboundResult(true, $"已保存到得到大脑（note_id={noteId}，标题《{title}》）。", noteId);
    }

    private async Task<OutboundResult> SearchAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "query", out var query))
            return new OutboundResult(false, "缺少参数 query。");

        var body = new JsonObject { ["query"] = query };
        var json = await SendAsync(HttpMethod.Post, "/resource/recall", body, ct);
        if (!json.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
            return new OutboundResult(false, DescribeApiError(json));

        var data = json.TryGetProperty("data", out var d) ? d.GetRawText() : "{}";
        return new OutboundResult(true, "搜索结果（JSON 原样，note id 用于 getnote_read_note）：\n" + Truncate(data, 1500));
    }

    private async Task<OutboundResult> ReadNoteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "id", out var id))
            return new OutboundResult(false, "缺少参数 id（必须来自 getnote_search 返回）。");

        var url = $"/resource/note/detail?id={Uri.EscapeDataString(id)}";
        var json = await SendAsync(HttpMethod.Get, url, null, ct);
        if (!json.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
            return new OutboundResult(false, DescribeApiError(json));

        var data = json.TryGetProperty("data", out var d) ? d.GetRawText() : "{}";
        return new OutboundResult(true, "笔记详情（JSON 原样）：\n" + Truncate(data, 2000));
    }

    private async Task<OutboundResult> ListNotebooksAsync(string argumentsJson, CancellationToken ct)
    {
        var page = "1";
        if (ToolArgs.TryGetInt(argumentsJson, "page", out var parsed) && parsed > 0)
            page = parsed.ToString();

        var json = await SendAsync(HttpMethod.Get, $"/resource/knowledge/list?page={page}", null, ct);
        if (!json.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
            return new OutboundResult(false, DescribeApiError(json));

        if (json.TryGetProperty("data", out var data) && data.TryGetProperty("topics", out var topics) &&
            topics.ValueKind == JsonValueKind.Array)
        {
            var topicList = ParseTopics(data);
            var lines = topicList.Select(t => $"- {t.Name} (topic_id={t.Id})");
            return new OutboundResult(true, "知识库列表：\n" + string.Join("\n", lines));        }
        return new OutboundResult(true, "知识库列表（JSON 原样）：\n" + Truncate(json.GetProperty("data").GetRawText(), 1000));
    }

    /// <summary>知识库条目（名称 + topic_id），按钮路径默认知识库与设置面板测试连接共用</summary>
    public sealed record GetNoteTopic(string Name, string Id);

    /// <summary>
    /// 设置面板「测试连接」：验证凭证 + 拉取知识库列表（兼测读权限）。
    /// 与对话工具 list_notebooks 走同一接口；不写测试笔记，避免脏数据。
    /// </summary>
    public async Task<(bool Ok, string Message, List<GetNoteTopic> Topics)> TestConnectionAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.GetNoteApiKey) || string.IsNullOrWhiteSpace(_settings.GetNoteClientId))
            return (false, "请先填写 API Key 和 Client ID。", new List<GetNoteTopic>());
        try
        {
            var json = await SendAsync(HttpMethod.Get, "/resource/knowledge/list?page=1", null, ct);
            if (!json.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
                return (false, DescribeApiError(json), new List<GetNoteTopic>());
            var topics = json.TryGetProperty("data", out var data) ? ParseTopics(data) : new List<GetNoteTopic>();
            return (true, $"连接成功，已加载 {topics.Count} 个知识库。", topics);
        }
        catch (HttpRequestException ex)
        {
            return (false, $"网络请求失败：{ex.Message}", new List<GetNoteTopic>());
        }
        catch (TaskCanceledException)
        {
            return (false, "请求超时（30 秒），请稍后重试。", new List<GetNoteTopic>());
        }
    }

    /// <summary>解析 knowledge/list 的 topics 数组；topic_id/id 兼容数字与字符串</summary>
    private static List<GetNoteTopic> ParseTopics(JsonElement data)
    {
        var topics = new List<GetNoteTopic>();
        if (data.TryGetProperty("topics", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in arr.EnumerateArray())
            {
                var name = t.TryGetProperty("name", out var n) ? n.GetString() : "";
                string? tid = null;
                if (t.TryGetProperty("topic_id", out var tidEl)) tid = tidEl.ToString();
                else if (t.TryGetProperty("id", out var idEl)) tid = idEl.ToString();
                if (!string.IsNullOrWhiteSpace(tid))
                    topics.Add(new GetNoteTopic(name ?? "(未命名)", tid));
            }
        }
        return topics;
    }

    // ── HTTP 与解析 ──

    private async Task<JsonElement> SendAsync(HttpMethod method, string url, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + url);
        request.Headers.Authorization = new AuthenticationHeaderValue(_settings.GetNoteApiKey);
        request.Headers.Add("X-Client-ID", _settings.GetNoteClientId);
        if (body != null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Truncate(text, 300)}");

        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new HttpRequestException($"响应不是合法 JSON: {Truncate(text, 300)}");
        }
    }

    /// <summary>官方响应信封 {success, error:{...}}，error 文案透出（含限流 10202 等）</summary>
    private static string DescribeApiError(JsonElement json)
    {
        if (json.TryGetProperty("error", out var err))
        {
            if (err.ValueKind == JsonValueKind.String) return err.GetString() ?? "未知错误";
            if (err.ValueKind == JsonValueKind.Object)
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                var code = err.TryGetProperty("code", out var c) ? c.ToString() : null;
                var hint = code == "10202" ? "（疑似触发限流，请稍后分批重试）" : "";
                return $"API 错误 {code}: {msg}{hint}";
            }
        }
        return "API 返回失败（未提供错误详情）：" + Truncate(json.GetRawText(), 200);
    }

    private static List<string> ExtractTags(string argumentsJson)
    {
        var tags = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("tags", out var arr) && arr.ValueKind == JsonValueKind.Array)
                tags.AddRange(arr.EnumerateArray()
                    .Where(t => t.ValueKind == JsonValueKind.String)
                    .Select(t => t.GetString() ?? "")
                    .Where(t => t.Length > 0));
        }
        catch (JsonException) { }
        return tags;
    }

    private static string Truncate(string text, int maxLength)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "…（已截断）";
    }
}
