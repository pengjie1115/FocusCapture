using FocusCapture.Models;

namespace FocusCapture.Services.AI;

/// <summary>
/// 「AI 整理」预置规则目录 + 用户规则列表的解析（2026-09-27 规则化改造）。
///
/// <para><b>为什么预置指令放在代码而不是 settings</b>：升级内置规则的提示词（改措辞、修约束）
/// 不需要给老用户写数据迁移 —— settings 里只存 Id / 名称 / 显隐 / 顺序，指令按 Id 从这里取。</para>
///
/// <para><b>纯静态、零 WPF 依赖</b>：预览窗 / 流程 / 设置页都从这里取规则，一处定义三处同源。</para>
/// </summary>
public static class TidyRuleCatalog
{
    public const string TidyId = "builtin-tidy";        // 理顺条理（默认规则）
    public const string SummaryId = "builtin-summary";  // 总结摘要
    public const string TodoId = "builtin-extract-todo"; // 提取待办
    public const string ExplainId = "builtin-explain";  // 解释扩展

    /// <summary>预置规则定义（顺序 = 默认底栏顺序）。指令正文统一住在 <see cref="NoteTidyPrompt"/>（与快层检查点同源）。</summary>
    public static IReadOnlyList<BuiltinDef> Builtins { get; } = new[]
    {
        new BuiltinDef(TidyId, "理顺条理", TidyRuleKind.Rewrite, NoteTidyPrompt.TidyInstruction),
        new BuiltinDef(SummaryId, "总结摘要", TidyRuleKind.Rewrite, NoteTidyPrompt.SummaryInstruction),
        new BuiltinDef(TodoId, "提取待办", TidyRuleKind.ExtractTodo, NoteTidyPrompt.ExtractTodoInstruction),
        new BuiltinDef(ExplainId, "解释扩展", TidyRuleKind.Rewrite, NoteTidyPrompt.ExplainInstruction),
    };

    /// <summary>预置规则的定义行（Id → 名称 / 分档 / 指令）。</summary>
    public sealed record BuiltinDef(string Id, string Name, TidyRuleKind Kind, string Instruction);

    public static BuiltinDef? Find(string? id) => Builtins.FirstOrDefault(b => b.Id == id);

    /// <summary>出厂规则列表（全可见，顺序同目录）—— AppSettings 初始化与列表被清空时的兜底。</summary>
    public static List<TidyRule> DefaultList() =>
        Builtins.Select(b => new TidyRule { Id = b.Id, Name = b.Name, IsBuiltin = true, Visible = true }).ToList();

    /// <summary>
    /// 补齐用户列表里缺的预置规则（升级版号新增了内置规则时，老 settings 里没有它 —— 追加到尾部，
    /// 顺序用户可自行调）。<b>幂等</b>；顺带兜底空列表。由 <see cref="AppSettings.Load"/> 在载入后调用。
    /// </summary>
    public static void Normalize(List<TidyRule>? rules)
    {
        if (rules == null) return;
        if (rules.Count == 0)
        {
            rules.AddRange(DefaultList());
            return;
        }
        foreach (var b in Builtins)
            if (!rules.Any(r => r.Id == b.Id))
                rules.Add(new TidyRule { Id = b.Id, Name = b.Name, IsBuiltin = true, Visible = true });
    }

    /// <summary>
    /// 底栏要显示的规则：设置列表里 Visible 的那些，保序。
    /// 列表空（未走 Load 的裸 settings）→ 回落出厂列表，规则条不至于空白。
    /// </summary>
    public static List<TidyRule> ResolveVisible(AppSettings? s)
    {
        var rules = s?.TidyRules;
        if (rules == null || rules.Count == 0) return DefaultList();
        return rules.Where(r => r.Visible).ToList();
    }

    /// <summary>
    /// 默认规则：按 <see cref="AppSettings.TidyDefaultRuleId"/> 找；找不到（被删 / 键坏）→ 回落第一
    /// 条预置（理顺条理），**与可见性无关**（用户拍板：两边各管各的，全隐藏也不影响默认规则运行）。
    /// </summary>
    public static TidyRule ResolveDefaultRule(AppSettings? s)
    {
        var rules = s?.TidyRules;
        if (rules != null && rules.Count > 0)
        {
            var hit = rules.FirstOrDefault(r => r.Id == s!.TidyDefaultRuleId);
            if (hit != null) return hit;
        }
        return rules?.FirstOrDefault(r => r.IsBuiltin && r.Id == TidyId)
               ?? rules?.FirstOrDefault(r => r.IsBuiltin)
               ?? rules?.FirstOrDefault()
               ?? DefaultList()[0];
    }

    /// <summary>规则的结果分档：预置按目录；自定义一律 Rewrite（文本流）。</summary>
    public static TidyRuleKind KindOf(TidyRule rule)
        => rule.IsBuiltin ? Find(rule.Id)?.Kind ?? TidyRuleKind.Rewrite : TidyRuleKind.Rewrite;
}
