using System.Threading;
using FocusCapture.Models;

namespace FocusCapture.Services.Agent;

/// <summary>
/// 把笔记/待办导出成本机文件（写，需确认）。2026-09-17 新增。
///
/// 落点 = 设置里的「默认导出文件夹」（<see cref="AppSettings.ExportFolderPath"/>），与界面导出同一处，
/// **不进网盘**（用户明确要求：导出就是本机拿一份，不要外流）。
///
/// 未设置过导出文件夹时**不弹文件夹选择框** —— 工具跑在后台线程，弹框会打断对话；
/// 改为明确告诉用户去界面上选一次。
/// </summary>
public class ExportNotesTool : AgentTool
{
    private readonly NoteService _noteService;
    private readonly AppSettings _settings;

    public ExportNotesTool(NoteService noteService, AppSettings settings)
    {
        _noteService = noteService;
        _settings = settings;
    }

    public override string Name => "export_notes";

    public override string Description =>
        "把笔记/待办导出成本机文件（Markdown / 文本 / Word / JSON）。" +
        "用户说「把今天记的导出来」「导一份给我」时使用。默认导出**今天**的记录，也可用 from_date/to_date 指定区间。\n" +
        "文件存到用户设置好的默认导出文件夹，**不联网、不上传网盘**。\n" +
        "若返回「还没设置导出文件夹」，请让用户先在界面上做一次导出（那里会让他选文件夹），之后就能直接导了。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"date":{"type":"string","description":"可选，某一天 yyyy-MM-dd；不填且未给区间时默认今天"},"from_date":{"type":"string","description":"可选，起始日 yyyy-MM-dd（与 to_date 配对）"},"to_date":{"type":"string","description":"可选，结束日 yyyy-MM-dd"},"format":{"type":"string","description":"可选：md（默认）/ txt / docx / json"}},"required":[]}""";

    public override bool IsReadOnly => false;

    public override string DescribeAction(string argumentsJson)
    {
        var (start, end, _) = ResolveRange(argumentsJson);
        var scope = start == end
            ? $"{start:yyyy-MM-dd} 的记录"
            : $"{start:yyyy-MM-dd} 至 {end:yyyy-MM-dd} 的记录";
        ToolArgs.TryGetString(argumentsJson, "format", out var fmt);
        var f = string.IsNullOrWhiteSpace(fmt) ? "md" : fmt.Trim().ToLowerInvariant();
        return $"把{scope}导出为 {f} 文件（存到本机导出文件夹）";
    }

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.ExportFolderPath))
            return "错误：还没有设置导出文件夹。请让用户先打开「灵感速览 → 导出」选一次文件夹，" +
                   "之后就能直接导出了（工具不会自己弹文件夹选择框，那会打断对话）。";

        var (start, end, rangeError) = ResolveRange(argumentsJson);
        if (rangeError != null) return rangeError;

        var formatStr = "md";
        if (ToolArgs.TryGetString(argumentsJson, "format", out var fmtRaw)) formatStr = fmtRaw.Trim().ToLowerInvariant();

        ExportFormat? format = formatStr switch
        {
            "md" or "markdown" => ExportFormat.Markdown,
            "txt" or "text" => ExportFormat.Txt,
            "docx" or "word" => ExportFormat.Word,
            "json" => ExportFormat.Json,
            _ => null,
        };
        if (format == null)
            return $"错误：format「{formatStr}」不支持，可用：md / txt / docx / json。";

        var entries = await Task.Run(() => _noteService.LoadNotesRange(start, end), ct);
        if (entries.Count == 0)
            return start == end
                ? $"{start:yyyy-MM-dd} 没有记录，无需导出。"
                : $"{start:yyyy-MM-dd} 至 {end:yyyy-MM-dd} 没有记录，无需导出。";

        var svc = new NoteExportService();
        var config = new ExportConfig
        {
            IncludeTime = true,
            IncludeSource = true,
            IncludeTag = true,
            IncludeContent = true,
            Format = format.Value,
        };

        var baseName = start == end
            ? $"灵感_{start:yyyy-MM-dd}"
            : $"灵感_{start:yyyy-MM-dd}_{end:yyyy-MM-dd}";
        var fileName = NoteExportService.SanitizeFileName(baseName) + svc.GetFileExtension(format.Value);

        try
        {
            Directory.CreateDirectory(_settings.ExportFolderPath);
            var path = NoteExportService.GetUniquePath(Path.Combine(_settings.ExportFolderPath, fileName));

            if (format.Value == ExportFormat.Word)
                File.WriteAllBytes(path, svc.BuildWord(entries, config));
            else
                File.WriteAllText(path, svc.BuildExport(entries, config), new UTF8Encoding(false));

            var size = RootMigrationService.FormatSize(new FileInfo(path).Length);
            return $"已导出 {entries.Count} 条（{start:yyyy-MM-dd}{(start == end ? "" : $" 至 {end:yyyy-MM-dd}")}）到：{path}（{size}）。" +
                   "文件在本机，未上传网盘。";
        }
        catch (Exception ex)
        {
            AppLog.Error("Agent", "导出笔记失败", ex);
            return "错误：导出失败 —— " + ex.Message;
        }
    }

    /// <summary>解析导出范围；返回 (起, 止, 错误消息)。错误消息非空时调用方直接返回它。</summary>
    private static (DateTime Start, DateTime End, string? Error) ResolveRange(string argumentsJson)
    {
        DateTime from = default, to = default;
        var hasFrom = ToolArgs.TryGetString(argumentsJson, "from_date", out var fromStr)
                      && DateTime.TryParse(fromStr, out from);
        var hasTo = ToolArgs.TryGetString(argumentsJson, "to_date", out var toStr)
                    && DateTime.TryParse(toStr, out to);

        if (hasFrom || hasTo)
        {
            var start = hasFrom ? from.Date : (hasTo ? to.Date : DateTime.Today);
            var end = hasTo ? to.Date : (hasFrom ? from.Date : DateTime.Today);
            if (end < start) (start, end) = (end, start);
            return (start, end, null);
        }

        if (ToolArgs.TryGetString(argumentsJson, "date", out var dateStr))
        {
            if (!DateTime.TryParse(dateStr, out var d))
                return (DateTime.Today, DateTime.Today, $"错误：date「{dateStr}」格式不对，请用 yyyy-MM-dd。");
            return (d.Date, d.Date, null);
        }

        return (DateTime.Today, DateTime.Today, null);
    }
}
