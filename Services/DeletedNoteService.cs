using FocusCapture.Models;

namespace FocusCapture.Services;

/// <summary>
/// 软删除跟踪服务：不修改源 .md 文件，仅记录被"隐藏"的笔记指纹。
/// 误删可从源文件回溯；定期清理 90 天前的旧删除记录防止 deleted.json 无限膨胀。
/// </summary>
public class DeletedNoteService
{
    private const int AutoCleanupDays = 90;
    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusCapture");
    private static readonly string FilePath = Path.Combine(BaseDir, "deleted.json");

    private List<DeletedNote> _records = new();

    public DeletedNoteService()
    {
        Load();
        CleanupOld();
    }

    /// <summary>从 NoteEntry 计算对应的 .md 文件名</summary>
    public static string GetFileName(NoteEntry entry)
    {
        return string.IsNullOrEmpty(entry.Tag)
            ? $"灵感_{entry.Timestamp:yyyy-MM-dd}.md"
            : $"{entry.Tag}.md";
    }

    /// <summary>内容指纹：取前 30 字（去前后空格）</summary>
    public static string ComputeFingerprint(string content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        var trimmed = content.Trim();
        return trimmed.Length > 30 ? trimmed[..30] : trimmed;
    }

    /// <summary>是否已被标记为删除</summary>
    public bool IsDeleted(NoteEntry entry)
    {
        var file = GetFileName(entry);
        var fp = ComputeFingerprint(entry.Content);
        return _records.Any(d =>
            d.File.Equals(file, StringComparison.OrdinalIgnoreCase) &&
            d.Timestamp == entry.Timestamp &&
            d.ContentFingerprint == fp);
    }

    /// <summary>标记单条为已删除（去重）</summary>
    public void MarkDeleted(NoteEntry entry)
    {
        if (IsDeleted(entry)) return;
        _records.Add(new DeletedNote
        {
            File = GetFileName(entry),
            Timestamp = entry.Timestamp,
            ContentFingerprint = ComputeFingerprint(entry.Content),
            DeletedAt = DateTime.Now
        });
        Save();
    }

    /// <summary>批量标记</summary>
    public void MarkDeletedRange(IEnumerable<NoteEntry> entries)
    {
        var changed = false;
        foreach (var e in entries)
        {
            if (IsDeleted(e)) continue;
            _records.Add(new DeletedNote
            {
                File = GetFileName(e),
                Timestamp = e.Timestamp,
                ContentFingerprint = ComputeFingerprint(e.Content),
                DeletedAt = DateTime.Now
            });
            changed = true;
        }
        if (changed) Save();
    }

    /// <summary>已删除记录数（用于状态显示/调试）</summary>
    public int Count => _records.Count;

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            _records = JsonSerializer.Deserialize<List<DeletedNote>>(json) ?? new();
        }
        catch { _records = new(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_records,
                new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }
        catch { /* best effort */ }
    }

    /// <summary>清理超过 N 天的删除记录，防止文件无限膨胀</summary>
    private void CleanupOld()
    {
        var cutoff = DateTime.Now.AddDays(-AutoCleanupDays);
        var before = _records.Count;
        _records.RemoveAll(d => d.DeletedAt < cutoff);
        if (_records.Count != before) Save();
    }
}
