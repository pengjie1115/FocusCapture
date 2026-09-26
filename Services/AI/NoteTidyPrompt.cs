using System.Text.RegularExpressions;

namespace FocusCapture.Services.AI;

/// <summary>
/// 「AI 整理」的提示词、长度闸门与结果清洗 —— <b>刻意零项目依赖</b>（只吃 string、不碰配置 / 日志 / WPF），
/// 所以能被快层检查点直接链接并秒级验证（同 TokenCountParser / ContextBudget 的做法）。
///
/// <para><b>为什么这些要单独成文件而不是塞进调用点</b>：这三件事坏了的表现<b>全是静默的</b> ——
/// 长度闸门算错 → 超长正文照发，用户等半天拿到 400 或半截结果；
/// 结果清洗漏掉围栏 → 笔记里凭空多出 ``` 和「以下是整理后的内容：」；
/// 提示词漏掉「不新增事实」→ 模型开始自由发挥，把用户的原始记录改成了另一件事。</para>
/// </summary>
public static class NoteTidyPrompt
{
    /// <summary>
    /// 单次整理允许的最大输入字符数（超出直接拒绝，不做截断 / 分段 —— 2026-09-26 用户拍板）。
    /// 取值依据：中文 1 字符约 1~1.7 token，8000 字符 ≈ 8k~13k token，加上系统提示词与输出余量，
    /// 主流 32k 窗口模型都能吃下；再长就该让用户自己先精简，而不是赌模型记得住后半段。
    /// </summary>
    public const int MaxInputChars = 8000;

    /// <summary>
    /// 整理用的系统提示词。<b>七条约束一条都不能省</b>，其中 ①（不新增事实）与 ④（不翻译）是最要命的两条：
    /// 用户拿它整理自己的笔记，一旦模型开始"补全"或顺手翻译，产出就不再是他的记录了。
    /// </summary>
    public const string SystemPrompt =
        "你是笔记整理助手。用户会给你一段文字（可能是复制来的一大段，也可能是随手写的、逻辑不清的内容），" +
        "请把它整理得条理清楚。规则：" +
        "① 完整保留原意与全部信息，不新增任何事实、不做评价、不删要点；" +
        "② 按逻辑重新组织：相关的内容归到一起，用分点或短段落，必要时加小标题；" +
        "③ 去掉重复啰嗦与口误，改正明显的错别字，把口语化的表达改通顺；" +
        "④ 保持原文语言（中文还是中文），不要翻译；" +
        "⑤ 若内容是待办事项，保留时间、地点、对象等要素，并按执行顺序排列；" +
        "⑥ 直接输出整理后的正文，不要任何开场白、结语、说明或代码块围栏；" +
        "⑦ 只用 Markdown 的「- 」分点与 **加粗** 小标题，不要使用 # 一级标题。";

    /// <summary>组装一次请求的消息对（system + 用户正文）。返回元组而非 ChatMessage，是为了保持本文件零依赖。</summary>
    public static (string System, string User) BuildMessages(string? text)
        => (SystemPrompt, (text ?? "").Trim());

    /// <summary>正文字符数（前后空白不计）。</summary>
    public static int CharCount(string? text) => (text ?? "").Trim().Length;

    /// <summary>是否超长（超长 = 拒绝整理，见 <see cref="TooLongMessage"/>）。</summary>
    public static bool IsTooLong(string? text) => CharCount(text) > MaxInputChars;

    /// <summary>超长时给用户的人话提示（带上限，用户才知道要精简到多少）。</summary>
    public static string TooLongMessage(int chars) =>
        $"这段内容太长了（{chars} 字符，上限 {MaxInputChars} 字符），AI 整理不了这么长的正文。\n\n" +
        "请先自己删掉一部分再试 —— 硬发过去要么被服务商拒绝，要么只整理了前半段（那样更糟：你会以为后面那半段也整理过了）。";

    /// <summary>
    /// 清洗模型回包：① 剥掉整体包裹的 ``` 代码块围栏；② 剥掉「以下是整理后的内容：」这类开场白。
    ///
    /// <para>只剥<b>确实像前言</b>的首行（≤40 字符、以冒号或句号结尾、且含整理类措辞），
    /// 且剥完必须还剩正文 —— 宁可留着丑，也不能把用户正文的第一行当开场白吃掉。</para>
    /// </summary>
    public static string CleanResult(string? raw)
    {
        var t = (raw ?? "").Replace("\r\n", "\n").Trim();
        if (t.Length == 0) return "";

        // ① 整体围栏：^```lang\n ... \n```$
        var fence = Regex.Match(t, @"^```[A-Za-z]*[ \t]*\n(?<body>[\s\S]*?)\n?```$");
        if (fence.Success) t = fence.Groups["body"].Value.Trim();

        // ② 开场白：只剥行首，最多 3 行，且剥完必须还有正文
        var lines = t.Split('\n').ToList();
        var removed = 0;
        while (lines.Count > 1 && removed < 3)
        {
            var head = lines[0].Trim();
            if (head.Length == 0) { lines.RemoveAt(0); removed++; continue; }
            if (!IsPreamble(head)) break;
            lines.RemoveAt(0);
            removed++;
        }

        return string.Join("\n", lines).Trim();
    }

    /// <summary>这一行像不像模型的开场白（而非用户正文）。判据刻意收紧，避免误吃正文首行。</summary>
    public static bool IsPreamble(string? line)
    {
        var s = (line ?? "").Trim();
        if (s.Length == 0 || s.Length > 40) return false;
        if (!(s.EndsWith("：", StringComparison.Ordinal)
              || s.EndsWith(":", StringComparison.Ordinal)
              || s.EndsWith("。", StringComparison.Ordinal))) return false;

        return s.Contains("以下是", StringComparison.Ordinal)
            || s.Contains("整理后", StringComparison.Ordinal)
            || s.Contains("整理如下", StringComparison.Ordinal)
            || s.Contains("帮你整理", StringComparison.Ordinal)
            || s.Contains("已整理", StringComparison.Ordinal)
            || s.Contains("好的", StringComparison.Ordinal);
    }
}
