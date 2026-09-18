using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FocusCapture.ClipDiag;

/// <summary>
/// 剪贴板占用探针（2026-09-18 新增，**仅诊断用，不进产品流程**）。
///
/// 用途：灵感速览出现「复制失败：剪贴板被其他程序占用」时，判定到底是**外部程序**长期占用，
/// 还是**本应用自己没释放**。Win32 的 <c>GetOpenClipboardWindow</c> 直接返回"当前打开剪贴板的窗口"，
/// 映射到进程即可点名——不需要猜。
///
/// 用法：<c>ClipDiag.exe [--seconds 90] [--interval 500]</c>
/// 日志：<c>%TEMP%\fc-clipdiag.log</c>（同时打印到控制台）。
/// </summary>
internal static class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        var seconds = GetIntArg(args, "--seconds", 90);
        var intervalMs = GetIntArg(args, "--interval", 500);
        var logPath = Path.Combine(Path.GetTempPath(), "fc-clipdiag.log");

        var sb = new StringBuilder();
        void Emit(string line)
        {
            Console.WriteLine(line);
            sb.AppendLine(line);
        }

        Emit($"===== 剪贴板占用探针 | {DateTime.Now:yyyy-MM-dd HH:mm:ss} | 时长 {seconds}s / 间隔 {intervalMs}ms =====");
        Emit("说明：OpenClipboard=失败 表示那一刻**任何程序**都写不了剪贴板；");
        Emit("      占用窗口 = GetOpenClipboardWindow() 的结果，即「此刻谁正把剪贴板捏着不放」（含进程名）。");
        Emit("      所有者 = GetClipboardOwner()，通常等于最后一个写入剪贴板并采用延迟渲染的程序。");
        Emit("");
        Emit("→ 现在去复现「复制失败」（右键复制 / 双击后 Ctrl+C），本工具会把占用者点名。");
        Emit("");

        var total = Math.Max(1, seconds * 1000 / Math.Max(1, intervalMs));
        var failOpen = 0;
        var successOpen = 0;
        var holders = new Dictionary<string, int>();
        var owners = new Dictionary<string, int>();

        for (var i = 0; i < total; i++)
        {
            var openWnd = GetOpenClipboardWindow();
            var ownerWnd = GetClipboardOwner();
            var seq = GetClipboardSequenceNumber();

            var ok = OpenClipboard(IntPtr.Zero);
            var err = Marshal.GetLastWin32Error();
            if (ok) { CloseClipboard(); successOpen++; } else { failOpen++; }

            var holder = openWnd != IntPtr.Zero ? DescribeWindow(openWnd) : "(无)";
            var owner = ownerWnd != IntPtr.Zero ? DescribeWindow(ownerWnd) : "(无)";

            if (!ok)
            {
                Bump(holders, holder);
                Bump(owners, owner);
                Emit($"[{DateTime.Now:HH:mm:ss.fff}] OpenClipboard=**失败** err={err} 占用窗口={holder} 所有者={owner} seq={seq}");
            }
            else if (i % 10 == 0)
            {
                Emit($"[{DateTime.Now:HH:mm:ss.fff}] OpenClipboard=成功 占用窗口={holder} 所有者={owner} seq={seq}");
            }

            Thread.Sleep(intervalMs);
        }

        Emit("");
        Emit("===== 汇总 =====");
        Emit($"采样 {total} 次：成功 {successOpen} 次 / 失败 {failOpen} 次" +
             (total > 0 ? $"（失败率 {(double)failOpen / total:P0}）" : ""));

        if (holders.Count > 0)
        {
            Emit("失败时的占用窗口分布（谁把剪贴板捏着）：");
            foreach (var kv in holders.OrderByDescending(k => k.Value))
                Emit($"  {kv.Value,4} 次  {kv.Key}");
        }

        if (owners.Count > 0)
        {
            Emit("失败时的剪贴板所有者分布（最后一个写入者）：");
            foreach (var kv in owners.OrderByDescending(k => k.Value))
                Emit($"  {kv.Value,4} 次  {kv.Key}");
        }

        if (failOpen == 0)
            Emit("结论：全程未出现占用 —— 说明故障是间歇性的，需要「复现失败的同时」再跑一次本工具。");

        Emit($"日志已写入：{logPath}");

        try { File.WriteAllText(logPath, sb.ToString(), new UTF8Encoding(false)); }
        catch (Exception ex) { Console.WriteLine("写日志失败：" + ex.Message); }

        return 0;
    }

    private static void Bump(Dictionary<string, int> map, string key)
    {
        map.TryGetValue(key, out var c);
        map[key] = c + 1;
    }

    /// <summary>窗口句柄 → 「进程名(pid) class=窗口类名」，用于点名占用者</summary>
    private static string DescribeWindow(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out var pid);
        var procName = "?";
        try { procName = Process.GetProcessById((int)pid).ProcessName; } catch { }

        var cls = new StringBuilder(256);
        try { GetClassName(hWnd, cls, cls.Capacity); } catch { }

        var clsText = cls.Length > 0 ? cls.ToString() : "(读不到)";
        return $"0x{hWnd.ToInt64():X8} {procName}(pid={pid}) class={clsText}";
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
