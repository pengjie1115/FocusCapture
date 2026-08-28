using System;
using System.Diagnostics;
using FocusCapture.Models;
using FocusCapture.Services.Sync;

namespace FocusCapture.Services;

public class NoteService
{
    private readonly AppSettings _settings;
    private readonly DeletedNoteService _deletedService;
    private readonly RecycleBinService _recycleBin;

    // Windows 文件名非法字符
    private static readonly char[] InvalidFileChars = Path.GetInvalidFileNameChars();

    /// <summary>静态整文件写锁（v3.5）：UpdateTodo（原地改行）与同步层 AppendLine/RemoveLines 共用，
    /// 防后台同步（SyncEngine）与用户操作并发整文件重写互相覆盖。</summary>
    private static readonly object FileWriteLock = new();

    /// <summary>速览行格式：- [yyyy-MM-dd HH:mm] 内容 — 来源: xxx（兼容旧格式 - [HH:mm]）。public 让 NoteImportService 复用同一份正则。</summary>
    public static readonly Regex NoteLineRegex = new(
        @"^- \[(\d{4}-\d{2}-\d{2} )?(\d{2}:\d{2})\] (.+?)(?: — 来源: (.+))?$",
        RegexOptions.Compiled);

    /// <summary>本机笔记变更事件（保存/编辑/AI 回填/删除成功后触发）——SyncEngine 订阅后启动 30s 合并窗口推送（QUEST-5 任务6）。</summary>
    public event Action? NotesChanged;

    /// <summary>回收站服务（公开：SyncEngine 软删落地、UI 层复用同一实例）。</summary>
    public RecycleBinService RecycleBin => _recycleBin;

    /// <summary>外部路径（回收站恢复/清空等，非 SaveNote/AppendEdit/DeleteNote 内部）完成本机变更后调用，触发 NotesChanged。</summary>
    public void RaiseNotesChanged() => NotesChanged?.Invoke();

    public NoteService(AppSettings settings)
    {
        _settings = settings;
        _deletedService = new DeletedNoteService();
        _recycleBin = new RecycleBinService(settings.NotesPath);
    }

    /// <summary>软删除服务（暴露给 UI 层调用 MarkDeleted）</summary>
    public DeletedNoteService DeletedService => _deletedService;

    /// <summary>
    /// 保存笔记/待办。type=Todo 时自动做本地规则时间识别（命中且是未来时间 → 设 DueTime；已过/未命中 → 纯待办）。
    /// dueTime 非空（如输入框已弹窗确认过）→ 直接用显式值，跳过自动识别。
    /// </summary>
    public NoteEntry? SaveNote(string content, string? sourceWindow = null, NoteType type = NoteType.Note, DateTime? dueTime = null)
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

        // v3.5：待办类型 + 规则解析（命中且是未来时间 → 设 DueTime；已过 / 未命中 → 不设）
        // v2：dueTime 显式传入（输入框已弹窗确认过）→ 优先用显式值
        entry.Type = type;
        if (type == NoteType.Todo)
        {
            if (dueTime.HasValue)
                entry.DueTime = dueTime.Value;
            else if (TimeParser.TryParse(entry.Content, out var due))
                entry.DueTime = due;
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
            // 降级：不带标签写入默认文件（保持类型）
            if (!string.IsNullOrEmpty(entry.Tag))
            {
                entry.Tag = null;
                return SaveNote(content, sourceWindow, type);
            }
            return null;
        }

        NotesChanged?.Invoke();
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

    /// <summary>编辑保存：追加带标记的新行，原行不动（MD 只增不减）。带 (ref 原笔记时间戳) 精确关联回原笔记。</summary>
    public bool AppendEdit(NoteEntry entry, string newContent)
    {
        if (entry == null || string.IsNullOrWhiteSpace(newContent)) return false;
        if (ImmersiveSessionService.IsLocked(entry.Timestamp)) return false;

        var filePath = FindEntryFile(entry);
        if (filePath == null) return false;

        // 多段内容换行转义为单行标记，保持单行存储格式
        var escaped = newContent
            .Replace("\r\n", "\n")
            .Replace("\n", "\u23CE");
        var line = $"- [{DateTime.Now:yyyy-MM-dd HH:mm}] 【编辑】{escaped} — 来源: 手动编辑 (ref {entry.Timestamp:yyyy-MM-dd HH:mm})";

        try
        {
            File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
            NotesChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 编辑保存失败 ({filePath}): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// AI 回填-追加到原笔记：追加一条带标记的新行（MD 只增不减，禁止覆盖原行）。
    /// 行尾带 (ref 原笔记时间戳)，解析时精确关联回原笔记（展示层合并为子条目）。
    /// 受沉浸式锁定约束：锁定时返回 false。
    /// </summary>
    public bool AppendToNote(NoteEntry entry, string fillText)
    {
        if (entry == null || string.IsNullOrWhiteSpace(fillText)) return false;
        if (ImmersiveSessionService.IsLocked(entry.Timestamp)) return false;

        var filePath = FindEntryFile(entry);
        if (filePath == null) return false;

        // 新行只含回填内容；多段内容换行转义为单行标记，保持单行存储格式
        var escaped = fillText
            .Replace("\r\n", "\n")
            .Replace("\n", "\u23CE");
        var line = $"- [{DateTime.Now:yyyy-MM-dd HH:mm}] 【AI 释义】{escaped} — 来源: AI 回填 (ref {entry.Timestamp:yyyy-MM-dd HH:mm})";

        try
        {
            File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
            NotesChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] AI 回填失败 ({filePath}): {ex.Message}");
            return false;
        }
    }

    /// <summary>AI 回填-单独形成一条新笔记（普通行，独立成条）</summary>
    public NoteEntry? SaveAiNote(string text)
        => SaveNote(text, "AI 回填");

    /// <summary>定位 entry 所在的 md 文件（当天灵感文件 + 全部标签文件），找不到返回 null</summary>
    private string? FindEntryFile(NoteEntry entry)
    {
        // 行前缀：完整日期（新格式）；旧格式 [HH:mm] 行作为兼容回退
        var fullPrefix = $"- [{entry.Timestamp:yyyy-MM-dd HH:mm}]";
        var timePrefix = $"- [{entry.Timestamp:HH:mm}]";

        var dayFile = Path.Combine(_settings.NotesPath, $"灵感_{entry.Timestamp:yyyy-MM-dd}.md");
        var candidates = new List<string>();
        if (File.Exists(dayFile)) candidates.Add(dayFile);

        if (Directory.Exists(_settings.NotesPath))
        {
            foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.StartsWith("灵感_")) continue;
                if (!candidates.Contains(file)) candidates.Add(file);
            }
        }

        foreach (var file in candidates)
        {
            if (FileContainsLine(file, fullPrefix) || FileContainsLine(file, timePrefix))
                return file;
        }
        return null;
    }

    private static bool FileContainsLine(string filePath, string prefix)
    {
        try
        {
            var text = File.ReadAllText(filePath, Encoding.UTF8);
            var idx = text.IndexOf(prefix, StringComparison.Ordinal);
            while (idx >= 0)
            {
                if (idx == 0 || text[idx - 1] == '\n') return true;
                idx = text.IndexOf(prefix, idx + 1, StringComparison.Ordinal);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 读取笔记文件失败 ({filePath}): {ex.Message}");
        }
        return false;
    }

    /// <summary>删除笔记：物理删除 MD 对应行（选项 A）+ 软删除记录；关联的 AI 释义/编辑标记行一并删除。</summary>
    public bool DeleteNote(NoteEntry entry)
    {
        if (entry == null) return false;

        var filePath = FindEntryFile(entry);
        if (filePath == null) return false;

        try
        {
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            var refStr = entry.Timestamp.ToString("yyyy-MM-dd HH:mm");

            // 分钟精度歧义检测：同分钟存在 >1 条原笔记（非标记行）时，(ref 时间戳) 无法区分归属，
            // 删除时保守不删关联标记行（宁可留孤行，不可误删其他笔记的标记行——2026-08-13 测试发现误删+错误软删）。
            var sameMinuteCount = 0;
            foreach (var rawLine in lines)
            {
                var l = rawLine.TrimEnd('\r');
                if (l.Contains("【编辑】", StringComparison.Ordinal) ||
                    l.Contains("【AI 释义】", StringComparison.Ordinal)) continue;
                var m = NoteLineRegex.Match(l);
                if (!m.Success) continue;
                var day = m.Groups[1].Success
                    ? m.Groups[1].Value.Trim()
                    : DateTime.Today.ToString("yyyy-MM-dd");
                if (DateTime.TryParse($"{day} {m.Groups[2].Value}", out var ts) && ts == entry.Timestamp)
                    sameMinuteCount++;
            }
            var refAmbiguous = sameMinuteCount > 1;

            var keep = new List<string>(lines.Length);
            var removedLines = new List<string>();
            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');
                if (IsEntryLine(line, entry) || IsAssociatedMarkerLine(line, refStr, entry, refAmbiguous))
                {
                    removedLines.Add(line);
                    continue;
                }
                keep.Add(line);
            }
            if (removedLines.Count == 0) return false;

            // 先写回收站（成功）→ 再删原行（2026-08-13 审查修正：防"行已删但回收站没记"的数据永久丢失，
            // 见 QUEST-5 §2 铁律与反作弊 9；不再 MarkDeleted，避免 v2.0 软删记录与回收站双轨冲突）
            if (!_recycleBin.Add(Path.GetFileName(filePath), removedLines))
            {
                Debug.WriteLine($"[FocusCapture] 删除中止：回收站写入失败，原行保留 ({filePath})");
                return false;
            }
            File.WriteAllLines(filePath, keep, Encoding.UTF8);
            NotesChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 删除笔记失败 ({filePath}): {ex.Message}");
            return false;
        }
    }

    // ── v3.5 待办：UpdateTodo 原地改行（MD 只增不减的唯一例外，红线 2） ──

    /// <summary>
    /// 待办原地改行：更新内容/状态/提醒时间。用变更前字段定位原行 → 重建行文本 → 整文件重写该行。
    /// 找不到原行返回 false。禁止追加新行；禁止改到非待办行（入口要求 Type=Todo）。
    /// 顺序敏感：先定位后重建——禁止先套用变更再匹配（IsEntryLine 比较 line == entry.ToMarkdownLine()，
    /// 先改字段后新行文本带 (状态:) 等后缀永远匹配不上原行，UpdateTodo 将恒返回 false）。
    /// 与同步层 AppendLine/RemoveLines 共用 FileWriteLock，防后台同步与用户操作并发整文件重写互相覆盖。
    /// </summary>
    public bool UpdateTodo(NoteEntry entry, string? newContent = null, TodoStatus? status = null,
        DateTime? dueTime = null, bool clearDue = false)
    {
        if (entry == null || entry.Type != NoteType.Todo) return false;

        var filePath = FindEntryFile(entry);
        if (filePath == null) return false;

        lock (FileWriteLock)
        {
            try
            {
                var lines = File.ReadAllLines(filePath, Encoding.UTF8);

                // 先定位：用变更前字段（调用方传入的 entry 尚未套用变更）精确整行匹配原行
                var matchedIndex = -1;
                for (var i = 0; i < lines.Length; i++)
                {
                    if (IsEntryLine(lines[i].TrimEnd('\r'), entry)) { matchedIndex = i; break; }
                }
                if (matchedIndex < 0) return false;

                // 后重建：以 entry 原始字段为底，套用变更，FormatTodoLine 与 ToMarkdownLine 同一套格式
                var updated = CloneForUpdate(entry);
                if (newContent != null) updated.Content = newContent.Trim();
                if (clearDue) updated.DueTime = null;
                else if (dueTime.HasValue) updated.DueTime = dueTime;
                if (status.HasValue) updated.TodoStatus = status.Value;

                lines[matchedIndex] = NoteEntry.FormatTodoLine(updated);
                File.WriteAllLines(filePath, lines, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FocusCapture] 待办更新失败 ({filePath}): {ex.Message}");
                return false;
            }
        }

        // 成功后把变更回写传入的 entry（防调用方后续再 UpdateTodo 用旧字段定位失败——面板徽标已办/右键设提醒/建议条设提醒
        // 链路都会先后对同一条待办多次 UpdateTodo，定位必须始终用最新字段）
        if (newContent != null) entry.Content = newContent.Trim();
        if (clearDue) entry.DueTime = null;
        else if (dueTime.HasValue) entry.DueTime = dueTime;
        if (status.HasValue) entry.TodoStatus = status.Value;

        NotesChanged?.Invoke();
        return true;
    }

    /// <summary>浅克隆 NoteEntry（仅携带存储相关字段），供 UpdateTodo 以原始字段为底重建新行。变更不污染调用方传入的 entry。</summary>
    private static NoteEntry CloneForUpdate(NoteEntry e) => new()
    {
        Timestamp = e.Timestamp,
        Content = e.Content,
        SourceWindow = e.SourceWindow,
        Tag = e.Tag,
        Type = e.Type,
        DueTime = e.DueTime,
        TodoStatus = e.TodoStatus
    };

    /// <summary>精确匹配 entry 对应的存储行（新格式整行 / 旧格式 [HH:mm] 兼容）</summary>
    private static bool IsEntryLine(string line, NoteEntry entry)
    {
        if (line == entry.ToMarkdownLine()) return true;

        var escaped = entry.Content
            .Replace("\r\n", "\n")
            .Replace("\n", "\u23CE");
        var old = $"- [{entry.Timestamp:HH:mm}] {escaped}";
        return line == old || line == old + $" — 来源: {entry.SourceWindow}";
    }

    /// <summary>
    /// 判断是否为该条笔记关联的标记行（AI 释义 / 编辑）。
    /// - (ref) 精确匹配：refAmbiguous=true（同分钟有多条原笔记，ref 分钟精度无法区分归属）时**保守不删**，防误删其他笔记的标记行；
    /// - 有 (ref ...) 但匹配不上 → 属于其他笔记，不删；
    /// - 无 ref 的 v2.0 旧数据按 ±60s 回退（2026-08-13 测试发现并修正）。
    /// </summary>
    private static bool IsAssociatedMarkerLine(string line, string refStr, NoteEntry entry, bool refAmbiguous)
    {
        if (!line.Contains("【AI 释义】", StringComparison.Ordinal) &&
            !line.Contains("【编辑】", StringComparison.Ordinal))
            return false;

        // ref 精确匹配：歧义时保守不删
        if (line.Contains($"(ref {refStr})", StringComparison.Ordinal)) return !refAmbiguous;

        // 有 ref 但匹配不上 → 属于其他笔记，不误删
        if (line.Contains("(ref ", StringComparison.Ordinal)) return false;

        var match = NoteLineRegex.Match(line);
        if (!match.Success) return false;
        var day = match.Groups[1].Success
            ? match.Groups[1].Value.Trim()
            : DateTime.Today.ToString("yyyy-MM-dd");
        if (!DateTime.TryParse($"{day} {match.Groups[2].Value}", out var ts)) return false;
        return Math.Abs((ts - entry.Timestamp).TotalMinutes) < 1;
    }

    /// <summary>按指定日期加载笔记：该日灵感文件 + 所有标签文件（旧格式标签行归入“今天”）</summary>
    public List<NoteEntry> LoadNotes(DateTime date)
    {
        var result = new List<NoteEntry>();
        var day = date.ToString("yyyy-MM-dd");

        // 读取该日灵感文件（无标签笔记）
        var dayFile = Path.Combine(_settings.NotesPath, $"灵感_{day}.md");
        if (File.Exists(dayFile))
        {
            result.AddRange(ParseNotes(dayFile, null, date));
        }

        // 也读取所有 tag 文件：新格式行按行内日期归类，旧格式行没有日期、统一归入“今天”
        if (Directory.Exists(_settings.NotesPath))
        {
            foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.StartsWith("灵感_")) continue; // already parsed

                var fileEntries = ParseNotes(file, fileName, DateTime.Today);
                result.AddRange(fileEntries.Where(e => e.Timestamp.Date == date.Date));
            }
        }

        return result
            .Where(e => !_deletedService.IsDeleted(e))
            .OrderByDescending(e => e.Timestamp)
            .ToList();
    }

    private List<NoteEntry> ParseNotes(string filePath, string? tag, DateTime dateContext)
    {
        var entries = new List<NoteEntry>();
        try
        {
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            foreach (var line in lines)
            {
                // 新格式: - [yyyy-MM-dd HH:mm] 内容 — 来源: xxx
                // 旧格式: - [HH:mm] 内容 — 来源: xxx（日期取 dateContext）
                var match = NoteLineRegex.Match(line);
                if (!match.Success) continue;

                var day = match.Groups[1].Success
                    ? match.Groups[1].Value.Trim()
                    : dateContext.ToString("yyyy-MM-dd");
                if (!DateTime.TryParse($"{day} {match.Groups[2].Value}", out var ts)) continue;

                var rawContent = match.Groups[3].Value.Replace("\u23CE", "\n");
                var source = match.Groups[4].Success ? match.Groups[4].Value : "";

                // 标记行（AI 释义 / 编辑）：关联回最近原笔记，展示层合并为子条目/编辑内容
                var marker = ParseMarkerLine(rawContent, source, out var markerText, out var refTs);
                if (marker != null)
                {
                    NoteEntry? target = null;
                    // 有 ref → 精确关联；无 ref → 同文件、时间相近（±60s）的最近原笔记
                    if (refTs.HasValue)
                        target = entries.LastOrDefault(e => Math.Abs((e.Timestamp - refTs.Value).TotalMinutes) < 1);
                    if (target == null)
                        target = entries.LastOrDefault(e => Math.Abs((e.Timestamp - ts).TotalMinutes) < 1);

                    if (target != null)
                    {
                        if (marker == "AI") target.AiFills.Add(markerText);
                        else target.EditedContent = markerText;
                        continue; // 关联成功，不生成独立条目
                    }

                    // 找不到相近原笔记 → 独立成条（内容去掉标记前缀，Tag 置空）
                    entries.Add(new NoteEntry
                    {
                        Timestamp = ts,
                        Content = markerText,
                        SourceWindow = marker == "AI" ? "AI 回填" : "手动编辑",
                        Tag = null
                    });
                    continue;
                }

                // 普通行（v3.5 待办解析：必须放在 ParseMarkerLine 判定之后，否则待办正文以【编辑】/【AI 释义】开头会被误判成标记行）
                if (rawContent.StartsWith("【待办】", StringComparison.Ordinal))
                {
                    var entry = new NoteEntry
                    {
                        Timestamp = ts,
                        Type = NoteType.Todo,
                        Content = rawContent,
                        SourceWindow = source,
                        Tag = tag
                    };
                    // 剥离 (提醒: …[, 状态: …]) 与独立 (状态: …) 属性（兼容组合/独立两种括号写法）
                    var body = rawContent["【待办】".Length..].Trim();
                    var attrMatch = Regex.Match(body, @"\(提醒: (\d{4}-\d{2}-\d{2} \d{2}:\d{2})(?:, 状态: (已办|已读))?\)");
                    if (attrMatch.Success)
                    {
                        if (DateTime.TryParse(attrMatch.Groups[1].Value, out var due))
                            entry.DueTime = due;
                        if (attrMatch.Groups[2].Success)
                            entry.TodoStatus = attrMatch.Groups[2].Value == "已办" ? TodoStatus.Done : TodoStatus.Read;
                        body = body.Remove(attrMatch.Index, attrMatch.Length).Trim();
                    }
                    var stMatch = Regex.Match(body, @"\(状态: (已办|已读)\)");
                    if (stMatch.Success)
                    {
                        entry.TodoStatus = stMatch.Groups[1].Value == "已办" ? TodoStatus.Done : TodoStatus.Read;
                        body = body.Remove(stMatch.Index, stMatch.Length).Trim();
                    }
                    entry.Content = body;
                    entries.Add(entry);
                    continue;
                }

                // 普通行
                entries.Add(new NoteEntry
                {
                    Timestamp = ts,
                    Content = rawContent,
                    SourceWindow = source,
                    Tag = tag
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 读取笔记失败 ({filePath}): {ex.Message}");
        }
        return entries;
    }

    /// <summary>识别标记行：返回 "AI"/"Edit" 与内容、可选 (ref 时间戳)；普通行返回 null。ref 可写在内容尾部或来源区。</summary>
    private static string? ParseMarkerLine(string content, string source, out string text, out DateTime? refTs)
    {
        text = "";
        refTs = null;

        string marker;
        string body;
        if (content.StartsWith("【AI 释义】", StringComparison.Ordinal))
        {
            marker = "AI";
            body = content["【AI 释义】".Length..];
        }
        else if (content.StartsWith("【编辑】", StringComparison.Ordinal))
        {
            marker = "Edit";
            body = content["【编辑】".Length..];
        }
        else return null;

        text = body.Trim();

        // 解析 (ref yyyy-MM-dd HH:mm)：优先内容尾部，其次来源区
        var refMatch = Regex.Match($"{text} {source}", @"\(ref (\d{4}-\d{2}-\d{2} \d{2}:\d{2})\)");
        if (refMatch.Success && DateTime.TryParse(refMatch.Groups[1].Value, out var rt))
        {
            refTs = rt;
            // ref 写在内容尾部时，从展示内容中移除
            var idx = text.IndexOf($"(ref {refMatch.Groups[1].Value})", StringComparison.Ordinal);
            if (idx >= 0) text = text[..idx].TrimEnd();
        }
        return marker;
    }

    /// <summary>统计指定月份每天笔记数（含标签文件与当天灵感文件，排除软删除与标记行）</summary>
    public Dictionary<DateTime, int> LoadNoteCounts(int year, int month)
    {
        var counts = new Dictionary<DateTime, int>();
        if (!Directory.Exists(_settings.NotesPath)) return counts;

        foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
        {
            // 旧格式行无日期：灵感文件按文件名日期归属，标签文件按"今天"归属（与 LoadNotes 一致）
            DateTime dateContext = DateTime.Today;
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.StartsWith("灵感_") && DateTime.TryParse(fileName["灵感_".Length..], out var fileDate))
                dateContext = fileDate;
            var tag = fileName.StartsWith("灵感_") ? null : fileName;

            try
            {
                var lines = File.ReadAllLines(file, Encoding.UTF8);
                foreach (var line in lines)
                {
                    var match = NoteLineRegex.Match(line);
                    if (!match.Success) continue;

                    var rawContent = match.Groups[3].Value.Replace("\u23CE", "\n");
                    // 标记行（AI 释义 / 编辑）不是独立笔记，不参与热力统计
                    if (rawContent.StartsWith("【AI 释义】") || rawContent.StartsWith("【编辑】")) continue;

                    var day = match.Groups[1].Success
                        ? match.Groups[1].Value.Trim()
                        : dateContext.ToString("yyyy-MM-dd");
                    if (!DateTime.TryParse($"{day} {match.Groups[2].Value}", out var ts)) continue;
                    if (ts.Year != year || ts.Month != month) continue;

                    // 排除已软删除
                    var entry = new NoteEntry
                    {
                        Timestamp = ts,
                        Content = rawContent,
                        SourceWindow = match.Groups[4].Success ? match.Groups[4].Value : "",
                        Tag = tag
                    };
                    if (_deletedService.IsDeleted(entry)) continue;

                    var key = ts.Date;
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FocusCapture] 日历统计失败 ({file}): {ex.Message}");
            }
        }
        return counts;
    }

    // ── 同步层行级读写扩展（QUEST-5 任务2）：只新增方法，不改现有方法 ──

    /// <summary>
    /// 遍历 NotesPath 下全部 .md，逐行解析（不合并标记行，同步层按纯行级处理），
    /// 返回 (相对路径, 原始行, 解析结果)。相对路径 = 文件名（双机路径一致 → 确定性 ID 一致）。
    /// </summary>
    public List<(string RelativePath, string Line, NoteEntry Entry)> ReadAllLines()
    {
        var result = new List<(string, string, NoteEntry)>();
        if (!Directory.Exists(_settings.NotesPath)) return result;

        foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
        {
            var relativePath = Path.GetFileName(file);
            var fileName = Path.GetFileNameWithoutExtension(file);
            var tag = fileName.StartsWith("灵感_", StringComparison.Ordinal) ? null : fileName;

            string[] lines;
            try { lines = File.ReadAllLines(file, Encoding.UTF8); }
            catch { continue; }

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');
                var m = NoteLineRegex.Match(line);
                if (!m.Success) continue;

                var day = m.Groups[1].Success ? m.Groups[1].Value.Trim() : DateTime.Today.ToString("yyyy-MM-dd");
                if (!DateTime.TryParse($"{day} {m.Groups[2].Value}", out var ts)) continue;

                var entry = new NoteEntry
                {
                    Timestamp = ts,
                    Content = m.Groups[3].Value.Replace("\u23CE", "\n"),
                    SourceWindow = m.Groups[4].Success ? m.Groups[4].Value : "",
                    Tag = tag
                };
                result.Add((relativePath, line, entry));
            }
        }
        return result;
    }

    /// <summary>向指定文件追加一行（目录不存在自动创建）。line 必须是单行格式（含 \u23CE 转义）。共享 FileWriteLock 防并发覆盖。</summary>
    public void AppendLine(string relativePath, string line)
    {
        var safeName = Path.GetFileName(relativePath); // 防御路径穿越
        if (string.IsNullOrEmpty(safeName)) return;
        Directory.CreateDirectory(_settings.NotesPath);
        var filePath = Path.Combine(_settings.NotesPath, safeName);
        lock (FileWriteLock)
        {
            File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    /// <summary>从指定文件移除指定的行（按整行内容精确匹配），供"清空回收站后同步软删"与"冲突替换"用。共享 FileWriteLock。</summary>
    public void RemoveLines(string relativePath, HashSet<string> lineContents)
    {
        var safeName = Path.GetFileName(relativePath);
        if (string.IsNullOrEmpty(safeName)) return;
        var filePath = Path.Combine(_settings.NotesPath, safeName);
        if (!File.Exists(filePath)) return;

        lock (FileWriteLock)
        {
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            var keep = new List<string>(lines.Length);
            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');
                if (lineContents.Contains(line)) continue;
                keep.Add(line);
            }
            File.WriteAllLines(filePath, keep, Encoding.UTF8);
        }
    }

    /// <summary>MD 行本地时间戳 → ISO 8601 UTC 字符串（同步层 CreatedAt/UpdatedAt 约定，分钟精度、DateTimeKind 处理）。</summary>
    public static string ToUtcIsoString(DateTime localTime)
        => DateTime.SpecifyKind(localTime, DateTimeKind.Local).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ── 导入 / 区间 / 查找 扩展（2026-08-15 新增，灵感速览面板功能） ──

    /// <summary>
    /// 导入笔记：写入对应 MD 文件 + Content 去重（与目标日期已有笔记按 Content 完全相同去重，
    /// 重复项默认保留原笔记跳过导入）。调用者负责已勾选过滤。无时间戳条目按 targetDate + 当前时分分配。
    /// 全部处理后触发一次 NotesChanged。
    /// </summary>
    public int ImportNotes(List<NoteEntry> entries, DateTime targetDate)
    {
        if (entries == null || entries.Count == 0) return 0;

        // 收集目标日期所有 MD 文件（含 tag 文件）的现有 Content 用于去重
        var existingContents = LoadAllContents();

        var written = 0;
        var now = DateTime.Now;
        // 无时间戳条目按 targetDate.Date + 递增时分分配（避免同分钟冲突）
        var fallbackCursor = new DateTime(targetDate.Year, targetDate.Month, targetDate.Day, now.Hour, now.Minute, 0);

        try
        {
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Content)) continue;

                // 去重：Content 已存在则跳过
                var contentKey = entry.Content.Trim();
                if (existingContents.Contains(contentKey)) continue;

                // 重新分配时间戳：无时间戳条目 → targetDate + 递增时分
                if (entry.Timestamp == DateTime.MinValue)
                {
                    entry.Timestamp = fallbackCursor;
                    fallbackCursor = fallbackCursor.AddMinutes(1);
                }

                // 决定写入哪个 MD 文件：tag 文件 vs 当天灵感文件
                var fileName = !string.IsNullOrEmpty(entry.Tag)
                    ? $"{entry.Tag}.md"
                    : $"灵感_{entry.Timestamp:yyyy-MM-dd}.md";

                try
                {
                    AppendLine(fileName, entry.ToMarkdownLine());
                    existingContents.Add(contentKey);   // 防同一批次内重复
                    written++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FocusCapture] 导入写入失败 ({fileName}): {ex.Message}");
                }
            }
        }
        finally
        {
            if (written > 0) NotesChanged?.Invoke();
        }
        return written;
    }

    /// <summary>收集所有 MD 文件的现有 Content 集合（用于导入去重）</summary>
    private HashSet<string> LoadAllContents()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(_settings.NotesPath)) return set;

        foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
        {
            try
            {
                foreach (var rawLine in File.ReadAllLines(file, Encoding.UTF8))
                {
                    var m = NoteLineRegex.Match(rawLine.TrimEnd('\r'));
                    if (!m.Success) continue;
                    var c = m.Groups[3].Value.Replace("\u23CE", "\n").Trim();
                    if (!string.IsNullOrEmpty(c)) set.Add(c);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FocusCapture] 读取已有笔记失败 ({file}): {ex.Message}");
            }
        }
        return set;
    }

    /// <summary>加载所有笔记（合并 AI 释义/编辑标记行），用于区间筛选和全局查找的基础集。</summary>
    public List<NoteEntry> LoadAllEntries()
    {
        var result = new List<NoteEntry>();
        if (!Directory.Exists(_settings.NotesPath)) return result;

        foreach (var file in Directory.GetFiles(_settings.NotesPath, "*.md"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var tag = fileName.StartsWith("灵感_", StringComparison.Ordinal) ? null : fileName;
            // dateContext 影响旧格式 [HH:mm] 行的归属（与 LoadNotes 一致用今天）
            result.AddRange(ParseNotes(file, tag, DateTime.Today));
        }

        return result
            .Where(e => !_deletedService.IsDeleted(e))
            .OrderByDescending(e => e.Timestamp)
            .ToList();
    }

    /// <summary>区间加载：start..end（含两端）的所有笔记，按时间戳倒序。</summary>
    public List<NoteEntry> LoadNotesRange(DateTime start, DateTime end)
    {
        var s = start.Date;
        var e = end.Date;
        if (e < s) (s, e) = (e, s);   // 防御性交换

        return LoadAllEntries()
            .Where(x => x.Timestamp.Date >= s && x.Timestamp.Date <= e)
            .ToList();
    }

    /// <summary>全局关键字查找：Content / EditedContent / SourceWindow 任一包含 keyword（不区分大小写）。</summary>
    public List<NoteEntry> LoadNotesSearch(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return new List<NoteEntry>();

        return LoadAllEntries()
            .Where(x =>
                x.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(x.EditedContent) && x.EditedContent.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                x.SourceWindow.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
