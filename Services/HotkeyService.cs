using FocusCapture.Models;

namespace FocusCapture.Services;

public class HotkeyService : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly AppSettings _settings;

    // 热键 ID 常量
    public const int ID_SUMMON = 1001;
    public const int ID_CLIPBOARD_TOGGLE = 1002;
    public const int ID_QUICK_VIEW = 1003;
    public const int ID_VOICE_INPUT = 1004;

    public event Action<int>? HotkeyPressed; // 回调传热键 ID

    public HotkeyService(IntPtr hwnd, AppSettings settings)
    {
        _hwnd = hwnd;
        _settings = settings;
    }

    public void RegisterAll()
    {
        UnregisterAll();

        Register(ID_SUMMON, _settings.SummonHotkey);
        Register(ID_CLIPBOARD_TOGGLE, _settings.ClipboardToggleHotkey);
        Register(ID_QUICK_VIEW, _settings.QuickViewHotkey);
        Register(ID_VOICE_INPUT, _settings.VoiceInputHotkey);
    }

    public void UnregisterAll()
    {
        Win32.UnregisterHotKey(_hwnd, ID_SUMMON);
        Win32.UnregisterHotKey(_hwnd, ID_CLIPBOARD_TOGGLE);
        Win32.UnregisterHotKey(_hwnd, ID_QUICK_VIEW);
        Win32.UnregisterHotKey(_hwnd, ID_VOICE_INPUT);
    }

    private void Register(int id, HotkeyBinding hk)
    {
        if (hk.Key == 0) return; // disabled hotkey
        var mods = Win32.ModifiersToWin32(hk.Modifiers);
        Win32.RegisterHotKey(_hwnd, id, mods, (uint)hk.Key);
    }

    /// <summary>处理 WM_HOTKEY 消息</summary>
    public void HandleHotkey(int id)
    {
        HotkeyPressed?.Invoke(id);
    }

    public void Dispose() => UnregisterAll();
}
