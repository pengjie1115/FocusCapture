using FocusCapture.Services.AI;

namespace FocusCapture.Services.AI;

/// <summary>
/// 动态提示词组装：根据输入形态选择词典式释义或整段翻译，禁止单模板走天下。
/// </summary>
public static class PromptBuilder
{
    /// <summary>短词判定：≤30 字符且不含空格 → 词典式释义；否则整段翻译</summary>
    public static bool IsShortWord(string text)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length <= 30 && !trimmed.Any(char.IsWhiteSpace);
    }

    public static string BuildTranslatePrompt(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (IsShortWord(trimmed))
        {
            return $"请解释这个单词/短语：{trimmed}\n" +
                   "请以词典式输出：词性、释义、常见搭配、例句。";
        }
        return $"请把以下内容翻译成中文，并简要解释其含义：\n\n{trimmed}";
    }
}
