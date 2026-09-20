using System.Threading;

namespace FocusCapture.Services.Skills;

/// <summary>
/// Skill 脚本执行器（2026-09-20，Skill 运行时阶段一）。
///
/// <para>
/// <b>这是整个方案里唯一真正危险的地方</b> —— 它会在用户电脑上跑第三方脚本。
/// 所以九条硬规则全部写死在代码里，**不由模型决定、也不做成可配置项**：
/// </para>
/// <list type="number">
/// <item>Skill 名只在已扫描目录表里查，取表中预存的绝对路径 —— 绝不把模型传的字符串拼进路径</item>
/// <item>脚本解析后必须仍在 <c>&lt;skill&gt;\scripts\</c> 内（<c>..</c> / 分隔符 / 绝对路径一律拒）</item>
/// <item>解释器白名单：只认 <c>.py</c></item>
/// <item>参数用 <c>ArgumentList</c> 数组传，不拼命令行字符串</item>
/// <item>超时 150 秒，到点杀进程树（必须大于脚本内部超时：kb.py 自己就设了 90 秒）</item>
/// <item>输出截断（保留头尾）</item>
/// <item>CWD 固定为该 Skill 根目录</item>
/// <item>环境变量：注入 Python 行为控制项，清掉宿主标记（防串号）</item>
/// <item>首次执行某 Skill 必须用户确认一次，之后记住</item>
/// </list>
/// <para>
/// <b>防线在"准入"而不在"运行时拦截"</b>：脚本一旦跑起来，它读什么文件、连什么网，宿主管不了 ——
/// 这是"允许执行任意脚本"的固有代价，不是本类的缺陷。所以第 9 条（准入确认 + 可撤销）才是地基。
/// </para>
/// </summary>
public sealed class SkillScriptRunner
{
    /// <summary>
    /// 默认超时。**必须大于脚本内部超时** —— kb.py 自己就设了 90 秒，两个 90 秒叠一起会误杀正常脚本。
    /// 检查点注入更小的值来测超时路径；生产一律用这个默认值。
    /// </summary>
    public const int DefaultTimeoutMs = 150_000;

    private const int OutputMaxChars = 4000;

    private readonly int _timeoutMs;

    private static readonly string[] AllowedExtensions = { ".py" };

    /// <summary>会被清掉的环境变量：它们代表"宿主身份"，留着会让子工具行为跑到别的宿主空间去</summary>
    private static readonly string[] HostMarkerVars =
    {
        "PYTHONPATH", "HERMES_HOME", "OPENCLAW_HOME", "LARK_CHANNEL",
    };

    private readonly SkillCatalog _catalog;
    private readonly SkillRuntime _runtime;

    /// <param name="timeoutMs">执行超时；默认 <see cref="DefaultTimeoutMs"/>。
    /// 生产不要改它 —— 检查点用更小的值来验证"到点确实杀得掉"。</param>
    public SkillScriptRunner(SkillCatalog catalog, SkillRuntime runtime, int timeoutMs = DefaultTimeoutMs)
    {
        _catalog = catalog;
        _runtime = runtime;
        _timeoutMs = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
    }

    /// <summary>
    /// 准入确认回调：某 Skill 首次要跑脚本时问用户一次。返回 true = 允许（并记住）。
    /// 未设置时一律拒绝 —— 宁可功能不可用，也不静默放行。
    /// </summary>
    public Func<SkillInfo, Task<bool>>? TrustPrompt { get; set; }

    /// <summary>已授权的 Skill 名（大小写不敏感）。持久化由调用方负责。</summary>
    public HashSet<string> TrustedSkills { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>刚刚授权某个 Skill 时回调，调用方据此写配置</summary>
    public Action<string>? OnTrusted { get; set; }

    /// <summary>
    /// 执行一个 Skill 脚本。返回**给模型读的固定三段文本**（退出码 + stdout + stderr），
    /// 宿主**不做成败判定** —— 判定交给模型，避免宿主替模型"宣布成功"。
    /// </summary>
    public async Task<string> RunAsync(
        string? skillName,
        string? scriptFile,
        IReadOnlyList<string>? args,
        string? stdin,
        CancellationToken ct)
    {
        // ── 规则 1：查表，不拼路径 ──
        if (!_catalog.TryGet(skillName, out var skill))
            return $"错误：不存在名为「{skillName}」的 Skill。可用 Skill 见系统提示中的清单。";

        // ── 规则 2/3：脚本名与扩展名校验 ──
        var file = (scriptFile ?? "").Trim();
        if (file.Length == 0) return "错误：缺少参数 script。";
        if (file.Contains("..") || file.Contains('\\') || file.Contains('/') ||
            !string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal))
            return "错误：script 只能是文件名，不能包含路径分隔符或 ..";

        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return $"错误：不支持的脚本类型「{ext}」。当前只允许：{string.Join("、", AllowedExtensions)}。";

        // 解析后再校验一次越界（双保险：即使上面的字符检查被绕过）
        var scriptsDir = Path.GetFullPath(Path.Combine(skill.RootPath, "scripts"));
        var scriptPath = Path.GetFullPath(Path.Combine(scriptsDir, file));
        var prefix = scriptsDir.EndsWith(Path.DirectorySeparatorChar)
            ? scriptsDir
            : scriptsDir + Path.DirectorySeparatorChar;

        if (!scriptPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return "错误：脚本路径越界 —— 只允许执行该 Skill 的 scripts 目录内的文件。";

        if (!File.Exists(scriptPath))
            return $"错误：脚本不存在：scripts\\{file}（该 Skill 的脚本：{DescribeScripts(skill)}）。";

        // ── 规则 9：准入确认 ──
        if (!TrustedSkills.Contains(skill.Name))
        {
            if (TrustPrompt == null)
                return $"错误：Skill「{skill.Name}」尚未被授权执行脚本，且当前无法弹出确认。请在「设置 → Skill」中查看。";

            var allowed = await TrustPrompt(skill).ConfigureAwait(false);
            if (!allowed)
                return "用户已取消该操作。请停止此动作，不要重复尝试。";

            TrustedSkills.Add(skill.Name);
            OnTrusted?.Invoke(skill.Name);
        }

        // ── 规则 8 的一半：运行时体检（文件在 ≠ 能跑）──
        var (runtimeOk, runtimeMsg) = await _runtime.ProbeAsync(ct).ConfigureAwait(false);
        if (!runtimeOk)
        {
            // 措辞纪律：这里**绝不能**出现"已完成/已执行/成功"之类的字样
            return $"错误：Skill「{skill.Name}」无法执行 —— {runtimeMsg}\n{_runtime.MissingHint}";
        }

        // ── 组装进程 ──
        var psi = new ProcessStartInfo
        {
            FileName = _runtime.PythonPath,
            WorkingDirectory = skill.RootPath,          // 规则 7
            UseShellExecute = false,                    // 必须 false 才能重定向与自定义环境
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            // 注意：StandardInputEncoding 只能在 RedirectStandardInput = true 时设置，
            // 否则 Process.Start 抛「StandardInputEncoding is only supported when standard
            // input is redirected」（2026-09-20 被慢层检查点抓到）。所以按下需要单独设。
        };
        if (stdin != null) psi.StandardInputEncoding = new UTF8Encoding(false);

        psi.ArgumentList.Add(scriptPath);               // 规则 4：数组传参，不经 shell
        if (args != null)
        {
            foreach (var a in args)
                psi.ArgumentList.Add(a ?? "");
        }

        // ── 规则 8：环境变量 ──
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";   // 不往应用目录写 __pycache__
        psi.Environment["PYTHONIOENCODING"] = "utf-8";      // 输出编码确定，不随机器 locale 漂
        foreach (var v in HostMarkerVars)
            psi.Environment.Remove(v);                      // 清宿主标记，防串号

        AppLog.Info("Skill", $"执行 {skill.Name}\\scripts\\{file}，参数 {args?.Count ?? 0} 个" +
                             (stdin != null ? $"，stdin {stdin.Length} 字符" : ""));

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppLog.Error("Skill", $"启动脚本进程失败：{skill.Name}\\{file}", ex);
            // 只有解释器确实不在时，才提示"去装运行时" —— 否则会把"参数/配置写错"误导成"环境缺失"。
            // （2026-09-20：检查点里真的踩到过这个误导 —— StandardInputEncoding 配错，提示却说运行时没装。）
            var hint = File.Exists(_runtime.PythonPath) ? "" : "\n" + _runtime.MissingHint;
            return $"错误：无法启动脚本进程 —— {ex.Message}{hint}";
        }

        if (proc == null) return "错误：脚本进程未能创建（Process.Start 返回空）。";

        using (proc)
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (stdin != null)
            {
                try
                {
                    await proc.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                    proc.StandardInput.Close();
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Skill", $"写入 stdin 失败：{ex.Message}");
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeoutMs);

            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillTree(proc);
                AppLog.Warn("Skill", $"脚本超时被杀：{skill.Name}\\{file}");
                return $"错误：脚本执行超时（{_timeoutMs / 1000} 秒），进程已被终止。\n" +
                       "若脚本本身就该跑很久，请让它在 Skill 说明里改成分批调用。";
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            AppLog.Info("Skill", $"脚本退出：{skill.Name}\\{file}，exit={proc.ExitCode}，" +
                                 $"stdout {stdout.Length} 字符，stderr {stderr.Length} 字符");

            return FormatResult(proc.ExitCode, stdout, stderr);
        }
    }

    /// <summary>杀整棵进程树：脚本可能自己又起了子进程（kb.py 就会拉起 lark-cli）</summary>
    private static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Skill", $"终止脚本进程失败：{ex.Message}");
        }
    }

    /// <summary>固定三段输出：退出码 + stdout + stderr。宿主只搬运事实，不下结论。</summary>
    private static string FormatResult(int exitCode, string stdout, string stderr)
    {
        var sb = new StringBuilder();
        sb.Append("EXIT=").Append(exitCode).Append('\n');
        sb.Append("--- stdout ---\n").Append(Truncate(stdout.TrimEnd(), OutputMaxChars)).Append('\n');
        sb.Append("--- stderr ---\n").Append(stderr.Trim().Length == 0 ? "（空）" : Truncate(stderr.TrimEnd(), OutputMaxChars));
        return sb.ToString();
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        var head = max * 2 / 3;
        var tail = max - head;
        return s[..head] + $"\n…（中间省略 {s.Length - max} 字符）…\n" + s[^tail..];
    }

    private static string DescribeScripts(SkillInfo skill) =>
        skill.ScriptFiles.Count == 0 ? "（无）" : string.Join("、", skill.ScriptFiles);
}
