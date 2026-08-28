using System.Text.Json;
using System.Text.Json.Serialization;
using FocusCapture;

namespace FocusCapture.Models;

public class HotkeyBinding
{
    public int Modifiers { get; set; } // 1=Alt, 2=Ctrl, 4=Shift, 8=Win
    public int Key { get; set; }       // Virtual key code
}

public class AppSettings
{
    private const string ConfigDir = "FocusCapture";
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ConfigDir, "settings.json");

    // ── 热键 ──
    public HotkeyBinding SummonHotkey { get; set; } = new() { Modifiers = 1, Key = 0x20 };         // Alt+Space
    public HotkeyBinding ClipboardToggleHotkey { get; set; } = new() { Modifiers = 3, Key = 0x70 }; // Ctrl+Alt+F1
    public HotkeyBinding QuickViewHotkey { get; set; } = new() { Modifiers = 3, Key = 0x56 };       // Ctrl+Alt+V

    // ── 剪贴板自动捕获 ──
    public bool ClipboardCaptureEnabled { get; set; } = false;

    // ── 不透明度 ──
    public double InputOpacity { get; set; } = 0.80;
    public double FloatBallOpacity { get; set; } = 0.85;
    public double QuickViewOpacity { get; set; } = 0.80;

    // ── 存储 ──
    public string NotesPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FocusCapture");

    // ── 悬浮球位置 ──
    public double BallLeft { get; set; } = -1;
    public double BallTop { get; set; } = -1;

    // ── 开机自启 ──
    public bool AutoStart { get; set; } = false;

    // ── 导出 ──
    public string ExportFolderPath { get; set; } = "";
    public ExportConfig? LastExportConfig { get; set; }

    // ── AI 模型 ──
    public string AiBaseUrl { get; set; } = "https://apihub.agnes-ai.cn/v1";
    public string AiApiKey { get; set; } = "";
    public string AiModel { get; set; } = "agnes-2.5-flash";
    public string AiAssistantName { get; set; } = "AI 问答";

    // ── 外观 ──
    public string CustomIconPath { get; set; } = ""; // 自定义托盘图标（%AppData%\FocusCapture\custom_icon.png）

    // ── 沉浸式语音输入 ──
    public HotkeyBinding VoiceInputHotkey { get; set; } = new() { Modifiers = 3, Key = 0x52 }; // Ctrl+Alt+R
    public HotkeyBinding SaveHotkey { get; set; } = new() { Modifiers = 2, Key = 0x53 };       // Ctrl+S
    public double VoiceWindowLeft { get; set; } = -1;     // -1 = 居中
    public double VoiceWindowTop { get; set; } = -1;
    public double VoiceWindowWidth { get; set; } = 900;
    public double VoiceWindowHeight { get; set; } = 600;
    public string VoiceTheme { get; set; } = "Dark";       // Dark / Light
    public bool VoiceTopmost { get; set; } = false;
    public double VoiceSplitterPosition { get; set; } = 0.65; // 正文占比 (0.3~0.9)

    // ── 云同步（QUEST-5）──
    public SyncSettings Sync { get; set; } = new();

    // ── v3.5 待办与提醒 ──
    public string InputDefaultType { get; set; } = "Note";                       // "Note" / "Todo"
    public HotkeyBinding TodoSwitchHotkey { get; set; } = new() { Modifiers = 2, Key = 0x54 }; // Ctrl+T，全局热键（RegisterHotKey），可能与其他应用冲突，设置可改
    public bool DailySummaryEnabled { get; set; } = true;
    public string DailySummaryTime { get; set; } = "18:00";                       // "HH:mm"
    public int SnoozeMinutes { get; set; } = 10;
    public int PopupAutoCloseSeconds { get; set; } = 10;
    public bool AskTimeForDateOnly { get; set; } = true;                         // 纯日期（如"30号"）是否弹窗问几点；false=直接默认当天 09:00

    // ── 序列化 ──
    public static AppSettings Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);

            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var settings = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings);
                if (settings != null) return settings;
            }
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, AppJsonContext.Default.AppSettings);
            File.WriteAllText(ConfigPath, json);
        }
        catch { /* best effort */ }
    }
}

/// <summary>导出内容选择配置</summary>
public class ExportConfig
{
    public bool IncludeTime { get; set; } = true;
    public bool IncludeSource { get; set; } = true;
    public bool IncludeTag { get; set; } = false;
    public bool IncludeContent { get; set; } = true;
    public ExportFormat Format { get; set; } = ExportFormat.Markdown;
}
