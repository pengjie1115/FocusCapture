using System.Threading;

namespace FocusCapture.Services.Skills;

/// <summary>
/// 内置 Python 运行时的定位与体检（2026-09-20，Skill 运行时阶段一）。
///
/// <para>
/// 运行时由 <c>tools\fetch-python-runtime.ps1</c> 拉取，落在 <c>&lt;应用目录&gt;\runtime\python\</c>，
/// 随应用分发（约 11MB）。**它刻意放在应用目录而不是数据根** —— 运行时是应用部件，
/// 跟着数据迁移被复制来复制去没有意义。
/// </para>
/// <para>
/// **"文件在" ≠ "能跑"**：这里提供 <see cref="ProbeAsync"/> 做一次真实启动测试并缓存结果。
/// 执行器在开跑前会问一次，避免把"启动器坏了"误报成"脚本写错了"。
/// </para>
/// </summary>
public sealed class SkillRuntime
{
    private readonly string _pythonPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (bool Ok, string Message)? _cached;

    /// <param name="baseDir">应用目录（调用方传 <c>AppContext.BaseDirectory</c>）</param>
    public SkillRuntime(string? baseDir)
    {
        _pythonPath = Path.Combine(baseDir ?? "", "runtime", "python", "python.exe");
    }

    /// <summary>内置解释器的预期路径（设置页展示用）</summary>
    public string PythonPath => _pythonPath;

    /// <summary>内置解释器是否在位（只查文件，不启动进程）</summary>
    public bool IsPresent => File.Exists(_pythonPath);

    /// <summary>运行时的初始状态（只查文件，不启动进程）—— 用于清单生成这种同步热路径</summary>
    public string QuickStatus => IsPresent ? "已就位" : $"未安装（缺少 {_pythonPath}）";

    /// <summary>
    /// 真跑一次（结果缓存到进程结束）。
    /// 同步路径只查文件、异步路径才真启动 —— 因为清单每轮都要生成，不能每次都起进程。
    /// </summary>
    public async Task<(bool Ok, string Message)> ProbeAsync(CancellationToken ct = default)
    {
        if (_cached.HasValue) return _cached.Value;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached.HasValue) return _cached.Value;
            _cached = await DoProbeAsync(ct).ConfigureAwait(false);
            return _cached.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>清掉缓存（用户重新拉了运行时后调用）</summary>
    public void Invalidate() => _cached = null;

    private async Task<(bool Ok, string Message)> DoProbeAsync(CancellationToken ct)
    {
        if (!File.Exists(_pythonPath))
            return (false, $"未检测到内置 Python 运行时。预期位置：{_pythonPath}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import sys;print(sys.version.split()[0])");

            using var proc = Process.Start(psi);
            if (proc == null) return (false, "运行时进程无法启动（Process.Start 返回空）。");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
                return (false, "运行时启动超时（10 秒无响应）。");
            }

            var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
            var stderr = (await stderrTask.ConfigureAwait(false)).Trim();

            if (proc.ExitCode == 0 && stdout.Length > 0)
                return (true, $"正常（Python {stdout}）");

            var detail = stderr.Length > 0 ? stderr : $"退出码 {proc.ExitCode}";
            return (false, $"运行时启动失败：{detail}");
        }
        catch (Exception ex)
        {
            return (false, $"运行时无法启动：{ex.Message}");
        }
    }

    /// <summary>
    /// 运行时缺失时给用户/模型看的统一文案 —— 必须带**应用内**的下一步。
    ///
    /// <para>
    /// ⚠ 面向用户的文案里**不许出现开发者路径**（<c>tools\*.bat</c> 这类）：那是"把该由应用做的事
    /// 外包给用户"的典型，而且实测模型会照着抄进给用户的回答里（2026-09-20 修）。
    /// 开发期怎么拉运行时写在 <c>docs/skill-runtime-plan.md</c>，不写在给用户看的话里。
    /// </para>
    /// </summary>
    public string MissingHint =>
        $"未检测到可用的 Python 运行时（预期位置：{_pythonPath}）。\n" +
        "这是**应用自身的组件缺失**，不是用户的操作问题：请如实说明该 Skill 现在执行不了，" +
        "并提示用户到「设置 → AI 模型 → Skill 扩展」查看运行时状态。" +
        "**不要建议用户去命令行、也不要让用户去找脚本文件操作。**";
}
