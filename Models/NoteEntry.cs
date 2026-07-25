namespace FocusCapture.Models;

public class NoteEntry
{
    public DateTime Timestamp { get; set; }
    public string Content { get; set; } = string.Empty;
    public string SourceWindow { get; set; } = string.Empty;
    public string? Tag { get; set; }  // extracted #tag

    /// <summary>生成单行 markdown 条目，多段内容中的换行用 \u23CE 转义</summary>
    public string ToMarkdownLine()
    {
        var time = Timestamp.ToString("HH:mm");
        // 把多段内容的换行转义为单个标记，保证单行存储格式不被破坏
        var escaped = Content
            .Replace("\r\n", "\n")
            .Replace("\n", "\u23CE");
        var line = $"- [{time}] {escaped}";
        if (!string.IsNullOrWhiteSpace(SourceWindow))
            line += $" — 来源: {SourceWindow}";
        return line;
    }

    public string FirstLine => Content.Split('\n')[0].Trim();
}

public enum ExportFormat
{
    Markdown,
    Json,
    Txt,
    Word
}
