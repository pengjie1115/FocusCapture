using System.Threading;
using FocusCapture.Services.AI;
using FocusCapture.Services.Files;
using FocusCapture.Services.Sync;

namespace FocusCapture.Services.Agent;

/// <summary>
/// 文件交付通道：工具把「要展示给用户的云文件」推到这里，UI 订阅后渲染成对话里的卡片。
///
/// 为什么不直接在工具里开窗/弹资源管理器：工具跑在后台线程，而且工具层不该知道 UI 长什么样。
/// 拆成一个事件，UI 侧决定展示形态，将来换界面不用动工具。
/// </summary>
public static class FileDeliveryHub
{
    /// <summary>工具请求交付某文件（UI 订阅）。</summary>
    public static event Action<FileMetadata>? Delivered;

    /// <summary>请求打开该文件（UI 订阅；工具带 action=open 时触发）。</summary>
    public static event Action<FileMetadata>? OpenRequested;

    /// <summary>请求在资源管理器中定位（UI 订阅）。</summary>
    public static event Action<FileMetadata>? LocateRequested;

    internal static void Deliver(FileMetadata meta) => Delivered?.Invoke(meta);

    internal static void RequestOpen(FileMetadata meta) => OpenRequested?.Invoke(meta);

    internal static void RequestLocate(FileMetadata meta) => LocateRequested?.Invoke(meta);
}

/// <summary>
/// 文件类工具共用的解析与格式化。
///
/// 这里集中体现本次更新最重要的一条红线（方案 §6.2）：
/// <b>工具的路径参数绝不能让 AI 生成</b>。所有涉及「本机哪个文件」的入口，
/// 要么是用户亲手签发的牌号（FileHandleStore），要么是本地元数据里的编号（file_id）——
/// 两者都不是模型能凭空编出来的。
/// </summary>
internal static class FileToolSupport
{
    /// <summary>编号至少给到 8 位，避免模型复制 32 位长串时敲错。</summary>
    public const int RefLength = 12;

    /// <summary>把内部 id 压成便于模型引用的短编号。</summary>
    public static string ShortRef(string id) => id.Length <= RefLength ? id : id[..RefLength];

    /// <summary>按短编号（前缀）解析出唯一文件；有歧义时明确报错而不是猜一个。</summary>
    public static (FileMetadata? Meta, string? Error) ResolveFileRef(string? refId)
    {
        var id = (refId ?? "").Trim();
        if (id.Length == 0)
            return (null, "缺少参数 file_id。请先用 find_cloud_files 查询，拿到编号后再引用。");

        var all = FileRepository.AllMetadata();
        var exact = all.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return (exact, null);

        var matches = all
            .Where(m => m.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => (matches[0], null),
            0 => (null, $"找不到编号为「{id}」的文件。请先用 find_cloud_files 查询可用文件。"),
            _ => (null, $"编号「{id}」匹配到 {matches.Count} 个文件，请输入更长的编号以消歧。"),
        };
    }

    /// <summary>格式化一行文件描述（编号 + 类型 + 名称 + 时间 + 大小 + 标签）。</summary>
    public static string Describe(FileMetadata m)
    {
        var local = FileRepository.FindCache(m.Id) != null && File.Exists(FileRepository.FindCache(m.Id)!.LocalPath);
        var parts = new List<string>
        {
            $"[{ShortRef(m.Id)}]",
            $"{FileTypes.Label(m.Type)}",
            m.Name,
            m.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            RootMigrationService.FormatSize(m.Size),
        };
        if (m.Tags.Count > 0) parts.Add("标签:" + string.Join("/", m.Tags));
        parts.Add(local ? "本机已有" : "需取回");
        return string.Join("  ", parts);
    }
}

/// <summary>
/// 把文件存到网盘（链路 A / B / C，写操作需确认）。
///
/// 三种来源，都不接受「路径」参数：
/// - <c>content</c>：AI 当场生成的内容（链路 A，最轻，不需要任何文件路径）
/// - <c>handle</c>：用户在对话框点「选择文件」后签发的牌号（链路 C）
/// - <c>attachment_name</c>：当前对话里已经附上的附件（链路 B）
/// </summary>
public class StoreFileToCloudTool : AgentTool
{
    private readonly Func<IReadOnlyList<ChatAttachment>> _currentAttachments;

    public StoreFileToCloudTool(Func<IReadOnlyList<ChatAttachment>> currentAttachments)
        => _currentAttachments = currentAttachments;

    public override string Name => "store_file_to_cloud";

    public override string Description =>
        "把文件保存到用户的网盘（云端永久保留，之后可在任意设备取回）。三种用法，按用户意图选一种：\n" +
        "1) 用户说「把这份报告/这段话存到网盘」→ 用 content 参数把内容直接传进来，并用 file_name 起个带扩展名的名字（如「周报.md」）。\n" +
        "2) 用户说「把那张图/那个文件存上去」，且对话框里已经显示了「已选择文件」卡片 → 用 handle 参数原样引用卡片上的牌号（形如 file:20260916-1）。\n" +
        "3) 用户说「把刚才那张图存到网盘」→ 用 attachment_name 参数传当前对话附件里的文件名。\n" +
        "⚠️ 本工具没有也没有任何路径参数：你无法指定本机文件路径，也无法编造牌号。若用户想要的文件还没被选过，" +
        "请明确请他点对话框里的「选择文件」按钮，不要猜测或杜撰路径。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"content":{"type":"string","description":"要保存的文本内容（用法 1，AI 生成的内容）"},"file_name":{"type":"string","description":"文件名，需带扩展名，如 周报.md。与 content 搭配使用"},"handle":{"type":"string","description":"用户在对话框里选择的文件牌号，形如 file:20260916-1。必须原样引用界面上显示的值，不可自行构造"},"attachment_name":{"type":"string","description":"当前对话中已附件的文件名（用法 3）"},"tags":{"type":"string","description":"可选，逗号分隔的标签，如：周报,2026Q3"},"type":{"type":"string","description":"可选，artifact=AI产出 / upload=用户上传 / attachment=对话附件，默认自动判断"}},"required":[]}""";

    public override bool IsReadOnly => false;

    public override string DescribeAction(string argumentsJson)
    {
        ToolArgs.TryGetString(argumentsJson, "file_name", out var name);
        if (ToolArgs.TryGetString(argumentsJson, "handle", out var handle)) return $"把您选择的文件（{handle}）保存到网盘";
        if (ToolArgs.TryGetString(argumentsJson, "attachment_name", out var att)) return $"把对话附件「{att}」保存到网盘";
        if (name.Length > 0) return $"把内容保存到网盘，文件名「{name}」";
        return "把文件保存到网盘";
    }

    /// <summary>放后台线程执行：登记文件要算 SHA256/MD5 并复制文件，大文件上不该占着工具循环的线程。</summary>
    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => Task.Run(() => ExecuteCore(argumentsJson), ct);

    private string ExecuteCore(string argumentsJson)
    {
        ToolArgs.TryGetString(argumentsJson, "content", out var content);
        ToolArgs.TryGetString(argumentsJson, "file_name", out var fileName);
        ToolArgs.TryGetString(argumentsJson, "handle", out var handle);
        ToolArgs.TryGetString(argumentsJson, "attachment_name", out var attachmentName);
        ToolArgs.TryGetString(argumentsJson, "tags", out var tagsRaw);
        ToolArgs.TryGetString(argumentsJson, "type", out var typeRaw);

        var tags = string.IsNullOrWhiteSpace(tagsRaw)
            ? null
            : tagsRaw.Split(new[] { ',', '，', '/', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

        var explicitType = FileTypes.IsValid(typeRaw) ? typeRaw : null;

        // ① 牌号（链路 C）
        if (handle.Length > 0)
        {
            if (!FileHandleStore.TryResolve(handle, out var path, out var error))
                return "错误：" + error;
            var (meta, err) = FileRepository.RegisterLocalFile(
                path, explicitType ?? FileTypes.Upload, Path.GetFileName(path), attachExisting: false, tags);
            if (meta == null) return "错误：" + err;
            NotifyUpload();
            return $"已保存到网盘：{meta.Name}（{RootMigrationService.FormatSize(meta.Size)}，编号 {FileToolSupport.ShortRef(meta.Id)}）。"
                   + UploadNote();
        }

        // ② 当前对话附件（链路 B）
        if (attachmentName.Length > 0)
        {
            var attachments = _currentAttachments();
            var hit = attachments.FirstOrDefault(a =>
                string.Equals(a.FileName, attachmentName, StringComparison.OrdinalIgnoreCase))
                ?? attachments.FirstOrDefault(a =>
                    a.FileName.Contains(attachmentName, StringComparison.OrdinalIgnoreCase));

            if (hit == null)
            {
                var available = attachments.Count == 0
                    ? "当前对话里没有任何附件。"
                    : "当前对话里的附件有：" + string.Join("、", attachments.Select(a => a.FileName));
                return $"错误：没找到名为「{attachmentName}」的对话附件。{available}";
            }

            var path = ChatAttachmentService.ResolvePath(hit);
            if (!File.Exists(path))
                return $"错误：附件「{hit.FileName}」的本体不在本机（可能是从其他设备同步过来的会话）。";

            var (meta, err) = FileRepository.RegisterLocalFile(
                path, explicitType ?? FileTypes.Attachment, hit.FileName, attachExisting: true, tags);
            if (meta == null) return "错误：" + err;
            NotifyUpload();
            return $"已把对话附件「{meta.Name}」保存到网盘（编号 {FileToolSupport.ShortRef(meta.Id)}）。" + UploadNote();
        }

        // ③ AI 生成的内容（链路 A）
        if (!string.IsNullOrWhiteSpace(content))
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "错误：用 content 保存内容时必须同时给出 file_name（带扩展名，如「周报.md」）。";

            var (meta, err) = FileRepository.RegisterText(content, fileName.Trim(),
                explicitType ?? FileTypes.Artifact, tags);
            if (meta == null) return "错误：" + err;
            NotifyUpload();
            return $"已保存到网盘：{meta.Name}（{RootMigrationService.FormatSize(meta.Size)}，编号 {FileToolSupport.ShortRef(meta.Id)}）。"
                   + UploadNote();
        }

        return "错误：没有可保存的来源。请在 content（你生成的内容 + file_name）、handle（用户已选择的文件牌号）、" +
               "attachment_name（当前对话附件名）三者中提供至少一个。" +
               "如果用户想上传本机某个文件但还没选过，请请他点对话框里的「选择文件」按钮。";
    }

    private static void NotifyUpload() => UploadQueue.Kick();

    private static string UploadNote() => FileRepository.CloudReady
        ? "文件已在本机就位，正在后台上传到网盘。"
        : "（提示：尚未完成百度网盘授权，文件暂存在本机，授权后会自动补传。）";
}

/// <summary>
/// 在本地元数据里检索文件（链路 D 的入口，只读）。
/// **纯离线**：不联网、不消耗网盘额度 —— 这正是「本地当工作区」的价值所在。
/// </summary>
public class FindCloudFilesTool : AgentTool
{
    public override string Name => "find_cloud_files";

    public override string Description =>
        "在用户的网盘文件记录里检索（本地离线查询，不联网、不消耗额度）。" +
        "用户说「把我上周存的那份文档找出来」「网盘里有没有关于 XX 的文件」时使用。" +
        "返回结果每行开头的中括号里是编号，后续用 fetch_cloud_file / read_cloud_file 时把它作为 file_id 传入。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"keyword":{"type":"string","description":"文件名或标签关键词，模糊匹配"},"type":{"type":"string","description":"可选：artifact=AI产出 / upload=用户上传 / attachment=对话附件；也可填 图片 / 文档"},"tag":{"type":"string","description":"可选，按标签过滤"},"recent_days":{"type":"number","description":"可选，最近 N 天内新增（用户说「上周」填 7，「最近三天」填 3）"},"from_date":{"type":"string","description":"可选，起始日期 yyyy-MM-dd"},"to_date":{"type":"string","description":"可选，结束日期 yyyy-MM-dd"},"limit":{"type":"number","description":"可选，最多返回几条，默认 20"}},"required":[]}""";

    public override bool IsReadOnly => true;

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        ToolArgs.TryGetString(argumentsJson, "keyword", out var keyword);
        ToolArgs.TryGetString(argumentsJson, "type", out var type);
        ToolArgs.TryGetString(argumentsJson, "tag", out var tag);

        var query = new FileQuery
        {
            Keyword = keyword.Length > 0 ? keyword : null,
            Type = type.Length > 0 ? type : null,
            Tag = tag.Length > 0 ? tag : null,
            Limit = ToolArgs.TryGetInt(argumentsJson, "limit", out var lim) && lim > 0 ? Math.Min(lim, 100) : 20,
        };
        if (ToolArgs.TryGetInt(argumentsJson, "recent_days", out var days) && days > 0) query.RecentDays = days;
        if (ToolArgs.TryGetString(argumentsJson, "from_date", out var from) && DateTime.TryParse(from, out var fd)) query.From = fd;
        if (ToolArgs.TryGetString(argumentsJson, "to_date", out var to) && DateTime.TryParse(to, out var td)) query.To = td;

        var hits = FileRepository.Search(query);
        if (hits.Count == 0)
        {
            // 元数据清单还在从云端同步时**绝不能下「不存在」的结论** —— 那是把「还没同步」误报成「被删了」，
            // 用户会以为文件丢了。宁可让模型多说一句「稍后再试」。
            if (!FileStoreSync.InitialPullCompleted)
                return Task.FromResult("文件清单正在从云端同步中，暂时还不能下结论。请让用户稍等片刻再问一次。");

            var scope = keyword.Length > 0 ? $"包含「{keyword}」的" : "";
            return Task.FromResult($"没有找到{scope}文件记录。用户可以点对话框里的「选择文件」把本机文件存到网盘。");
        }

        var lines = hits.Select(FileToolSupport.Describe);
        return Task.FromResult($"共 {hits.Count} 个文件：\n" + string.Join("\n", lines)
            + "\n\n（要取回某个文件给用户，用 fetch_cloud_file 传它的编号；要读内容自己用，用 read_cloud_file。）");
    }
}

/// <summary>
/// 从网盘取回文件并交付给用户（链路 D 的出口，写操作需确认 —— 会下载数据、占用本地空间）。
/// </summary>
public class FetchCloudFileTool : AgentTool
{
    public override string Name => "fetch_cloud_file";

    public override string Description =>
        "把网盘里的某个文件取回本机，并在对话里给用户一张可操作的文件卡片（可打开 / 在文件夹中定位 / 另存为）。" +
        "用户说「把那份文档给我」「取回来看看」时使用。file_id 用 find_cloud_files 返回的编号。" +
        "首次取回需要从网盘下载，速度受用户网盘账号等级影响，可能需要等待。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"file_id":{"type":"string","description":"find_cloud_files 返回的编号（中括号里的字符串）"},"action":{"type":"string","description":"可选：open=取回后直接打开，locate=取回后在资源管理器中定位；不传则只给卡片"}},"required":["file_id"]}""";

    public override bool IsReadOnly => false;

    public override string DescribeAction(string argumentsJson)
    {
        ToolArgs.TryGetString(argumentsJson, "file_id", out var id);
        var (meta, _) = FileToolSupport.ResolveFileRef(id);
        return meta == null
            ? $"从网盘取回文件（编号 {id}）"
            : $"从网盘取回「{meta.Name}」（{RootMigrationService.FormatSize(meta.Size)}）";
    }

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "file_id", out var fileId))
            return "错误：缺少参数 file_id（用 find_cloud_files 返回的编号）。";

        var (meta, error) = FileToolSupport.ResolveFileRef(fileId);
        if (meta == null) return "错误：" + error;

        var (path, dlError) = await FileRepository.EnsureLocalAsync(meta.Id, null, ct).ConfigureAwait(false);
        if (path == null) return "错误：" + dlError;

        FileDeliveryHub.Deliver(meta);

        ToolArgs.TryGetString(argumentsJson, "action", out var action);
        if (string.Equals(action, "open", StringComparison.OrdinalIgnoreCase))
            FileDeliveryHub.RequestOpen(meta);
        else if (string.Equals(action, "locate", StringComparison.OrdinalIgnoreCase))
            FileDeliveryHub.RequestLocate(meta);

        return $"已取回「{meta.Name}」（{RootMigrationService.FormatSize(meta.Size)}），已在本机：" +
               $"{path}。对话里已给出文件卡片，用户可以打开、在文件夹中定位或另存为。";
    }
}

/// <summary>
/// 读取网盘文件的内容供自己使用（链路 E，只读）。本期只支持文本类（Markdown / 文本 / 代码）。
/// </summary>
public class ReadCloudFileTool : AgentTool
{
    public override string Name => "read_cloud_file";

    public override string Description =>
        "读取网盘里某个文本文件的内容，用于总结、翻译、分析。" +
        "仅支持文本类（md / txt / 代码 / csv / json 等）；图片、Word、PDF 读不了 —— " +
        "遇到这类文件请用 fetch_cloud_file 取回给用户自己打开，不要假装读到了内容。";

    public override string ParametersJson =>
        """{"type":"object","properties":{"file_id":{"type":"string","description":"find_cloud_files 返回的编号"},"max_chars":{"type":"number","description":"可选，最多读取多少字，默认 20000"}},"required":["file_id"]}""";

    public override bool IsReadOnly => true;

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!ToolArgs.TryGetString(argumentsJson, "file_id", out var fileId))
            return "错误：缺少参数 file_id。";

        var (meta, error) = FileToolSupport.ResolveFileRef(fileId);
        if (meta == null) return "错误：" + error;

        var maxChars = ToolArgs.TryGetInt(argumentsJson, "max_chars", out var mc) && mc > 0
            ? Math.Min(mc, 60000)
            : 20000;

        var (text, readError) = await FileRepository.ReadTextAsync(meta.Id, maxChars, ct).ConfigureAwait(false);
        if (text == null) return "错误：" + readError;

        return $"文件「{meta.Name}」的内容如下：\n\n{text}";
    }
}
