using System.Text.Json;
using System.Text.Json.Serialization;
using FocusCapture;
using FocusCapture.Services;      // 迁移失败时要落日志（AppLog 在 Services 命名空间下）
using FocusCapture.Services.AI;   // 迁移时按 BaseUrl 反查预置供应商名（AiProviders.MatchByUrl）

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

    // ── 输入框圆角（2026-09-23 新增；可调四角弧度，0=纯直角，默认 10 = 改造前现状）──
    // 落「显示」板块；窗口 Background 改透明后，此值才真正露出圆角（见 InputWindow.xaml）
    public double InputBorderRadius { get; set; } = 10;

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
    // 【2026-09-23 多供应商改造】下面四个扁平字段是「单供应商时代」的遗留：
    //   AiBaseUrl / AiApiKey / AiModel / AiMaxTokens
    // 首次加载时由 MigrateLegacyAiConfig() 合成 AiModelProviders 里的第一条供应商；
    // 之后新代码不再读它们（唯一的兜底读取在 Services/AI/AiModelResolver）。
    // 刻意不删：回退旧版本时仍能读到配置，不至于让用户「降级即失联」。
    public string AiBaseUrl { get; set; } = "https://api.agnes-ai.cn/v1";   // 2026-09-23 修正：此前误写为 apihub.agnes-ai.cn（多一个 hub）
    public string AiApiKey { get; set; } = "";
    public string AiModel { get; set; } = "";   // 不再预置：模型更新快，交给用户自填
    public int AiMaxTokens { get; set; } = 4096;  // 回答长度上限（token）= 请求体 max_tokens

    // ── AI 模型：多供应商 × 多模型（2026-09-23 新增）──
    /// <summary>已配置的供应商列表（每个自带 BaseUrl / Key / 若干模型）。</summary>
    public List<AiProviderEntry> AiModelProviders { get; set; } = new();

    /// <summary>
    /// 当前使用的模型，格式 <c>&lt;providerId&gt;/&lt;modelId&gt;</c>。
    /// 本期只留数据字段与解析入口（<see cref="AiModelResolver.ResolveActive"/>），
    /// 切换 UI 在 AI 问答侧后续接入 —— 那时只需改这一个值，其余代码零改动。
    /// </summary>
    public string ActiveModelKey { get; set; } = "";

    public string AiAssistantName { get; set; } = "AI 问答";
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
    public double AiDrawerWidth { get; set; } = 240;   // AI 对话历史抽屉记忆宽度（拖拽后跨启动恢复；收起状态不记忆）

    // ── Skill 运行时（2026-09-20）──
    /// <summary>
    /// 已被用户授权执行脚本的 Skill 名（首次执行某 Skill 的脚本时弹窗确认，允许后记在这里）。
    /// 撤销 = 从这个列表里删掉，下次执行会重新询问 —— 只能授权不能撤销的安全机制是残缺的。
    /// </summary>
    public List<string> SkillTrusted { get; set; } = new();

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
    public string CustomIconPath { get; set; } = ""; // 自定义应用图标：托盘与任务栏窗口共用（%AppData%\FocusCapture\custom_icon.png）

    // ── 悬浮球拖放保存（2026-09-16）──
    /// <summary>
    /// 拖到悬浮球触发保存的总开关。**默认关**：关闭时悬浮球 AllowDrop=false，
    /// 拖放完全无反应（连光标都不变），与改造前行为完全一致。
    /// </summary>
    public bool DragToSaveEnabled { get; set; } = false;

    /// <summary>拖放后浮出的小条／卡片的不透明度（0.3~1.0），与 FloatBallOpacity 同套做法。</summary>
    public double DropActionOpacity { get; set; } = 0.90;

    /// <summary>文字拖入后小条的停留秒数（2~10，默认 3）。不点则自动消失，笔记已存在本地。</summary>
    public int DropActionStripSeconds { get; set; } = 3;

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

    // ── 云同步 ──
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
        AppSettings? loaded = null;
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);

            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                loaded = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings);
            }
        }
        catch { /* 读不出来就用默认值 —— 与原行为一致 */ }

        var settings = loaded ?? new AppSettings();

        // 老配置迁移**刻意放在上面那个 try 之外**：它自带 try，失败时什么都不改。
        // 若放进同一个 try，迁移里任何意外都会落进 catch → 返回 new AppSettings() → **用户配置全丢**。
        // 2026-09-23 当天刚出过一次「配置被静默清空」的事故，这类风险一律不冒。
        settings.MigrateLegacyAiConfig();
        return settings;
    }

    /// <summary>
    /// 老配置迁移（2026-09-23 多供应商改造）：把扁平的 AiBaseUrl / AiApiKey / AiModel / AiMaxTokens
    /// 合成 <see cref="AiModelProviders"/> 里的第一条供应商。
    ///
    /// <para><b>幂等</b>：已有任何供应商就完全不动，防止重复迁移生成多条。</para>
    /// <para><b>保命</b>：全程 try 住，失败只是「没迁移」——
    /// AiModelResolver.ResolveActive 有读旧扁平字段的兜底路径，
    /// 所以迁移失败不会让 AI 不可用，只是用户看到的仍是旧配置形态。</para>
    /// </summary>
    public void MigrateLegacyAiConfig()
    {
        try
        {
            if (AiModelProviders.Count > 0) return;   // 幂等：已经迁移过了

            var baseUrl = (AiBaseUrl ?? "").Trim();
            var apiKey = AiApiKey ?? "";
            var model = (AiModel ?? "").Trim();

            // 判据刻意**不看 BaseUrl**：它有非空默认值（api.agnes-ai.cn），
            // 拿它当「配过」的证据，会让全新用户凭空多出一条没有 Key、没有模型的 Agnes 供应商卡片。
            // Key 或模型名只要有一个填过，才算真配过。
            if (apiKey.Length == 0 && model.Length == 0) return;

            var preset = AiProviders.MatchByUrl(baseUrl);
            var provider = new AiProviderEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = preset?.Name ?? AiProviders.Custom,
                BaseUrl = baseUrl,
                ApiKey = apiKey,
            };

            if (model.Length > 0)
            {
                provider.Models.Add(new AiModelEntry
                {
                    Id = model,
                    DisplayName = model,                                     // 迁移来的没有更漂亮的名字，Id 即显示名
                    ContextWindow = 0,                                       // 不预填猜测值（见 AiModelEntry 注释）
                    MaxOutputTokens = AiMaxTokens > 0 ? AiMaxTokens : 4096,   // 旧值 ≤0 视为未配置，回退默认
                });
                ActiveModelKey = provider.Id + "/" + model;
            }

            AiModelProviders.Add(provider);
        }
        catch (Exception ex)
        {
            // 迁移失败不致命（解析层有兜底），所以这里不外抛。
            // AppLog 自己再套一层 try：此处已在 catch 里，若日志写入再抛就没东西接了，
            // 会顺着 Load() 一路冒到启动路径 —— 记不上日志事小，启动崩了事大。
            try { AppLog.Error("Settings", $"老配置迁移失败，AI 模型将按旧扁平字段兜底运行：{ex.Message}"); } catch { }
        }
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
