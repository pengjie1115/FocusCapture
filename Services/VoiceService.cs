using System.Diagnostics;
using System.Text.Json;
using System.Threading;

namespace FocusCapture.Services;

/// <summary>
/// 管理 Python ASR 子进程生命周期。
/// 启动 → 异步读 stdout JSON → 事件回调 →
/// 停止 → 关闭 stdin 管道，Python 端检测到 stdin 关闭后退出
/// </summary>
public class VoiceService : IDisposable
{
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private bool _disposed;

    // ── 事件 ──
    /// <summary>收到部分识别结果（SenseVoice 非流式，此事件仅作状态提示）</summary>
    public event Action<string>? PartialText;

    /// <summary>一段话识别完成</summary>
    public event Action<string>? FinalText;

    /// <summary>ASR 就绪</summary>
    public event Action<string>? Ready;

    /// <summary>错误</summary>
    public event Action<string>? Error;

    /// <summary>状态更新（下载进度、模型就绪等）</summary>
    public event Action<string>? StatusChanged;

    /// <summary>音量电平（0.0~1.0）</summary>
    public event Action<double>? VolumeLevel;

    /// <summary>是否正在运行</summary>
    public bool IsRunning => _process != null && !_process.HasExited;

    // ── 路径 ──
    private static readonly string ModelDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FocusCapture", "models", "sensevoice");

    private static readonly string ScriptPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "asr_server.py");

    /// <summary>启动 ASR 子进程</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VoiceService));
        if (IsRunning) return;

        // 模型由 Python 端自动下载，C# 端不做预检查
        if (!File.Exists(ScriptPath))
        {
            Error?.Invoke($"ASR 脚本缺失: {ScriptPath}\n请重新编译项目。");
            return;
        }

        var realPython = ResolvePythonPath();

        Debug.WriteLine($"[VoiceService] Python: {realPython}");
        Debug.WriteLine($"[VoiceService] Script: {ScriptPath}");
        Debug.WriteLine($"[VoiceService] Model:  {ModelDir}");

        _cts = new CancellationTokenSource();

        var psi = new ProcessStartInfo
        {
            FileName = realPython,
            Arguments = $"\"{ScriptPath}\" \"{ModelDir}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += OnProcessExited;

        try
        {
            _process.Start();
            _process.StandardInput.AutoFlush = true;

            // 异步读 stdout
            _readTask = Task.Run(() => ReadStdout(_cts.Token), _cts.Token);

            // 异步读 stderr（仅 debug 输出）
            _ = Task.Run(() =>
            {
                while (!_process.StandardError.EndOfStream)
                {
                    var line = _process.StandardError.ReadLine();
                    if (line != null) Debug.WriteLine($"[VoiceService:stderr] {line}");
                }
            }, _cts.Token);
        }
        catch (Exception ex)
        {
            Error?.Invoke($"启动语音识别失败: {ex.Message}");
            Stop();
        }
    }

    /// <summary>停止 ASR 子进程。关闭 stdin 管道，Python 端检测到后退出</summary>
    public void Stop()
    {
        if (_process == null) return;

        try
        {
            // 关闭 stdin → Python 的 sys.stdin.read(1) 返回空 → 退出
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
            }
        }
        catch { /* 进程可能已退出 */ }

        // 给 Python 2 秒优雅退出
        try
        {
            if (!_process.HasExited && !_process.WaitForExit(2000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { }

        _cts?.Cancel();
        _process?.Dispose();
        _process = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // ══════════════════════════════════════════════
    //  内部
    // ══════════════════════════════════════════════

    private void ReadStdout(CancellationToken ct)
    {
        if (_process == null) return;
        try
        {
            while (!_process.StandardOutput.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = _process.StandardOutput.ReadLine();
                if (string.IsNullOrEmpty(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.GetProperty("type").GetString();
                    var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";

                    switch (type)
                    {
                        case "ready":
                            Ready?.Invoke(text);
                            break;
                        case "partial":
                            PartialText?.Invoke(text);
                            break;
                        case "final":
                            FinalText?.Invoke(text);
                            break;
                        case "error":
                            Error?.Invoke(text);
                            break;
                        case "status":
                        case "progress":
                            StatusChanged?.Invoke(text);
                            break;
                        case "volume":
                            if (root.TryGetProperty("value", out var val))
                                VolumeLevel?.Invoke(val.GetDouble());
                            break;
                    }
                }
                catch (JsonException ex)
                {
                    Debug.WriteLine($"[VoiceService] JSON 解析失败: {ex.Message} | 行: {line}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* 管道关闭 */ }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        _cts?.Cancel();
    }

    private static string ResolvePythonPath()
    {
        // 从输出目录往上找 asr_venv
        var dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        for (int i = 0; i < 5; i++)
        {
            var candidate = Path.Combine(dir, "asr_venv", "Scripts", "python.exe");
            Debug.WriteLine($"[VoiceService] 尝试 Python: {candidate}");
            if (File.Exists(candidate))
            {
                Debug.WriteLine($"[VoiceService] 找到 Python: {candidate}");
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
            if (dir == null) break;
        }

        Debug.WriteLine("[VoiceService] 未找到 venv Python，回退到系统 python");
        return "python";
    }
}
