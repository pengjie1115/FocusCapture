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
    // 配置路径统一由 FocusCapturePaths 解析（2026-09-11）：测试隔离时可改道，生产默认值不变。
    // 必须是属性而非 static readonly —— 静态字段在类型初始化时求值并缓存，会导致覆盖失效。
    private static string ConfigPath => FocusCapturePaths.Combine("settings.json");

    // ── 热键 ──
    public HotkeyBinding SummonHotkey { get; set; } = new() { Modifiers = 1, Key = 0x20 };         // Alt+Space
    public HotkeyBinding ClipboardToggleHotkey { get; set; } = new() { Modifiers = 3, Key = 0x70 }; // Ctrl+Alt+F1
    public HotkeyBinding QuickViewHotkey { get; set; } = new() { Modifiers = 3, Key = 0x56 };       // Ctrl+Alt+V
    public HotkeyBinding SettingsHotkey { get; set; } = new() { Modifiers = 3, Key = 0x53 };        // Ctrl+Alt+S：唤出设置面板
    public HotkeyBinding AiAskHotkey { get; set; } = new() { Modifiers = 3, Key = 0x41 };          // Ctrl+Alt+A：唤起 AI 问答
    public HotkeyBinding TodoSummaryHotkey { get; set; } = new() { Modifiers = 3, Key = 0x54 };    // Ctrl+Alt+T：待办汇总面板（按一次唤出，再按收起）

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

    // ── 输入框：自动隐藏与位置（v3.6）──
    public bool InputAlwaysVisible { get; set; } = false;      // true = 始终显示，不自动隐藏
    public int InputAutoHideSeconds { get; set; } = 15;        // 自定义隐藏秒数（最短 3，不设上限）
    public bool InputRememberPosition { get; set; } = false;   // true = 唤起时出现在上次位置
    public double InputLeft { get; set; } = -1;                // 上次拖动到的位置（-1 = 未拖动过）
    public double InputTop { get; set; } = -1;

    // ── 开机自启 ──
    public bool AutoStart { get; set; } = false;

    // ── 导出 ──
    public string ExportFolderPath { get; set; } = "";
    public ExportConfig? LastExportConfig { get; set; }

    // ── AI 模型 ──
    public string AiBaseUrl { get; set; } = "https://apihub.agnes-ai.cn/v1";
    public string AiApiKey { get; set; } = "";
    public string AiModel { get; set; } = "";   // 不再预置：模型更新快，交给用户自填
    public string AiAssistantName { get; set; } = "AI 问答";
    public int AiMaxTokens { get; set; } = 4096;  // 回答长度上限（token）：此前不传由供应商默认值决定，常致长回答被 finish_reason=length 截断
    public int AiToolResultLimit { get; set; } = 8000;  // 工具结果喂回模型前的单条截断阈值（防超长结果撑爆上下文）

    // ── AI 附件（2026-09-14：问答输入框支持图片与文档）──
    // 图片清晰度档位：0=省流(长边768/质量70) 1=标准(1568/85) 2=高清(2048/90)
    // 越高越清晰但 token 与流量越大；标准档是"小字截图仍可辨认"的平衡点
    public int AiImageQualityLevel { get; set; } = 1;
    // 是否允许向上行请求携带图片。默认开（功能开箱可用）；若当前模型不支持视觉，
    // 可在设置中关闭——关闭后粘贴图片会被拦下并提示，避免反复撞 400。
    public bool AiVisionEnabled { get; set; } = true;

    // ── Agent 工具（AI 动手能力）──
    public bool AgentEnabled { get; set; } = false;   // 默认关：关闭时 AI 对话行为与旧版完全一致
    public bool AgentWriteConfirmPopup { get; set; } = false; // 默认不弹窗：写操作靠系统提示词对话内确认 + 回收站 + 运行日志兜底
    public int AgentMaxToolRounds { get; set; } = 15;  // 工具调用往返轮数上限：原硬编码 5 偏低（多步工具易触顶），默认 15
    public double AiDrawerWidth { get; set; } = 240;   // AI 对话历史抽屉记忆宽度（拖拽后跨启动恢复；收起状态不记忆，方案文档 §7 设置项 3）

    // ── 运行日志 ──
    public int LogRetentionDays { get; set; } = 30;   // 日志保留天数（1-365，超期自动清理）
    public string GetNoteApiKey { get; set; } = "";   // 得到大脑开放平台凭证（设置面板得到大脑区配置）
    public string GetNoteClientId { get; set; } = "";
    public string GetNoteDefaultTopicId { get; set; } = "";   // 默认上传知识库 id（知识库下拉选择后落盘；空 = 账号默认库）
    public string GetNoteDefaultTopicName { get; set; } = ""; // 默认知识库名称（仅下拉回显用，不参与请求）

    // ── 文件与网盘（2026-09-16）──
    // 说明：AppKey / SecretKey / 授权令牌**不在这里** —— 它们走 BaiduCredentialStore 的独立加密文件，
    // 既避免授权令牌跟着 settings.json 同步到别的设备，也避免频繁滚动刷新 token 时反复重写主配置。

    /// <summary>百度网盘沙箱内的工作目录（即开发者应用的目录名），默认 /apps/FocusCapture。</summary>
    public string BaiduNetRoot { get; set; } = "/apps/FocusCapture";

    /// <summary>
    /// 对话附件是否自动上传到网盘（**默认关**，保持「只存本机」的既有行为）。
    /// 注意这个开关只管「自动」：关着的时候用户显式说「把这张图存上去」照样会传（显式意图优先）。
    /// </summary>
    public bool BaiduAttachmentUploadEnabled { get; set; } = false;

    /// <summary>对话附件在云端的保留天数（默认 30，0 = 不自动清理）。只作用于附件，产出与主动上传永久保留。</summary>
    public int BaiduAttachmentRetentionDays { get; set; } = 30;

    /// <summary>本地缓存容量上限（GB，默认 5）。超出后按最后使用时间从最旧开始释放本地副本（云端不动）。</summary>
    public double LocalCacheMaxGb { get; set; } = 5;

    /// <summary>本地缓存闲置多少天后可被淘汰（默认 30 天）。</summary>
    public int LocalCacheIdleDays { get; set; } = 30;

    /// <summary>是否启用本地缓存自动淘汰（默认开）。关掉后只能手动清理。</summary>
    public bool LocalCacheEvictEnabled { get; set; } = true;

    // ── 外观 ──
    public string CustomIconPath { get; set; } = ""; // 自定义托盘图标（%AppData%\FocusCapture\custom_icon.png）

    // ── 灵感速览窗口壳（v3.9：宽度/置顶/标题栏可组装）──
    // 唤出面板宽度（px，480–1280）；标题栏按钮区预算 = 宽度 - QuickViewToolbarCatalog.FixedTitleBarOverhead
    public double QuickViewWidth { get; set; } = 620;
    // 面板是否全局置顶（默认开；最小化/最大化按钮为固定铬区，不占自定义槽位）
    public bool QuickViewTopmost { get; set; } = true;
    // 标题栏可组装按钮（有序 id 列表，功能目录见 QuickViewToolbarCatalog；读取侧做清洗回退，见 Sanitize）
    public List<string> QuickViewToolbarLeft { get; set; } = ["Calendar", "SyncUpload", "SyncDownload"];
    public List<string> QuickViewToolbarRight { get; set; } = ["Search", "Refresh", "AiAsk", "Export", "GetNote"];

    // ── 灵感速览时间筛选（v3.8）──
    // 唤出行为：false = 每次唤出重置为当天笔记（默认）；true = 恢复上一次的时间筛选
    public bool QuickViewRestoreLastFilter { get; set; } = false;
    // 上次时间筛选存档（跨启动记忆，供「恢复上次筛选」与弹层勾选回显用；Search 模式不记忆）
    public string QuickViewLastTimeMode { get; set; } = "Date";   // "Date" / "Range"
    public string QuickViewLastDate { get; set; } = "";           // Date 模式单日（yyyy-MM-dd；空 = 今天）
    public string QuickViewLastRangeStart { get; set; } = "";     // Range 模式起（yyyy-MM-dd）
    public string QuickViewLastRangeEnd { get; set; } = "";       // Range 模式止（yyyy-MM-dd）

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
    public bool DailySummaryEmptyPopup { get; set; } = true;                      // v3.7：当天无待办时是否仍弹汇总弹窗（默认弹）
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
