using System.Diagnostics;
using FocusCapture.Models;

namespace FocusCapture.Services;

public class NoteService
{
    private readonly AppSettings _settings;
    private readonly DeletedNoteService _deletedService;

    // Windows 文件名非法字符
    private static readonly char[] InvalidFileChars = Path.GetInvalidFileNameChars();

    public NoteService(AppSettings settings)
    {
        _settings = settings;
        _deletedService = new DeletedNoteService();
    }

    /// <summary>软删除服务（暴露给 UI 层调用 MarkDeleted）</summary>
    public DeletedNoteService DeletedService => _deletedService;

    public NoteEntry? SaveNote(string content, string? sourceWindow = null)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var entry = new NoteEntry
        {
            Timestamp = DateTime.Now,
            Content = content.Trim(),
            SourceWindow = sourceWindow ?? Win32.GetActiveWindowTitle(),
        };

        // 提取 #标签（取第一个）
        var tagMatch = Regex.Match(content, @"^#(\S+)");
        if (tagMatch.Success)
        {
            var rawTag = tagMatch.Groups[1].Value;
            // 净化标签：移除文件名非法字符，替换路径分隔符为下划线
            entry.Tag = SanitizeFileName(rawTag);
            // 去掉标签前缀存内容
            entry.Content = content[tagMatch.Length..].Trim();
        }

        // 确定文件名
        string fileName;
        if (!string.IsNullOrEmpty(entry.Tag))
            fileName = $"{entry.Tag}.md";
        else
            fileName = $"灵感_{DateTime.Now:yyyy-MM-dd}.md";

        try
        {
            Directory.CreateDirectory(_settings.NotesPath);
            var filePath = Path.Combine(_settings.NotesPath, fileName);
            var line = entry.ToMarkdownLine();
            File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 写入笔记失败: {ex.Message}");
            // 降级：不带标签写入默认文件
            if (!string.IsNullOrEmpty(entry.Tag))
            {
                entry.Tag = null;
                return SaveNote(content, sourceWindow);
            }
            return null;
        }

        return entry;
    }

    /// <summary>移除文件名非法字符，截断过长标签</summary>
    private static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (Array.IndexOf(InvalidFileChars, c) >= 0)
                sb.Append('_');
            else
                sb.Append(c);
        }
        // 限制标签长度，防止路径过长
        var result = sb.ToString().Trim();
        return result.Length > 60 ? result[..60] : result;
    }

    public string? SaveClipboard(string? sourceWindow = null)
    {
        var text = Win32.GetClipboardText();
        if (string.IsNullOrWhiteSpace(text)) return null;

        SaveNote(text, sourceWindow);
        return text;
    }

    public List<NoteEntry> LoadTodayNotes()
    {
        var result = new List<NoteEntry>();
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        // 读取今日灵感文件
        var todayFile = Path.Combine(_settings.NotesPath, $"灵感_{today}.md");
        if (File.Exists(todayFile))
        {
            result.AddRange(ParseNotes(todayFile, null));
        }

        // 也读取所有日期范围内的 tag 文件
        if (Directory.Exists(_settings.NotesPath))
        {
            foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.StartsWith("灵感_")) continue; // already parsed

                // 读取文件的所有行，筛选今天的
                var fileEntries = ParseNotes(file, fileName);
                result.AddRange(fileEntries.Where(e => e.Timestamp.Date == DateTime.Today));
            }
        }

        return result
            .Where(e => !_deletedService.IsDeleted(e))
            .OrderByDescending(e => e.Timestamp)
            .ToList();
    }

    private List<NoteEntry> ParseNotes(string filePath, string? tag)
    {
        var entries = new List<NoteEntry>();
        try
        {
            var lines = File.ReadAllLines(filePath, System.Text.Encoding.UTF8);
            foreach (var line in lines)
            {
                // 格式: - [HH:mm] 内容 — 来源: xxx
                var match = System.Text.RegularExpressions.Regex.Match(line, @"^- \[(\d{2}:\d{2})\] (.+?)(?: — 来源: (.+))?$");
                if (!match.Success) continue;

                if (DateTime.TryParse($"{DateTime.Today:yyyy-MM-dd} {match.Groups[1].Value}", out var ts))
                {
                    entries.Add(new NoteEntry
                    {
                        Timestamp = ts,
                        Content = match.Groups[2].Value,
                        SourceWindow = match.Groups[3].Success ? match.Groups[3].Value : "",
                        Tag = tag
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 读取笔记失败 ({filePath}): {ex.Message}");
        }
        return entries;
    }
}
