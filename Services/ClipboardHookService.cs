using System.Runtime.InteropServices;

namespace FocusCapture.Services;

/// <summary>剪贴板变化监听，开启时自动保存每一次复制的内容</summary>
public class ClipboardHookService : IDisposable
{
    // ── Win32: AddClipboardFormatListener ──
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    // ── 服务 ──
    private readonly NoteService _noteService;
    private readonly Action? _onCaptured;
    private IntPtr _hwnd;
    private bool _installed;
    private bool _disposed;
    private string? _lastSavedText; // 去重，防止同内容反复保存

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

    /// <summary>当窗口收到 WM_CLIPBOARDUPDATE 时由外部调用</summary>
    public void OnClipboardUpdate()
    {
        if (!_installed) return;

        var text = Win32.GetClipboardText();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text == _lastSavedText) return; // 去重

        _lastSavedText = text;
        _noteService.SaveNote(text);
        _onCaptured?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uninstall();
    }
}
