using System.Threading;
using FocusCapture.Services.Files;

namespace FocusCapture.Services.Agent;

/// <summary>
/// 文档类工具共用的来源解析（2026-09-17）。
///
/// 与文件类工具同一条红线：**没有路径参数**。可达范围只有两种 ——
/// 用户亲手签发的牌号 <c>handle</c>、本地元数据里的 <c>file_id</c>；模型两样都编不出来。
/// </summary>
internal static class DocumentSourceResolver
{
    public static async Task<(string? Path, string? Error)> ResolveAsync(string argumentsJson, CancellationToken ct)
    {
        if (ToolArgs.TryGetString(argumentsJson, "handle", out var handle))
        {
            if (!FileHandleStore.TryResolve(handle, out var localPath, out var handleError))
                return (null, "错误：" + handleError);
            return (localPath, null);
        }

        if (ToolArgs.TryGetString(argumentsJson, "file_id", out var fileId))
        {
            var (meta, error) = FileToolSupport.ResolveFileRef(fileId);
            if (meta == null) return (null, "错误：" + error);

            var (path, downloadError) = await FileRepository.EnsureLocalAsync(meta.Id, null, ct).ConfigureAwait(false);
            return path == null ? (null, "错误：" + downloadError) : (path, null);
        }

        return (null,
            "错误：需要提供 handle（用户在对话框里选择的文件牌号）或 file_id（find_cloud_files 返回的编号）。" +
            "如果用户想读的是本机某个文件但还没选过，请请他点对话框里的「选择文件」按钮。");
    }
}

/// <summary>
/// 读表格（xlsx / csv）内容供分析（只读）。2026-09-17 新增。
///
/// 为什么值得单做一个工具：表格贴进对话只能看到一大坨文本，
/// 行列语义、总行数、日期列全是模型自己猜 —— 那是出错的重灾区。
/// </summary>
public class ReadSpreadsheetTool : AgentTool
{
    public override string Name => "read_spreadsheet";

    public override string Description =>
        "读取 Excel（.xlsx）或 CSV 表格的内容，用于统计、对比、找异常。" +
        "返回【指定工作表】的总行列数与前面若干行，默认第一个工作表。\n" +
        "⚠️ 返回的是**文本形式**（不是原生表格对象）：只根据返回的内容回答，**不要臆造没返回的行/列**；" +
        "行数不够就说行数不够，不要把部分数据当成全部。\n" +
        "文件来源：handle（用户刚选的本机文件）或 file_id（网盘里的文件，会自动取回）。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"handle":{"type":"string","description":"用户在对话框选择的文件牌号，形如 file:20260917-1"},"file_id":{"type":"string","description":"网盘文件编号（find_cloud_files 返回）"},"sheet":{"type":"string","description":"可选：工作表名称或序号（从 1 开始）；不填取第一个"},"max_rows":{"type":"number","description":"可选：最多返回多少行，默认 50，上限 500"}},"required":[]}""";

    public override bool IsReadOnly => true;

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var (path, error) = await DocumentSourceResolver.ResolveAsync(argumentsJson, ct).ConfigureAwait(false);
        if (path == null) return error!;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var maxRows = ToolArgs.TryGetInt(argumentsJson, "max_rows", out var mr) && mr > 0 ? Math.Min(mr, 500) : 50;

        try
        {
            List<SheetData> sheets;
            if (ext == ".xlsx")
            {
                sheets = await Task.Run(() => XlsxTextExtractor.Read(path), ct).ConfigureAwait(false);
            }
            else if (ext is ".csv" or ".tsv")
            {
                var sep = ext == ".tsv" ? '\t' : ',';
                var text = await Task.Run(() => File.ReadAllText(path, new UTF8Encoding(false, true)), ct)
                    .ConfigureAwait(false);
                var rows = ParseDelimited(text, sep);
                var sheet = new SheetData { Name = Path.GetFileName(path) };
                foreach (var r in rows.Take(XlsxTextExtractor.MaxRowsPerSheet)) sheet.Rows.Add(r);
                sheet.Truncated = rows.Count > XlsxTextExtractor.MaxRowsPerSheet;
                sheets = new List<SheetData> { sheet };
            }
            else
            {
                return $"错误：「{Path.GetFileName(path)}」不是表格文件（支持 .xlsx / .csv / .tsv）。" +
                       "如果是 Word 或 PDF，请用 read_pdf（PDF）或直接让用户贴进对话。";
            }

            var sheetNames = string.Join("、", sheets.Select((s, i) => $"{i + 1}.{s.Name}"));
            SheetData target = sheets[0];
            if (ToolArgs.TryGetString(argumentsJson, "sheet", out var sheetArg))
            {
                var wanted = sheetArg.Trim();
                SheetData? picked = int.TryParse(wanted, out var idx) && idx >= 1 && idx <= sheets.Count
                    ? sheets[idx - 1]
                    : sheets.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase));

                if (picked == null)
                    return $"错误：没有名为「{wanted}」的工作表。可用的工作表：{sheetNames}";
                target = picked;
            }

            var cols = target.Rows.Count == 0 ? 0 : target.Rows.Max(r => r.Count);
            var sb = new StringBuilder();
            sb.Append($"文件：{Path.GetFileName(path)}；工作表：{target.Name}（共 {target.RowCount} 行 × {cols} 列）");
            if (sheets.Count > 1) sb.Append($"\n该文件共有 {sheets.Count} 个工作表：{sheetNames}");
            if (target.Truncated) sb.Append($"\n（表太大，只读取了前 {XlsxTextExtractor.MaxRowsPerSheet} 行）");
            sb.Append('\n');

            if (target.RowCount == 0)
            {
                sb.Append("（这个工作表是空的）");
                return sb.ToString();
            }

            var shown = Math.Min(maxRows, target.RowCount);
            for (var i = 0; i < shown; i++)
                sb.Append($"[{i + 1}] ").Append(string.Join(" | ", target.Rows[i])).Append('\n');

            if (target.RowCount > shown)
                sb.Append($"（只列了前 {shown} 行，共 {target.RowCount} 行；需要更多请提高 max_rows）");

            return sb.ToString();
        }
        catch (DecoderFallbackException)
        {
            return $"错误：「{Path.GetFileName(path)}」不是 UTF-8 编码的文本（常见于 Excel 导出的 GBK 编码 CSV），" +
                   "请让用户另存为 UTF-8 后再试。";
        }
        catch (InvalidDataException ex)
        {
            return "错误：" + ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Error("Agent", "读表格失败", ex);
            return "错误：读表格失败 —— " + ex.Message;
        }
    }

    /// <summary>极简分隔符解析：支持双引号包裹与 "" 转义（表格导出最常见的形式）</summary>
    private static List<List<string>> ParseDelimited(string text, char separator)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }

            if (ch == '"') { inQuotes = true; continue; }

            if (ch == separator) { row.Add(field.ToString().Trim()); field.Clear(); continue; }

            if (ch == '\r') continue;

            if (ch == '\n')
            {
                row.Add(field.ToString().Trim());
                field.Clear();
                rows.Add(row);
                row = new List<string>();
                continue;
            }

            field.Append(ch);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString().Trim());
            rows.Add(row);
        }

        return rows;
    }
}

/// <summary>
/// 读 PDF 文字内容（只读）。2026-09-17 新增。
///
/// ⚠️ 能力边界必须写进工具描述：**扫描件/图片版 PDF 没有文字层，读不出来**（属 OCR，本应用不做）。
/// 不写清楚的话，模型会把"读不到"编成"文件里没有相关内容"——那比报错更糟。
/// </summary>
public class ReadPdfTool : AgentTool
{
    public override string Name => "read_pdf";

    public override string Description =>
        "读取 PDF 里的文字，用于总结、翻译、找信息。文件来源：handle（用户刚选的本机文件）或 file_id（网盘里的 PDF，会自动取回）。\n" +
        "⚠️ **只对「有文字层的 PDF」有效**：扫描件、图片版 PDF 里根本没有文字，工具会明确告诉你读不出来 —— " +
        "那种情况不要编内容，改用 fetch_cloud_file 把文件取回给用户自己看。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"handle":{"type":"string","description":"用户在对话框选择的文件牌号，形如 file:20260917-1"},"file_id":{"type":"string","description":"网盘文件编号（find_cloud_files 返回）"},"max_chars":{"type":"number","description":"可选：最多读取多少字，默认 20000，上限 60000"}},"required":[]}""";

    public override bool IsReadOnly => true;

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var (path, error) = await DocumentSourceResolver.ResolveAsync(argumentsJson, ct).ConfigureAwait(false);
        if (path == null) return error!;

        if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            return $"错误：「{Path.GetFileName(path)}」不是 PDF。表格请用 read_spreadsheet；Word/纯文本可以让用户直接贴进对话。";

        var maxChars = ToolArgs.TryGetInt(argumentsJson, "max_chars", out var mc) && mc > 0 ? Math.Min(mc, 60000) : 20000;

        try
        {
            var (text, note) = await Task.Run(
                () => DocumentTextExtractor.Extract(path, maxChars), ct).ConfigureAwait(false);
            var tail = string.IsNullOrEmpty(note) ? "" : "\n\n…" + note;
            return $"文件「{Path.GetFileName(path)}」的文字内容：\n\n{text}{tail}";
        }
        catch (InvalidDataException ex)
        {
            return "错误：" + ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Error("Agent", "读 PDF 失败", ex);
            return "错误：读 PDF 失败 —— " + ex.Message;
        }
    }
}
