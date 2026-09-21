using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace FocusCapture.DepDiag;

/// <summary>
/// 依赖授权隔离探针（2026-09-21 新增，**仅诊断用，不进产品流程**）。
///
/// 用途：实测 <c>lark-cli config init --new</c> 的真实输出格式
/// （字段名 / URL 在哪一段 / JSON 还是纯文本 / stdout 还是 stderr /
///  流式先吐 URL 还是进程退出才一起吐）—— 这是开发任务书 §8 的"必做且阻塞"动作，
///  也是设计稿 §9.2 标注的"当前最大未验证风险"。
///
/// 三条硬要求（开发任务书 §8 + 设计稿 R1）：
/// 1. **绝不碰用户真实的 ~/.lark-cli** —— USERPROFILE 指向 %TEMP% 下的隔离目录。
/// 2. **清掉宿主变量** HERMES_HOME / OPENCLAW_HOME / LARK_CHANNEL / PYTHONPATH
///    （否则 lark-cli 会走 hermes 分支报 not bound，见 2026-09-21.md 实测）。
/// 3. **超时后保留已读输出** —— 这本身就是 R1 的验证点
///    （现有 SkillProcess.RunAsync 超时会丢弃已读内容，本探针要证明"流式读 + 超时保留"可行）。
///
/// 用法：<c>DepDiag.exe [--lark path] [--timeout 90] [--args "config init --new"] [--iso-root path]</c>
/// 自测（无副作用）：<c>DepDiag.exe --args "config init --help"</c>
/// 日志：<c>%TEMP%\fc-depdiag.log</c>（同时打印到控制台）。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        var larkArg = GetStringArg(args, "--lark", "");
        var timeoutSec = GetIntArg(args, "--timeout", 90);
        var larkArgs = GetStringArg(args, "--args", "config init --new");
        var isoRootArg = GetStringArg(args, "--iso-root", "");
        var logPath = Path.Combine(Path.GetTempPath(), "fc-depdiag.log");

        var sb = new StringBuilder();
        void Emit(string line) { Console.WriteLine(line); sb.AppendLine(line); }

        Emit($"===== 依赖授权隔离探针 | {DateTime.Now:yyyy-MM-dd HH:mm:ss} | 超时 {timeoutSec}s =====");
        Emit("说明：USERPROFILE 已隔离到 %TEMP% 下空目录，绝不读写真实 ~/.lark-cli；");
        Emit("      stdout/stderr 分别流式按行记录（带来源标记 + 时间戳），超时 kill 但保留已读（R1 验证）。");
        Emit("");

        // 1) 定位 lark-cli
        var lark = ResolveLark(larkArg, Emit);
        if (lark is null)
        {
            Emit("✗ 未找到 lark-cli，无法继续（传 --lark <绝对路径> 指定）。");
            try { File.WriteAllText(logPath, sb.ToString(), new UTF8Encoding(false)); } catch { }
            return 2;
        }
        Emit($"待跑命令：{lark} {larkArgs}");
        Emit("");

        // 2) 隔离 USERPROFILE
        var isoRoot = string.IsNullOrEmpty(isoRootArg)
            ? Path.Combine(Path.GetTempPath(), "depdiag-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"))
            : isoRootArg;
        Directory.CreateDirectory(isoRoot);
        Emit("===== 环境隔离 =====");
        Emit($"  USERPROFILE -> {isoRoot}");
        Emit($"  已清除变量：HERMES_HOME / OPENCLAW_HOME / LARK_CHANNEL / PYTHONPATH（不存在则无操作）");
        Emit($"  其余环境变量继承当前进程（含 PATH）");
        Emit("");

        // 3) 起进程（不合并流——要分别记录来自 stdout 还是 stderr，这是要抓的格式之一）
        var psi = new ProcessStartInfo
        {
            FileName = lark,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = isoRoot,
        };
        foreach (var a in SplitArgs(larkArgs)) psi.ArgumentList.Add(a);

        // 环境隔离：覆盖 USERPROFILE + 删 4 个宿主变量
        psi.Environment["USERPROFILE"] = isoRoot;
        foreach (var k in new[] { "HERMES_HOME", "OPENCLAW_HOME", "LARK_CHANNEL", "PYTHONPATH" })
            psi.Environment.Remove(k);

        // 4) 流式读（分别 stdout/stderr，按行 + 时间戳 + URL 检测）
        var sync = new object();
        var outLines = new List<string>();
        var errLines = new List<string>();
        var allUrls = new List<string>();
        var urlRe = new Regex(@"https?://[^\s""'<>]+", RegexOptions.Compiled);

        void HandleLine(string tag, string? data)
        {
            // e.Data == null 表示该流已关闭（EOF）；超时 kill 后用 Task.Delay 等 flush，此处无需处理
            if (data is null) return;
            lock (sync)
            {
                if (tag == "out") outLines.Add(data); else errLines.Add(data);
                foreach (Match m in urlRe.Matches(data)) allUrls.Add(m.Value);
            }
            Emit($"  [{tag} {DateTime.Now:HH:mm:ss.fff}] {data}");
        }

        Emit("===== 实时输出 =====");
        var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => HandleLine("out", e.Data);
        proc.ErrorDataReceived += (_, e) => HandleLine("err", e.Data);

        var sw = Stopwatch.StartNew();
        if (!proc.Start())
        {
            Emit("✗ 进程启动失败。");
            try { File.WriteAllText(logPath, sb.ToString(), new UTF8Encoding(false)); } catch { }
            return 3;
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // 超时保护：默认 90s。config init --new 输出 URL 后会阻塞等浏览器完成，
        // 探针不会去浏览器 → 必然超时 kill（这正是要验证的"超时保留已读"语义）。
        var cts = new CancellationTokenSource(timeoutSec * 1000);
        var timedOut = false;
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { timedOut = true; }

        if (timedOut)
        {
            Emit("");
            Emit($"[{sw.Elapsed.TotalSeconds:F1}s] 超时 —— kill 进程树（保留已读输出 = R1 验证点）。");
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { await proc.WaitForExitAsync(); } catch { }
            // 让异步读回调把管道里剩余行 flush 完
            try { await Task.Delay(500); } catch { }
        }

        var code = proc.HasExited ? proc.ExitCode : -1;

        // 5) 汇总
        Emit("");
        Emit("===== 汇总 =====");
        Emit($"  进程结局：{(timedOut ? "超时被 kill" : "自然退出")}  退出码={code}  耗时={sw.Elapsed.TotalSeconds:F1}s");
        Emit($"  stdout 行数 = {outLines.Count}    stderr 行数 = {errLines.Count}");
        Emit($"  stdout 有输出 = {(outLines.Count > 0 ? "是" : "否")}    stderr 有输出 = {(errLines.Count > 0 ? "是" : "否")}");
        if (allUrls.Count > 0)
        {
            Emit($"  检测到 URL（去重 {allUrls.Distinct().Count()} 条）：");
            foreach (var u in allUrls.Distinct()) Emit($"    {u}");
        }
        else
        {
            Emit("  未检测到 URL。");
        }
        Emit("");
        Emit("--- stdout 全文 ---");
        if (outLines.Count == 0) Emit("(空)"); else foreach (var l in outLines) Emit(l);
        Emit("");
        Emit("--- stderr 全文 ---");
        if (errLines.Count == 0) Emit("(空)"); else foreach (var l in errLines) Emit(l);
        Emit("");
        Emit($"日志已写入：{logPath}");

        try { File.WriteAllText(logPath, sb.ToString(), new UTF8Encoding(false)); }
        catch (Exception ex) { Console.WriteLine("写日志失败：" + ex.Message); }

        return 0;
    }

    // ---- 工具方法 ----

    /// <summary>定位 lark-cli：--lark 优先 → 从探针目录向上回溯找 runtime/lark-cli/lark-cli.exe → PATH</summary>
    private static string? ResolveLark(string arg, Action<string> emit)
    {
        if (!string.IsNullOrEmpty(arg))
        {
            if (File.Exists(arg)) { emit($"  lark-cli: {arg}（来源：--lark）"); return arg; }
            emit($"  --lark 指定但文件不存在：{arg}，回退自动探测。");
        }
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
        {
            var cand = Path.Combine(dir, "runtime", "lark-cli", "lark-cli.exe");
            if (File.Exists(cand)) { emit($"  lark-cli: {cand}（来源：自动探测）"); return cand; }
            dir = Path.GetDirectoryName(dir!) ?? "";
        }
        var onPath = FindOnPath("lark-cli.exe");
        if (onPath is not null) { emit($"  lark-cli: {onPath}（来源：PATH）"); return onPath; }
        return null;
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var d in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(d, name);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    /// <summary>极简切分：按空白。本场景 lark-cli 参数无内嵌空格，够用。</summary>
    private static IEnumerable<string> SplitArgs(string s)
    {
        foreach (var p in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            yield return p;
    }

    private static string GetStringArg(string[] args, string name, string fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return fallback;
    }

    private static int GetIntArg(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(args[i + 1], out var v) && v > 0) return v;
        }
        return fallback;
    }
}
