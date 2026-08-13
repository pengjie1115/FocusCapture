using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FocusCapture.Services.Sync;

/// <summary>
/// 回收站单条记录：一次删除操作涉及的（原行 + 关联【编辑】/【AI 释义】标记行）。
/// 存储于 NotesPath/.recycle_bin/recycle-{timestamp}.json，本地明文。
/// </summary>
public class RecycleBinEntry
{
    public string RelativePath { get; set; } = "";
    public List<string> Lines { get; set; } = new();
    public DateTime DeletedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>列表预览：取首行内容（去掉时间戳前缀，\u23CE 还原为空格）</summary>
    public string Preview
    {
        get
        {
            var first = Lines.FirstOrDefault() ?? "";
            var m = Regex.Match(first, @"^- \[\d{4}-\d{2}-\d{2} \d{2}:\d{2}\] (.+)");
            return (m.Success ? m.Groups[1].Value : first).Replace("\u23CE", " ");
        }
    }
}

/// <summary>
/// 本地回收站（QUEST-5 任务5）：删除先进回收站（N=30 天），可恢复；确认清空才物理删除。
/// 存储：NotesPath/.recycle_bin/ 下的 recycle-*.json。与 v2.0 DeletedNoteService（deleted.json）并存互不干扰。
/// </summary>
public class RecycleBinService
{
    private readonly string _binDir;
    private readonly int _retentionDays;

    public RecycleBinService(string notesPath, int retentionDays = 30)
    {
        _binDir = Path.Combine(notesPath, ".recycle_bin");
        _retentionDays = retentionDays;
        CleanupExpired();
    }

    /// <summary>删除时移入回收站：记录被删的行（含关联标记行）</summary>
    public void Add(string relativePath, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        var entry = new RecycleBinEntry
        {
            RelativePath = relativePath,
            Lines = lines.ToList(),
            DeletedAt = DateTime.Now,
            ExpiresAt = DateTime.Now.AddDays(_retentionDays)
        };
        try
        {
            Directory.CreateDirectory(_binDir);
            var fileName = $"recycle-{DateTime.Now:yyyyMMddHHmmssfff}.json";
            File.WriteAllText(Path.Combine(_binDir, fileName),
                JsonSerializer.Serialize(entry), Encoding.UTF8);
        }
        catch { /* 回收站写失败不影响删除主流程（行已从原文件移除） */ }
    }

    /// <summary>列出全部回收站记录（按删除时间倒序），返回 (记录文件名, 记录内容)</summary>
    public List<(string FileName, RecycleBinEntry Entry)> List()
    {
        var result = new List<(string FileName, RecycleBinEntry Entry)>();
        if (!Directory.Exists(_binDir)) return result;
        foreach (var file in Directory.GetFiles(_binDir, "recycle-*.json"))
        {
            try
            {
                var json = File.ReadAllText(file, Encoding.UTF8);
                var entry = JsonSerializer.Deserialize<RecycleBinEntry>(json);
                if (entry != null) result.Add((Path.GetFileName(file), entry));
            }
            catch { /* 损坏记录跳过 */ }
        }
        return result.OrderByDescending(x => x.Entry.DeletedAt).ToList();
    }

    /// <summary>恢复：把记录里的行追加回原文件（ID 不变），并删除回收站记录</summary>
    public void Restore(string fileName, RecycleBinEntry entry, NoteService noteService)
    {
        foreach (var line in entry.Lines)
            noteService.AppendLine(entry.RelativePath, line);
        try
        {
            var path = Path.Combine(_binDir, fileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    /// <summary>清空回收站：物理删除全部记录（行早已从原文件移除，删记录即彻底丢弃）</summary>
    public void PurgeAll()
    {
        if (!Directory.Exists(_binDir)) return;
        foreach (var file in Directory.GetFiles(_binDir, "recycle-*.json"))
        {
            try { File.Delete(file); } catch { }
        }
    }

    /// <summary>清理过期记录（启动时调用）</summary>
    private void CleanupExpired()
    {
        if (!Directory.Exists(_binDir)) return;
        var now = DateTime.Now;
        foreach (var file in Directory.GetFiles(_binDir, "recycle-*.json"))
        {
            try
            {
                var json = File.ReadAllText(file, Encoding.UTF8);
                var entry = JsonSerializer.Deserialize<RecycleBinEntry>(json);
                if (entry != null && entry.ExpiresAt < now) File.Delete(file);
            }
            catch { }
        }
    }
}
