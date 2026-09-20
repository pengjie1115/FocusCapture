using System.Threading;

namespace FocusCapture.Services.Skills;

/// <summary>子进程调用结果（宿主只搬运事实，不下结论）</summary>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, string? StartError)
{
    /// <summary>启动成功、没超时、退出码 0 —— 三者都满足才算正常返回</summary>
    public bool Ok => StartError == null && !TimedOut && ExitCode == 0;

    public string Combined => (Stdout + "\n" + Stderr).Trim();
}

/// <summary>
/// Skill 子进程的统一装配（2026-09-20）。
///
/// <para>
/// <b>为什么单独抽出来：</b>执行器（跑 Skill 脚本）与「外部依赖」层（驱动 lark-cli 授权）
/// 都要起子进程，两处各写一套的代价已经见过了 —— 执行器当初在
/// <c>RedirectStandardInput = false</c> 时设了 <c>StandardInputEncoding</c>，
/// <c>Process.Start</c> 直接抛，8 条检查点全红，而错误文案还把它误报成"运行时缺失"。
/// 那份经验固化在这里，只有一处会错。
/// </para>
/// <para>
/// <b>环境纪律（三条都不由调用方决定）：</b>
/// ① 清掉宿主标记变量（否则子工具会跑到别的宿主空间，比如 lark-cli 串到 WorkBuddy 的账号）；
/// ② 输出按 UTF-8 解码（本机不注入也正常，但那是环境巧合，不能当普遍事实）；
/// ③ 可以往 PATH 前面塞目录（让子进程里 <c>shutil.which("lark-cli")</c> 命中应用自带的那份）。
/// </para>
/// </summary>
internal static class SkillProcess
{
    /// <summary>会被清掉的环境变量：它们代表"宿主身份"，留着会让子工具跑到别的宿主空间去</summary>
    internal static readonly string[] HostMarkerVars = { "PYTHONPATH", "HERMES_HOME", "OPENCLAW_HOME", "LARK_CHANNEL" };

    /// <summary>
    /// 组装一个子进程启动信息。
    /// </summary>
    /// <param name="exePath">可执行文件绝对路径（调用方负责确保它是查表得到的，不拼模型给的字符串）</param>
    /// <param name="args">参数数组 —— 绝不拼命令行字符串</param>
    /// <param name="workingDir">工作目录（脚本=Skill 根目录；lark-cli 授权=临时目录，因为它的 -o 只收相对路径）</param>
    /// <param name="redirectStdin">是否喂标准输入</param>
    /// <param name="prependPath">前置到 PATH 的目录（可为空）</param>
    /// <param name="pythonFlags">是否注入 Python 行为控制项（只有跑 Python 脚本时才需要）</param>
    internal static ProcessStartInfo Build(
        string exePath,
        IEnumerable<string> args,
        string workingDir,
        bool redirectStdin,
        string? prependPath = null,
        bool pythonFlags = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workingDir,
            UseShellExecute = false,          // 必须 false，否则不能重定向、也不能自定义环境
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdin,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            // ⚠ StandardInputEncoding 只能在 RedirectStandardInput = true 时设置，
            // 否则 Process.Start 抛「only supported when standard input is redirected」（实测踩过）。
        };
        if (redirectStdin) psi.StandardInputEncoding = new UTF8Encoding(false);

        foreach (var a in args) psi.ArgumentList.Add(a ?? "");

        if (pythonFlags)
        {
            psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";   // 不往应用目录写 __pycache__
            psi.Environment["PYTHONIOENCODING"] = "utf-8";      // 输出编码确定，不随机器 locale 漂
        }
        foreach (var v in HostMarkerVars) psi.Environment.Remove(v);

        if (!string.IsNullOrEmpty(prependPath))
        {
            var current = FindEnv(psi, "PATH");
            psi.Environment["PATH"] = string.IsNullOrEmpty(current)
                ? prependPath
                : prependPath + Path.PathSeparator + current;
        }

        return psi;
    }

    /// <summary>大小写不敏感地取一个环境变量（Windows 上 PATH/Path 混写很常见）</summary>
    private static string? FindEnv(ProcessStartInfo psi, string name)
    {
        foreach (var kv in psi.Environment)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    /// <summary>
    /// 跑完一个子进程。**永不抛** —— 启动失败 / 超时都通过 <see cref="ProcessResult"/> 返回，
    /// 让调用方自己决定怎么措辞（措辞是要按场景区分的，这也是当初"参数写错被误报成环境缺失"的教训）。
    /// </summary>
    internal static async Task<ProcessResult> RunAsync(
        ProcessStartInfo psi,
        string? stdin,
        int timeoutMs,
        CancellationToken ct = default)
    {
        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", "", false, ex.Message);
        }
        if (proc == null) return new ProcessResult(-1, "", "", false, "Process.Start 返回空");

        using (proc)
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (stdin != null && psi.RedirectStandardInput)
            {
                try
                {
                    await proc.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                    proc.StandardInput.Close();
                }
                catch { /* 尽力而为：写不进去不影响读结果 */ }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs > 0 ? timeoutMs : 1);
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillTree(proc);
                return new ProcessResult(-1, "", "", true, null);
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new ProcessResult(proc.ExitCode, stdout, stderr, false, null);
        }
    }

    /// <summary>杀整棵进程树（脚本可能又起了子进程，比如 kb.py 会拉起 lark-cli）</summary>
    internal static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch { /* 尽力而为 */ }
    }
}
