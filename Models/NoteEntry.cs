namespace FocusCapture.Models;

public class NoteEntry
{
    public DateTime Timestamp { get; set; }
    public string Content { get; set; } = string.Empty;
    public string SourceWindow { get; set; } = string.Empty;
    public string? Tag { get; set; }  // extracted #tag

    public string ToMarkdownLine()
    {
        var time = Timestamp.ToString("HH:mm");
        var line = $"- [{time}] {Content}";
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
