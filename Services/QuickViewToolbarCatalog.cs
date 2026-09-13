namespace FocusCapture.Services;

/// <summary>
/// 灵感速览标题栏「可组装工具栏」的功能目录与布局预算（v3.9）。
/// 纯数据 + 纯逻辑：不得引用任何 WPF 类型 —— tests/FocusCapture.Tests.csproj 按文件直链编译本类，
/// 引入依赖会破坏快层测试的秒级编译约定（见该 csproj 注释）。
/// 按钮的构建/样式/事件绑定在 QuickViewWindow.xaml.cs（CreateToolbarButton / RebuildToolbar）。
/// </summary>
public static class QuickViewToolbarCatalog
{
    /// <summary>一个可放进标题栏的功能按钮定义。EstimatedWidth 含按钮间距，用于设置页的像素预算。</summary>
    public sealed record FuncDef(
        string Id,
        string Label,
        string ToolTip,
        double EstimatedWidth,
        bool IsTextButton,      // true = 宽度随内容自适应（时间筛选/AI 问答/导出文案是动态的）；false = 固定 28px 图标钮
        double FontSize,
        string Foreground,      // hex，如 "#4CAF50"
        string BorderBrush);

    /// <summary>八个可配置功能（id 稳定不变，settings.json 按此存储）。</summary>
    public static readonly IReadOnlyList<FuncDef> All = new FuncDef[]
    {
        new("Calendar",     "📅 今天", "选择时间范围：预设周期或自定义区间", 74, true,  11, "#CCCCCC", "#555555"),
        new("SyncUpload",   "↑",       "上传笔记到云端（沿用全量同步机制）", 34, false, 14, "#4CAF50", "#4CAF50"),
        new("SyncDownload", "↓",       "从云端拉取笔记到本地（仅拉不推）",   34, false, 14, "#4CAF50", "#4CAF50"),
        new("Search",       "🔍",      "全局查找（所有笔记）",               34, false, 12, "#CCCCCC", "#555555"),
        new("Refresh",      "⟳",       "刷新",                               34, false, 14, "#CCCCCC", "#555555"),
        new("AiAsk",        "AI 问答", "",                                   78, true,  11, "#4CAF50", "#4CAF50"), // 文案随 AiAssistantName 动态
        new("Export",       "▼ 导出",  "导出当前/已选笔记",                  74, true,  11, "#888888", "#555555"),
        new("GetNote",      "存",      "存到得到大脑（勾选条目优先；未勾选推送当前列表）", 34, false, 12, "#2196F3", "#2196F3"),
    };

    public static FuncDef? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(f => f.Id == id);

    /// <summary>升级默认布局 = 重构前的按钮集合（老用户 settings.json 无此字段时反序列化即得，无感迁移）。</summary>
    public static readonly IReadOnlyList<string> DefaultLeft = new[] { "Calendar", "SyncUpload", "SyncDownload" };
    public static readonly IReadOnlyList<string> DefaultRight = new[] { "Search", "Refresh", "AiAsk", "Export", "GetNote" };

    /// <summary>
    /// 清洗用户配置：未知 id 过滤、去重、保持顺序；整列无效/为空回退默认（防手改 settings.json 把工具栏清空后找不到入口）。
    /// </summary>
    public static (List<string> left, List<string> right) Sanitize(List<string>? left, List<string>? right)
    {
        return (CleanSide(left, DefaultLeft), CleanSide(right, DefaultRight));
    }

    private static List<string> CleanSide(List<string>? ids, IReadOnlyList<string> fallback)
    {
        if (ids == null || ids.Count == 0) return fallback.ToList();
        var result = new List<string>();
        foreach (var id in ids)
        {
            if (Find(id) == null) continue;      // 未知 id 丢弃
            if (result.Contains(id, StringComparer.Ordinal)) continue;   // 去重
            result.Add(id);
        }
        return result.Count == 0 ? fallback.ToList() : result;
    }

    // ── 标题栏像素预算 ──
    // 左右两组按钮共享标题行同一行空间，约束按像素而非按个数（图标钮 34px vs 文字钮 ~78px，差一倍多）。

    /// <summary>标题行固定开销：左右边距 28 + 标题文字 ~62 + 铬区（最小化/最大化/关闭 + 分隔线）~90。</summary>
    public const double FixedTitleBarOverhead = 180;

    /// <summary>面板宽度下，标题栏按钮区可用像素。</summary>
    public static double AvailableBudget(double panelWidth) => Math.Max(0, panelWidth - FixedTitleBarOverhead);

    /// <summary>当前左右配置实际占用的估算像素（AiAsk 按默认名「AI 问答」估，自定义更长名称时实际略宽）。</summary>
    public static double UsedBudget(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        double Sum(IEnumerable<string?> ids) => ids
            .Select(Find)
            .Where(f => f != null)
            .Sum(f => f!.EstimatedWidth);
        return Sum(left) + Sum(right);
    }

    /// <summary>在预算内还能否放下指定功能（设置页「添加」的准入判断）。</summary>
    public static bool CanAdd(double panelWidth, IReadOnlyList<string> left, IReadOnlyList<string> right, string idToAdd)
    {
        var def = Find(idToAdd);
        if (def == null) return false;
        return UsedBudget(left, right) + def.EstimatedWidth <= AvailableBudget(panelWidth) + 0.01;
    }
}
