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

    /// <summary>v3.5 待办编辑时间识别 LLM 兜底提示词：要求只输出严格 JSON，无时间 has_time=false。</summary>
    public static ChatMessage[] BuildTimeParseMessages(string text)
    {
        return new[]
        {
            new ChatMessage(ChatRoles.System,
                "你是一个时间解析器。从用户文本中找出唯一的提醒时间表达，输出严格 JSON：{\"has_time\":true/false,\"time\":\"yyyy-MM-dd HH:mm\"}（无时间则 has_time=false）。只输出 JSON，不要解释。"),
            new ChatMessage(ChatRoles.User, text)
        };
    }
}
