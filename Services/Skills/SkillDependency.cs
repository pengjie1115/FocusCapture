using System.Threading;

namespace FocusCapture.Services.Skills;

/// <summary>外部依赖的登录态（把 CLI 的原始状态词归一成宿主自己的语义）</summary>
public enum DependencyAuth
{
    /// <summary>没探测出来（命令没跑起来 / 输出看不懂）—— 按"未知"处理，绝不乐观地当成已授权</summary>
    Unknown,

    /// <summary>可执行文件在位，但用户身份没有 token</summary>
    Missing,

    /// <summary>有 token 但已失效</summary>
    Expired,

    /// <summary>可用</summary>
    Ready,
}

/// <summary>一次探测的结论（只描述事实，措辞由调用方决定 —— UI 含糊但日志要精确）</summary>
public sealed record DependencyStatus(
    string Id,
    string DisplayName,
    bool Resolved,
    string? ExePath,
    DependencyAuth Auth,
    string Account,
    string Detail)
{
    /// <summary>在位但登录态不是 ready —— 这时候授权是有意义的动作</summary>
    public bool NeedsAuth => Resolved && Auth != DependencyAuth.Ready;

    /// <summary>能不能做授权（文件不在就没得授权，只能先解决"没有它"这件事）</summary>
    public bool CanAuthorize => Resolved;
}

/// <summary>设备码一次会话（<c>auth login --no-wait --json</c> 的返回）</summary>
public sealed record DeviceCodeSession(string DeviceCode, string VerificationUrl, int ExpiresInSeconds);

/// <summary>
/// 一个外部依赖：某个外部可执行程序 + 它的登录态 + 授权方式（2026-09-20）。
///
/// <para>
/// <b>为什么需要这一层：</b>Skill 的脚本常常要调外部 CLI（写飞书要 lark-cli、发 GitHub 要 gh…），
/// 而这些 CLI 大多需要"先登录"。阶段一交付时这块是**空的** —— 结果是模型自己发挥了：
/// 它照着 CLI 输出里的提示，让用户去终端敲 <c>lark-cli auth login</c>，还把某个应用安装目录的
/// 绝对路径当命令写给了用户。**这是宿主该做的事被外包给了用户**，不是措辞问题。
/// </para>
/// <para>
/// <b>定位顺序（刻意的）：</b>① 应用自带的 <c>&lt;应用目录&gt;\runtime\&lt;id&gt;\</c> —— 确定、可随包分发；
/// ② 系统 PATH —— 兼容用户自己装过的情况。**不认识"别的应用安装目录里恰好有一份"**：
/// 那既不可移植，也让宿主的行为取决于别人装了什么。
/// </para>
/// <para>
/// <b>授权走标准设备码流</b>（三步，全部由宿主驱动，用户只扫一次码）：
/// <c>auth login --no-wait --json</c> 拿 device_code + verification_url →
/// <c>auth qrcode</c> 生成二维码图片 → 用户扫码并在飞书里点确认 →
/// <c>auth login --device-code</c> 领回 token。全程不出应用、用户不碰命令行。
/// </para>
/// </summary>
public class SkillDependency
{
    /// <summary>授权类命令的超时（扫码要人操作，不能用执行脚本那套 150 秒）</summary>
    private const int QuickCommandTimeoutMs = 30_000;

    private readonly string? _bundledDir;

    /// <param name="id">稳定标识，同时也是自带目录名（<c>runtime\&lt;id&gt;\</c>）</param>
    /// <param name="displayName">给用户看的名字</param>
    /// <param name="exeName">可执行文件名（不含扩展名）</param>
    /// <param name="scriptMarkers">静态扫描脚本时用来判断"这个 Skill 用到了它"的关键词</param>
    /// <param name="baseDir">应用目录（<c>AppContext.BaseDirectory</c>）</param>
    public SkillDependency(
        string id,
        string displayName,
        string exeName,
        IReadOnlyList<string> scriptMarkers,
        string? baseDir)
    {
        Id = id;
        DisplayName = displayName;
        ExeName = exeName;
        ScriptMarkers = scriptMarkers;
        _bundledDir = string.IsNullOrEmpty(baseDir) ? null : Path.Combine(baseDir, "runtime", id);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string ExeName { get; }
    public IReadOnlyList<string> ScriptMarkers { get; }

    /// <summary>授权时申请的权限域（飞书概念：base=多维表格，docs=文档，drive=云盘，sheets=表格，wiki=知识库）</summary>
    public virtual IReadOnlyList<string> AuthDomains => Array.Empty<string>();

    /// <summary>给用户看的权限域说明（透明优先：让他在扫码前知道要批什么）</summary>
    public virtual string AuthScopeText => "";

    /// <summary>自带目录（执行器会把它前置到子进程 PATH，让脚本里的 which 命中我们的那份）</summary>
    public string? BundledDir => _bundledDir;

    /// <summary>确认这个依赖被某段脚本文本用到（大小写不敏感的朴素匹配，宁可多判不可漏判）</summary>
    public bool Matches(string scriptText)
    {
        if (string.IsNullOrEmpty(scriptText)) return false;
        foreach (var m in ScriptMarkers)
            if (scriptText.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>定位可执行文件：应用自带优先，其次 PATH。找不到返回 null（**不抛**）</summary>
    public string? Locate()
    {
        if (_bundledDir != null)
        {
            var bundled = Path.Combine(_bundledDir, ExeName + ".exe");
            if (File.Exists(bundled)) return bundled;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), ExeName + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* PATH 里有畸形项是常态，跳过 */ }
        }
        return null;
    }

    /// <summary>
    /// 探测登录态。**永不抛** —— 探测不出来就是 <see cref="DependencyAuth.Unknown"/>，
    /// 绝不因为"读不懂输出"而当成已授权或未授权。
    /// </summary>
    public virtual async Task<DependencyStatus> ProbeAsync(CancellationToken ct = default)
    {
        var exe = Locate();
        if (exe == null)
            return new DependencyStatus(Id, DisplayName, false, null, DependencyAuth.Unknown, "",
                $"未找到 {ExeName}（应用未自带、系统 PATH 里也没有）");

        var psi = SkillProcess.Build(exe, new[] { "auth", "status" }, Path.GetDirectoryName(exe) ?? "", false);
        var r = await SkillProcess.RunAsync(psi, null, QuickCommandTimeoutMs, ct).ConfigureAwait(false);

        if (!r.Ok)
            return new DependencyStatus(Id, DisplayName, true, exe, DependencyAuth.Unknown, "",
                r.TimedOut ? "状态查询超时" : $"状态查询失败（退出码 {r.ExitCode}）：{Short(r.Combined)}");

        return ParseStatus(exe, r.Stdout);
    }

    /// <summary>
    /// 解析 <c>auth status</c> 的 JSON。
    ///
    /// <para>
    /// 判据是 <c>identities.user.available</c> / <c>identities.user.status</c> ——
    /// **不能看退出码**：实测用户身份缺失时它照样返回 0，只看退出码会得出"一切正常"。
    /// </para>
    /// <para>抽成 public 是为了让检查点能直接喂畸形 JSON（这一段全是解析，正是最容易出错的地方）。</para>
    /// </summary>
    public DependencyStatus ParseStatus(string? exe, string json)
    {
        var status = DependencyAuth.Unknown;
        var account = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("identities", out var ids) && ids.TryGetProperty("user", out var user))
            {
                account = user.TryGetProperty("userName", out var n) ? (n.GetString() ?? "") : "";
                var available = user.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True;
                var word = user.TryGetProperty("status", out var s) ? (s.GetString() ?? "") : "";
                status = available || string.Equals(word, "ready", StringComparison.OrdinalIgnoreCase)
                    ? DependencyAuth.Ready
                    : string.Equals(word, "expired", StringComparison.OrdinalIgnoreCase)
                        ? DependencyAuth.Expired
                        : DependencyAuth.Missing;
            }
        }
        catch (JsonException)
        {
            return new DependencyStatus(Id, DisplayName, true, exe, DependencyAuth.Unknown, "",
                $"状态输出不是合法 JSON：{Short(json)}");
        }

        var detail = status switch
        {
            DependencyAuth.Ready => account.Length > 0 ? $"已授权：{account}" : "已授权",
            DependencyAuth.Expired => "授权已过期，需要重新授权",
            DependencyAuth.Missing => "尚未授权",
            _ => "状态未知",
        };
        return new DependencyStatus(Id, DisplayName, true, exe, status, account, detail);
    }

    /// <summary>
    /// 发起设备码授权：返回 device_code 与验证链接（<c>--no-wait</c>，不阻塞）。
    /// </summary>
    public virtual async Task<(bool Ok, string Message, DeviceCodeSession? Session)> StartAuthAsync(CancellationToken ct = default)
    {
        var exe = Locate();
        if (exe == null) return (false, $"未找到 {ExeName}，无法发起授权。", null);

        var args = new List<string> { "auth", "login", "--no-wait", "--json" };
        if (AuthDomains.Count > 0)
        {
            args.Add("--domain");
            args.Add(string.Join(",", AuthDomains));
        }

        var psi = SkillProcess.Build(exe, args, Path.GetDirectoryName(exe) ?? "", false);
        var r = await SkillProcess.RunAsync(psi, null, QuickCommandTimeoutMs, ct).ConfigureAwait(false);
        if (!r.Ok) return (false, r.TimedOut ? "发起授权超时。" : $"发起授权失败：{Short(r.Combined)}", null);

        try
        {
            using var doc = JsonDocument.Parse(r.Stdout);
            var root = doc.RootElement;
            var code = root.TryGetProperty("device_code", out var c) ? c.GetString() : null;
            var url = root.TryGetProperty("verification_url", out var u) ? u.GetString() : null;
            var expires = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var sec) ? sec : 600;

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(url))
                return (false, $"授权响应缺少必要字段：{Short(r.Stdout)}", null);

            return (true, "", new DeviceCodeSession(code!, url!, expires));
        }
        catch (JsonException)
        {
            return (false, $"授权响应不是合法 JSON：{Short(r.Stdout)}", null);
        }
    }

    /// <summary>
    /// 把验证链接转成二维码 PNG，返回图片绝对路径（失败返回 null）。
    ///
    /// <para>
    /// ⚠ <b>统一用「相对名 + 调用方指定的 CWD」</b>，因为这里踩过一个版本差异（都实测过）：
    /// CLI <b>1.0.92</b> 拒绝绝对输出路径
    /// （<c>"unsafe output path: --output must be a relative path within the current directory"</c>，退出码 2），
    /// 而 <b>1.0.96</b> 接受绝对路径。相对名在两个版本上都成立 —— 选它是为了不依赖具体版本。
    /// </para>
    /// </summary>
    public virtual async Task<string?> MakeQrPngAsync(string verificationUrl, string workDir, CancellationToken ct = default)
    {
        var exe = Locate();
        if (exe == null) return null;

        const string fileName = "qr.png";
        var args = new[] { "auth", "qrcode", verificationUrl, "-o", "./" + fileName, "--size", "360" };
        var psi = SkillProcess.Build(exe, args, workDir, false);
        var r = await SkillProcess.RunAsync(psi, null, QuickCommandTimeoutMs, ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            AppLog.Warn("Skill", $"{Id} 二维码生成失败：exit={r.ExitCode} {Short(r.Combined)}");
            return null;
        }

        var png = Path.Combine(workDir, fileName);
        // 产物优先：不信它输出的 JSON，只认文件真的在
        return File.Exists(png) ? png : null;
    }

    /// <summary>
    /// 用 device_code 领回 token（这一步会阻塞轮询，直到用户扫码确认或超时）。
    /// </summary>
    public virtual async Task<(bool Ok, string Message)> CompleteAuthAsync(
        string deviceCode, int timeoutMs, CancellationToken ct = default)
    {
        var exe = Locate();
        if (exe == null) return (false, $"未找到 {ExeName}。");

        var args = new[] { "auth", "login", "--device-code", deviceCode };
        var psi = SkillProcess.Build(exe, args, Path.GetDirectoryName(exe) ?? "", false);
        var r = await SkillProcess.RunAsync(psi, null, timeoutMs, ct).ConfigureAwait(false);

        if (r.TimedOut) return (false, "等待扫码超时（授权码已过期），请重新发起。");
        if (!r.Ok) return (false, $"授权未完成：{Short(r.Combined)}");
        return (true, "已授权");
    }

    /// <summary>撤销授权（登出）。设置页的撤销入口靠它 —— 只能授权不能撤销的安全机制是残缺的。</summary>
    public virtual async Task<(bool Ok, string Message)> LogoutAsync(CancellationToken ct = default)
    {
        var exe = Locate();
        if (exe == null) return (false, $"未找到 {ExeName}。");

        var psi = SkillProcess.Build(exe, new[] { "auth", "logout", "--json" }, Path.GetDirectoryName(exe) ?? "", false);
        var r = await SkillProcess.RunAsync(psi, null, QuickCommandTimeoutMs, ct).ConfigureAwait(false);
        return r.Ok ? (true, "已退出登录") : (false, $"退出登录失败：{Short(r.Combined)}");
    }

    private static string Short(string? s)
    {
        var t = (s ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
        return t.Length <= 200 ? t : t[..200] + "…";
    }
}

/// <summary>
/// 依赖表：**一个依赖一项配置，机制不绑定任何一个具体 CLI**（当前只有飞书一项，因为 Skill 生态里
/// 它是第一个需要登录态的）。加 <c>gh</c> / <c>ffmpeg</c> 这类只需"在位"的依赖时，
/// 走同一个类、把授权相关方法留空即可。
/// </summary>
public static class SkillDependencies
{
    /// <summary>飞书 CLI 的依赖标识（同时也是自带目录名 <c>runtime\lark-cli\</c>）</summary>
    public const string LarkCliId = "lark-cli";

    /// <summary>
    /// 全部已知依赖。<paramref name="baseDir"/> 传应用目录。
    ///
    /// <para>
    /// 飞书这项申请的权限域是<b>判断而非权威依据</b>：取自 Skill 生态里最常见的写入目标
    /// （多维表格 / 文档 / 云盘 / 电子表格 / 知识库）。窗口会把这份清单原文显示给用户，
    /// 让他扫码前就知道要批什么；要收窄或扩大只改这一处。
    /// </para>
    /// </summary>
    public static IReadOnlyList<SkillDependency> All(string? baseDir) => new[]
    {
        new LarkCliDependency(baseDir),
    };

    /// <summary>从脚本文本里找出该 Skill 用到的依赖（用来做"跑之前先看缺什么"的预检）</summary>
    public static IReadOnlyList<SkillDependency> DetectIn(
        IReadOnlyList<SkillDependency> all, IEnumerable<string> scriptTexts)
    {
        var hit = new List<SkillDependency>();
        foreach (var dep in all)
        {
            foreach (var text in scriptTexts)
            {
                if (dep.Matches(text) && !hit.Contains(dep)) { hit.Add(dep); break; }
            }
        }
        return hit;
    }

    /// <summary>
    /// 读一个 Skill 的脚本文本（依赖静态扫描用）。**读不动就跳过、绝不抛** ——
    /// 漏检只意味着少一次预检，不该影响别的任何事。
    /// </summary>
    public static List<string> ReadScriptTexts(SkillInfo skill, int maxCharsPerFile = 200_000)
    {
        var texts = new List<string>();
        if (skill == null) return texts;
        foreach (var name in skill.ScriptFiles)
        {
            try
            {
                var text = File.ReadAllText(Path.Combine(skill.RootPath, "scripts", name));
                texts.Add(text.Length > maxCharsPerFile ? text[..maxCharsPerFile] : text);
            }
            catch { /* 跳过 */ }
        }
        return texts;
    }

    /// <summary>一个 Skill 用到了哪些已注册依赖（执行器预检与 load_skill 说明共用同一套判据）</summary>
    public static IReadOnlyList<SkillDependency> DetectInSkill(
        IReadOnlyList<SkillDependency> all, SkillInfo skill) =>
        DetectIn(all, ReadScriptTexts(skill));
}

/// <summary>飞书 CLI 的具体接线（命令名与权限域放在这里，机制在 <see cref="SkillDependency"/>）</summary>
internal sealed class LarkCliDependency : SkillDependency
{
    public LarkCliDependency(string? baseDir)
        : base(
            id: SkillDependencies.LarkCliId,
            displayName: "飞书（lark-cli）",
            exeName: "lark-cli",
            scriptMarkers: new[] { "lark-cli", "lark_cli", "larkcli" },
            baseDir: baseDir)
    {
    }

    public override IReadOnlyList<string> AuthDomains { get; } =
        new[] { "base", "docs", "drive", "sheets", "wiki" };

    public override string AuthScopeText => "多维表格 / 文档 / 云盘 / 电子表格 / 知识库";
}
