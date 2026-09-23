using FocusCapture.Models;

namespace FocusCapture.Services.AI;

/// <summary>
/// 解析出来的「一个可用模型端点」—— 构造 <see cref="IChatProvider"/> 所需的全部信息。
/// 这是多供应商改造后所有调用方唯一应该依赖的形状：
/// 调用方不需要知道配置里存了几家供应商、当前选的是哪个。
/// </summary>
public sealed record ResolvedAiModel(
    string ProviderId,
    string ProviderName,
    string BaseUrl,
    string ApiKey,
    string ModelId,
    string ModelDisplayName,
    int MaxOutputTokens,
    int ContextWindow)
{
    /// <summary>稳定键，格式与 <see cref="AppSettings.ActiveModelKey"/> 一致（<c>providerId/modelId</c>）。</summary>
    public string Key => ProviderId + "/" + ModelId;

    /// <summary>界面展示用标签：「供应商 · 模型显示名」；供应商名为空时只给模型名。</summary>
    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(ProviderName) ? ModelDisplayName : ProviderName + " · " + ModelDisplayName;
}

/// <summary>
/// AI 模型配置的统一解析入口（2026-09-23 多供应商改造）。
///
/// <para><b>为什么要它</b>：改造前有 4 处代码各自从设置里拼 BaseUrl + Key + 模型名。
/// 换成多供应商后若继续各自拼，AI 问答侧将来加「切换模型」就得改 4 个地方。
/// 现在全部走这里 —— 切换只需改一个 <see cref="AppSettings.ActiveModelKey"/>，其余代码零改动。</para>
///
/// <para><b>契约：解析不到返回 null，永不抛</b>（沿用本项目通则：本地状态推进不依赖外部成败，
/// 配置读坏不该让调用方崩在无法处理的异常上）。</para>
/// </summary>
public static class AiModelResolver
{
    /// <summary>「旧扁平配置」的伪供应商 Id —— 迁移失败或尚未迁移时，兜底路径给出的 Id。</summary>
    public const string LegacyProviderId = "__legacy__";

    /// <summary>按 <see cref="AppSettings.ActiveModelKey"/> 解析当前使用的模型。</summary>
    public static ResolvedAiModel? ResolveActive(AppSettings s) => Resolve(s, s.ActiveModelKey);

    /// <summary>
    /// 解析指定键对应的模型。三级回退，越往后越「保住能用」：
    /// <list type="number">
    /// <item>按 <paramref name="key"/> 精确命中供应商 + 模型</item>
    /// <item>键失效（用户删了供应商/模型）→ 退用「第一个可用模型」，不让一个过期键把整个 AI 锁死</item>
    /// <item>供应商列表为空（迁移失败或尚未迁移）→ 读旧扁平字段兜底，保证老用户升级后一定还能用</item>
    /// </list>
    /// </summary>
    public static ResolvedAiModel? Resolve(AppSettings s, string? key)
    {
        try
        {
            if (s == null) return null;

            if (!string.IsNullOrWhiteSpace(key))
            {
                var slash = key.IndexOf('/');
                if (slash > 0 && slash < key.Length - 1)
                {
                    var providerId = key[..slash];
                    var modelId = key[(slash + 1)..];
                    foreach (var p in s.AiModelProviders)
                    {
                        if (!string.Equals(p.Id, providerId, StringComparison.Ordinal)) continue;
                        foreach (var m in p.Models)
                        {
                            if (!string.Equals(m.Id, modelId, StringComparison.Ordinal)) continue;
                            var hit = Build(p, m);
                            if (hit != null) return hit;
                        }
                    }
                }
            }

            // 回退 2：第一个「配置完整」的模型。
            // 跳过配置不全的条目而不是直接放弃 —— 一个空 BaseUrl 的脏数据不该把后面好的挡住。
            foreach (var p in s.AiModelProviders)
            {
                foreach (var m in p.Models)
                {
                    var hit = Build(p, m);
                    if (hit != null) return hit;
                }
            }

            // 回退 3：旧扁平字段。这就是「迁移保命回退」的落地点。
            return ResolveLegacy(s);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>是否已配置到「能发请求」的程度 —— 供界面给「未配置」提示用，替代过去直接判某个字段是否为空。</summary>
    public static bool IsConfigured(AppSettings s) => ResolveActive(s) != null;

    /// <summary>
    /// 按当前配置造一个 provider —— <b>配置 → provider 的唯一映射点</b>。
    ///
    /// <para>解析不出来时<b>退回旧扁平字段</b>：结果与改造前完全一致（provider 造得出来，
    /// 但一用就报「未配置模型名称」）。这么做的目的是不让每个调用方到处判 null ——
    /// 改造前 <c>_aiProvider</c> 恰好是非空的，改成可空会在多处引入行为变化。</para>
    /// </summary>
    public static OpenAICompatibleProvider CreateProvider(AppSettings s)
    {
        var m = ResolveActive(s);
        return m != null
            ? new OpenAICompatibleProvider(m.BaseUrl, m.ApiKey, m.ModelId, m.MaxOutputTokens)
            : new OpenAICompatibleProvider(s.AiBaseUrl, s.AiApiKey, s.AiModel, s.AiMaxTokens);
    }

    private static ResolvedAiModel? ResolveLegacy(AppSettings s)
    {
        var baseUrl = (s.AiBaseUrl ?? "").Trim();
        var model = (s.AiModel ?? "").Trim();
        if (baseUrl.Length == 0 || model.Length == 0) return null;

        return new ResolvedAiModel(
            LegacyProviderId,
            AiProviders.MatchByUrl(baseUrl)?.Name ?? AiProviders.Custom,
            baseUrl,
            s.AiApiKey ?? "",
            model,
            model,
            s.AiMaxTokens,   // ≤0 由下游按「不传 max_tokens」处理
            0);              // 旧配置没有窗口概念 → 0 = 不裁剪，与改造前行为一致
    }

    /// <summary>把一条供应商 + 一个模型拼成解析结果；配置不完整（缺地址或缺模型 ID）返回 null。</summary>
    private static ResolvedAiModel? Build(AiProviderEntry p, AiModelEntry m)
    {
        if (p == null || m == null) return null;
        var baseUrl = (p.BaseUrl ?? "").Trim();
        if (baseUrl.Length == 0) return null;
        if (string.IsNullOrWhiteSpace(m.Id)) return null;

        return new ResolvedAiModel(
            p.Id ?? "",
            p.Name ?? "",
            baseUrl,
            p.ApiKey ?? "",
            m.Id,
            string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName,
            m.MaxOutputTokens,
            m.ContextWindow);
    }
}
