using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace FocusCapture.Services;

/// <summary>
/// 剪贴板变化监听，剪贴板监控开启时自动保存新增文本内容到笔记。
///
/// 防抖机制：部分系统工具会在"选中文字"时短暂写入剪贴板（~25ms 后恢复旧内容），
/// 这类瞬态写入会被 400ms 二次确认过滤掉，只有内容稳定保留的才视为真正的复制操作。
/// </summary>
public class ClipboardHookService : IDisposable
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly NoteService _noteService;
    private readonly Action? _onCaptured;
    private IntPtr _hwnd;
    private bool _installed;
    private bool _disposed;
    private string? _lastSavedText;
    private uint _lastSeqNum;

    // 防抖：400ms 后二次确认剪贴板内容未变才保存
    private DispatcherTimer? _debounceTimer;
    private string? _pendingText;
    private const int DebounceDelayMs = 400;

    private static DateTime _selfCopyTime = DateTime.MinValue;

    /// <summary>应用内部写入剪贴板前调用，抑制短时间内的监控捕获</summary>
    public static void MarkSelfCopy() => _selfCopyTime = DateTime.Now;

    public ClipboardHookService(NoteService noteService, Action? onCaptured = null)
    {
        _noteService = noteService;
        _onCaptured = onCaptured;
    }

    public bool IsInstalled => _installed;

    public void Install(IntPtr hwnd)
    {
        if (_installed) return;
        _hwnd = hwnd;
        _installed = AddClipboardFormatListener(hwnd);
    }

    public void Uninstall()
    {
        if (!_installed) return;
        RemoveClipboardFormatListener(_hwnd);
        _hwnd = IntPtr.Zero;
        _installed = false;
        _lastSavedText = null;
    }

    public void OnClipboardUpdate()
    {
        if (!_installed) return;

        uint seq = Win32.GetClipboardSequenceNumber();
        if (seq == _lastSeqNum) return;
        _lastSeqNum = seq;

        // 抑制应用自身触发的剪贴板变化
        if ((DateTime.Now - _selfCopyTime).TotalSeconds < 1)
            return;

        var text = Win32.GetClipboardText();
        if (string.IsNullOrWhiteSpace(text)) return;

        // 文本去重：与上次已保存的内容相同则跳过
        if (text == _lastSavedText) return;

        // 启动防抖：延迟二次确认
        _pendingText = text;

        if (_debounceTimer == null)
        {
            _debounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(DebounceDelayMs)
            };
            _debounceTimer.Tick += OnDebounceTimerTick;
        }

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnDebounceTimerTick(object? sender, EventArgs e)
    {
        _debounceTimer?.Stop();

        if (_pendingText == null) return;

        // 二次确认：剪贴板当前内容是否仍是之前捕获的文本
        var currentText = Win32.GetClipboardText();

        if (currentText == _pendingText)
        {
            // 内容稳定保留 → 真正的复制操作
            _lastSavedText = currentText;
            _noteService.SaveNote(currentText);
            _onCaptured?.Invoke();
        }
        // else: 内容已变 → 瞬态写入（疑似"选中即复制"工具），丢弃

        _pendingText = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _debounceTimer?.Stop();
        _debounceTimer = null;

        Uninstall();
    }
}
