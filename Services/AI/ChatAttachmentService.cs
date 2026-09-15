using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;

namespace FocusCapture.Services.AI;

/// <summary>
/// 附件服务：把用户粘贴/选择的文件变成可发给模型的形态，并管理本地存储。
///
/// 三条职责：
/// 1. 落盘 —— 一律复制到 <c>chat_history/attachments/</c>，文件名 = 内容 SHA256 前 24 位（同内容自动去重）
/// 2. 变形 —— 图片按清晰度档位缩放 + 压成 JPEG（带透明先白底合成）；文档抽成纯文本
/// 3. 清理 —— <see cref="CleanupOrphans"/> 扫描全部会话（含回收站），删掉无人引用的孤儿文件
///
/// 设计约束：会话 JSON 只存 <see cref="ChatAttachment"/> 引用，绝不嵌 base64 —— base64 只在上行请求时临时生成。
/// </summary>
public static class ChatAttachmentService
{
    /// <summary>单条消息附件数上限</summary>
    public const int MaxAttachmentsPerMessage = 5;

    /// <summary>图片源文件大小上限</summary>
    public const long MaxImageSourceBytes = 15L * 1024 * 1024;

    /// <summary>文档源文件大小上限</summary>
    public const long MaxDocumentSourceBytes = 20L * 1024 * 1024;

    /// <summary>文档抽取正文上限（字符）：超出截断并标注，防单个长文档撑爆会话文件与上下文</summary>
    public const int MaxExtractedChars = 50000;

    /// <summary>附件目录（chat_history/attachments）。同步引擎只扫该目录外层的 *.json，附件不会被误上传</summary>
    public static string Dir => FocusCapturePaths.Combine("chat_history", "attachments");

    /// <summary>
    /// 附件落盘后的通知（2026-09-16）。由装配处（MainWindow）订阅，用于把附件登记进文件仓库、
    /// 并在「附件上传网盘」开关打开时排队上传。
    ///
    /// 用事件而不是直接调用文件仓库，是为了让本类对「网盘」这件事零依赖：
    /// 附件服务只管把文件变成能发给模型的形态，要不要上云由上层决定。开关默认关 = 行为与改造前完全一致。
    /// </summary>
    public static event Action<ChatAttachment>? Stored;

    /// <summary>清晰度档位 →（长边像素, JPEG 质量）</summary>
    public static (int MaxEdge, int Quality) QualitySpec(int level) => level switch
    {
        0 => (768, 70),    // 省流
        2 => (2048, 90),   // 高清
        _ => (1568, 85),   // 标准（默认）
    };

    /// <summary>档位名称（设置面板与提示文案用）</summary>
    public static string QualityLabel(int level) => level switch
    {
        0 => "省流",
        2 => "高清",
        _ => "标准",
    };

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff",
    };

    private static readonly HashSet<string> DocumentExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".md", ".markdown", ".txt", ".log", ".json", ".xml", ".csv", ".tsv",
        ".cs", ".py", ".js", ".ts", ".java", ".kt", ".cpp", ".c", ".h", ".hpp", ".go", ".rs", ".php", ".rb", ".swift",
        ".html", ".htm", ".css", ".sql", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf", ".sh", ".bat", ".ps1",
    };

    /// <summary>带 alpha 通道的像素格式（用于决定是否需要白底合成）</summary>
    private static readonly HashSet<PixelFormat> AlphaFormats = new()
    {
        PixelFormats.Bgra32, PixelFormats.Pbgra32,
        PixelFormats.Rgba64, PixelFormats.Prgba64,
        PixelFormats.Rgba128Float, PixelFormats.Prgba128Float,
    };

    // ── 类型判定 ──

    public static bool IsSupportedImage(string filePath) => ImageExts.Contains(Path.GetExtension(filePath));

    public static bool IsSupportedDocument(string filePath) => DocumentExts.Contains(Path.GetExtension(filePath));

    public static bool IsSupported(string filePath) => IsSupportedImage(filePath) || IsSupportedDocument(filePath);

    /// <summary>文件选择框过滤器（加号按钮用）</summary>
    public static string FileDialogFilter =>
        "支持的格式|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.docx;*.md;*.txt;*.log;*.json;*.xml;*.csv;" +
        "*.cs;*.py;*.js;*.ts;*.java;*.cpp;*.h;*.go;*.rs;*.php;*.html;*.css;*.sql;*.yml;*.yaml;*.ini;*.bat;*.ps1;*.sh" +
        "|图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp" +
        "|文档与文本|*.docx;*.md;*.txt;*.log;*.json;*.xml;*.csv;*.cs;*.py;*.js;*.ts;*.html;*.css;*.sql;*.yml;*.sh" +
        "|所有文件|*.*";

    // ── 创建（入口） ──

    /// <summary>
    /// 从磁盘文件创建附件。图片会缩放压缩后落盘；文档会抽文本后落盘。
    /// 失败返回 null 并给出中文原因（不抛异常，调用方直接展示）。
    /// </summary>
    public static Task<(ChatAttachment? Attachment, string? Error)> CreateFromFileAsync(
        string sourcePath, int qualityLevel)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!File.Exists(sourcePath))
                    return ((ChatAttachment?)null, "文件不存在");

                var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
                if (ext == ".pdf")
                    return ((ChatAttachment?)null, "PDF 暂不支持（本次开发未包含）");

                if (IsSupportedImage(sourcePath))
                {
                    var info = new FileInfo(sourcePath);
                    if (info.Length > MaxImageSourceBytes)
                        return ((ChatAttachment?)null, $"图片超过 {MaxImageSourceBytes / 1024 / 1024}MB 上限");

                    var processed = CompressImageFile(sourcePath, qualityLevel);
                    var stored = WriteWithHash(processed.Data, ".jpg");
                    var image = new ChatAttachment
                    {
                        StoredName = stored,
                        FileName = Path.GetFileName(sourcePath),
                        Kind = ChatAttachmentKind.Image,
                        SizeBytes = processed.Data.Length,
                        PixelWidth = processed.Width,
                        PixelHeight = processed.Height,
                    };
                    NotifyStored(image);
                    return (image, (string?)null);
                }

                if (IsSupportedDocument(sourcePath))
                {
                    var info = new FileInfo(sourcePath);
                    if (info.Length > MaxDocumentSourceBytes)
                        return ((ChatAttachment?)null, $"文档超过 {MaxDocumentSourceBytes / 1024 / 1024}MB 上限");

                    var (text, note) = ExtractDocumentText(sourcePath);
                    if (string.IsNullOrWhiteSpace(text))
                        return ((ChatAttachment?)null, "文档中没有可提取的文字");

                    var raw = File.ReadAllBytes(sourcePath);
                    var stored = WriteWithHash(raw, ext);

                    var document = new ChatAttachment
                    {
                        StoredName = stored,
                        FileName = Path.GetFileName(sourcePath),
                        Kind = ChatAttachmentKind.Document,
                        SizeBytes = raw.Length,
                        ExtractedText = text,
                        ExtractNote = note,
                    };
                    NotifyStored(document);
                    return (document, (string?)null);
                }

                return ((ChatAttachment?)null, $"不支持的格式：{ext}");
            }
            catch (Exception ex)
            {
                return ((ChatAttachment?)null, "读取失败：" + ex.Message);
            }
        });
    }

    /// <summary>
    /// 从剪贴板位图创建附件（截图粘贴路径）。
    /// 必须先 Clone + Freeze：WPF 图像对象有线程亲和性，不冻结无法在后台线程处理。
    /// </summary>
    public static Task<ChatAttachment?> CreateFromBitmapAsync(
        BitmapSource source, string displayName, int qualityLevel)
    {
        BitmapSource frozen;
        try
        {
            frozen = (BitmapSource)source.Clone();
            frozen.Freeze();
        }
        catch
        {
            return Task.FromResult<ChatAttachment?>(null);
        }

        return Task.Run(() =>
        {
            try
            {
                var data = CompressBitmap(frozen, qualityLevel, out var w, out var h);
                var stored = WriteWithHash(data, ".jpg");
                return new ChatAttachment
                {
                    StoredName = stored,
                    FileName = displayName,
                    Kind = ChatAttachmentKind.Image,
                    SizeBytes = data.Length,
                    PixelWidth = w,
                    PixelHeight = h,
                };
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>通知订阅者（附件已落盘）。订阅者异常绝不能影响附件本身的创建流程。</summary>
    private static void NotifyStored(ChatAttachment attachment)
    {
        try { Stored?.Invoke(attachment); }
        catch { /* 网盘相关的问题不该让「粘贴一张图」失败 */ }
    }

    // ── 上行取用 ──

    /// <summary>附件在本机的完整路径</summary>
    public static string ResolvePath(ChatAttachment a) => Path.Combine(Dir, a.StoredName);

    /// <summary>
    /// 生成图片的 base64 data URL（仅上行请求时调用，不入盘、不入会话 JSON）。
    /// 本体缺失（跨端拉来的会话）返回 null，调用方降级为文字占位。
    /// </summary>
    public static string? BuildImageDataUrl(ChatAttachment a)
    {
        try
        {
            var path = ResolvePath(a);
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            var mime = a.StoredName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }

    // ── 清理 ──

    /// <summary>
    /// 孤儿清理：扫描 chat_history 下全部会话文件（含 trash 回收站），
    /// 删除没有任何会话引用的附件文件。应用启动与同步结束后调用。
    ///
    /// 为何不"删会话时立刻删附件"：同内容去重后可能被多条会话共用，直接删会伤及他人；
    /// 统一走引用扫描更安全。1 小时内的新文件跳过（防与正在进行的粘贴竞态）。
    /// </summary>
    public static int CleanupOrphans()
    {
        try
        {
            var dir = Dir;
            if (!Directory.Exists(dir)) return 0;

            var live = CollectReferencedStoredNames();
            var cutoff = DateTime.Now.AddHours(-1);
            var removed = 0;

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (live.Contains(name)) continue;
                try
                {
                    if (File.GetLastWriteTime(file) > cutoff) continue;
                    File.Delete(file);
                    removed++;
                }
                catch { /* 单个失败不影响整体 */ }
            }
            return removed;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>扫描全部会话文件，收集被引用的落盘文件名</summary>
    private static HashSet<string> CollectReferencedStoredNames()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var historyDir = FocusCapturePaths.Combine("chat_history");
        if (!Directory.Exists(historyDir)) return live;

        void ScanDir(string d)
        {
            foreach (var file in Directory.EnumerateFiles(d, "*.json"))
            {
                var session = ChatSessionService.LoadFile(file);
                if (session?.Messages == null) continue;
                foreach (var m in session.Messages)
                {
                    if (m.Attachments == null) continue;
                    foreach (var a in m.Attachments)
                        if (!string.IsNullOrEmpty(a.StoredName)) live.Add(a.StoredName);
                }
            }
        }

        ScanDir(historyDir);
        var trashDir = Path.Combine(historyDir, "trash");
        if (Directory.Exists(trashDir)) ScanDir(trashDir);
        return live;
    }

    // ── 图片压缩 ──

    private static (byte[] Data, int Width, int Height) CompressImageFile(string path, int qualityLevel)
    {
        using var fs = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("图片没有可读帧");
        var frame = decoder.Frames[0];
        var data = CompressBitmap(frame, qualityLevel, out var w, out var h);
        return (data, w, h);
    }

    /// <summary>缩放（按档位长边）+ 白底合成 + JPEG 编码</summary>
    private static byte[] CompressBitmap(BitmapSource source, int qualityLevel, out int width, out int height)
    {
        var (maxEdge, quality) = QualitySpec(qualityLevel);

        BitmapSource working = source;
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest > maxEdge)
        {
            var scale = (double)maxEdge / longest;
            var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            scaled.Freeze();
            working = scaled;
        }

        width = working.PixelWidth;
        height = working.PixelHeight;

        var flat = FlattenAlpha(working);

        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(flat));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>带 alpha 的图先合成到白底：JPEG 不支持透明，直接编码会把透明区变黑</summary>
    private static BitmapSource FlattenAlpha(BitmapSource src)
    {
        if (!AlphaFormats.Contains(src.Format)) return src;

        var w = src.PixelWidth;
        var h = src.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawImage(src, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    // ── 落盘 ──

    /// <summary>按内容哈希落盘（同内容自动去重），返回文件名</summary>
    private static string WriteWithHash(byte[] data, string ext)
    {
        var dir = Dir;
        Directory.CreateDirectory(dir);
        var hash = Convert.ToHexString(SHA256.HashData(data))[..24].ToLowerInvariant();
        var name = hash + ext;
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) File.WriteAllBytes(path, data);
        return name;
    }

    // ── 文档抽文本 ──

    /// <summary>抽出文档正文；返回（文本, 截断说明）</summary>
    private static (string Text, string? Note) ExtractDocumentText(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var raw = ext == ".docx" ? ExtractDocx(path) : ExtractPlainText(path);

        var text = raw.Replace("\r\n", "\n").Trim();
        if (text.Length > MaxExtractedChars)
            return (text[..MaxExtractedChars], $"（文档过长，仅取前 {MaxExtractedChars} 字）");
        return (text, null);
    }

    /// <summary>docx 抽文本：解 zip 读 word/document.xml，按 &lt;w:p&gt; 段落组织（零依赖，与 NoteImportService 同思路）</summary>
    private static string ExtractDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("Word 文件缺少 word/document.xml，格式异常");

        var xml = new XmlDocument();
        using (var s = entry.Open())
        using (var r = new StreamReader(s, Encoding.UTF8))
            xml.LoadXml(r.ReadToEnd());

        var nsmgr = new XmlNamespaceManager(xml.NameTable);
        nsmgr.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");

        var sb = new StringBuilder();
        var paragraphs = xml.SelectNodes("//w:p", nsmgr);
        if (paragraphs != null)
        {
            foreach (XmlNode p in paragraphs)
            {
                var texts = p.SelectNodes(".//w:t", nsmgr);
                if (texts == null) { sb.Append('\n'); continue; }
                foreach (XmlNode t in texts) sb.Append(t.InnerText);
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 纯文本类：要求合法 UTF-8。非 UTF-8（如 ANSI/GBK）会解码失败并给出明确提示，
    /// 不做自动编码嗅探（宁可能力边界清晰，也不猜错编码导致乱码内容被发给模型）。
    /// </summary>
    private static string ExtractPlainText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var text = strict.GetString(StripBom(bytes));
            return text;
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("文件不是 UTF-8 编码，暂不支持（请另存为 UTF-8 后重试）");
        }
    }

    private static byte[] StripBom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return bytes[3..];
        return bytes;
    }
}
