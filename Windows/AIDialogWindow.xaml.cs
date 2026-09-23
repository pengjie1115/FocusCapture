using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;
using FocusCapture.Services.Agent;
using FocusCapture.Services.Destinations;
using FocusCapture.Services.Files;
using FocusCapture.Services.Skills;
using FocusCapture.Windows.Controls;

namespace FocusCapture.Windows;

/// <summary>
/// 气泡里的单个附件展示项（2026-09-14）。
/// 与输入区用同一种卡片（图标前缀 + 截断文件名），悬停出预览、双击看大图/打开原文件；
/// 本体不在本机（跨端拉来的会话）时降级配色并标注，不显示裂图。
/// </summary>
public class ChatAttachmentViewModel
{
    /// <summary>对应的数据模型（预览 / 打开时用）</summary>
    public ChatAttachment Model { get; }

    /// <summary>卡片文案："图片 文件名" / "文件 文件名"</summary>
    public string ChipText { get; }

    public bool IsImage { get; }

    /// <summary>本体是否不在本机（跨端会话的附件）</summary>
    public bool IsMissing { get; }

    public ChatAttachmentViewModel(ChatAttachment model)
    {
        Model = model;
        ChipText = ChipLabel(model);
        IsImage = model.Kind == ChatAttachmentKind.Image;

        var path = ChatAttachmentService.ResolvePath(model);
        IsMissing = !File.Exists(path);
    }

    /// <summary>
    /// 卡片文案的唯一实现：输入区卡片与气泡卡片都调这里，避免两处文案漂移。
    /// 文件名截断到 16 字——输入框一行装不下长文件名，会把行撑得很难看。
    /// </summary>
    public static string ChipLabel(ChatAttachment model)
    {
        var name = model.FileName.Length > 16 ? model.FileName[..16] + "…" : model.FileName;
        return (model.Kind == ChatAttachmentKind.Image ? "图片 " : "文件 ") + name;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}

/// <summary>
/// 对话里的云文件卡片（2026-09-16）。
/// 与附件卡片是两套东西：附件是「要发给模型看的内容」，这里是「从网盘取回到本机的文件」，
/// 所以独立成一个 VM，避免把两种语义混进同一个模板里。
/// </summary>
public class CloudFileCardViewModel
{
    public FileMetadata Model { get; }

    /// <summary>卡片文案：云文件 名称 · 大小</summary>
    public string Label { get; }

    /// <summary>本机路径（取回后必有；为空表示还没落到本机）。</summary>
    public string? LocalPath { get; }

    public CloudFileCardViewModel(FileMetadata model)
    {
        Model = model;
        var entry = FileRepository.FindCache(model.Id);
        LocalPath = entry != null && File.Exists(entry.LocalPath) ? entry.LocalPath : null;

        var name = model.Name.Length > 20 ? model.Name[..20] + "…" : model.Name;
        Label = $"云文件 {name} · {ChatAttachmentViewModel.FormatSize(model.Size)}";
    }
}

/// <summary>对话框消息气泡 ViewModel</summary>
public class ChatBubbleViewModel : INotifyPropertyChanged
{
    public bool IsUser { get; }

    private string _content = string.Empty;
    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            FirePropertyChanged(nameof(Content));
        }
    }

    public bool IsFillable { get; }

    private bool _isFilled;
    public bool IsFilled
    {
        get => _isFilled;
        set
        {
            if (_isFilled == value) return;
            _isFilled = value;
            FirePropertyChanged(nameof(IsFilled));
        }
    }

    private string _reasoningText = "";
    /// <summary>思考过程文本（仅思考型模型产生；流式追加）</summary>
    public string ReasoningText
    {
        get => _reasoningText;
        set
        {
            if (_reasoningText == value) return;
            _reasoningText = value;
            FirePropertyChanged(nameof(ReasoningText));
            FirePropertyChanged(nameof(HasReasoning));
        }
    }

    public bool HasReasoning => _reasoningText.Length > 0;

    private bool _isReasoningOpen;
    /// <summary>思考过程区展开状态：流式期间自动展开，回答结束收起</summary>
    public bool IsReasoningOpen
    {
        get => _isReasoningOpen;
        set
        {
            if (_isReasoningOpen == value) return;
            _isReasoningOpen = value;
            FirePropertyChanged(nameof(IsReasoningOpen));
        }
    }

    private string _toolSteps = "";
    /// <summary>Agent 工具调用步骤（每步一行，回答完成后保留为过程记录）</summary>
    public string ToolSteps
    {
        get => _toolSteps;
        set
        {
            if (_toolSteps == value) return;
            _toolSteps = value;
            FirePropertyChanged(nameof(ToolSteps));
            FirePropertyChanged(nameof(HasToolSteps));
        }
    }

    public bool HasToolSteps => _toolSteps.Length > 0;

    /// <summary>消息携带的附件（仅用户消息会出现；无附件为 null）</summary>
    public IReadOnlyList<ChatAttachmentViewModel>? Attachments { get; }

    public bool HasAttachments => Attachments is { Count: > 0 };

    /// <summary>从网盘取回并交付给用户的文件卡片（工具执行过程中动态追加）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<CloudFileCardViewModel> CloudFiles { get; } = new();

    public bool HasCloudFiles => CloudFiles.Count > 0;

    /// <summary>追加一张云文件卡片（在 UI 线程调用）。</summary>
    public void AddCloudFile(FileMetadata meta)
    {
        if (CloudFiles.Any(c => c.Model.Id == meta.Id)) return;   // 同一文件只给一张卡
        CloudFiles.Add(new CloudFileCardViewModel(meta));
        FirePropertyChanged(nameof(HasCloudFiles));
    }

    /// <summary>卡片被移除后刷新「是否有云文件卡片」（添加走 AddCloudFile，移除走这里）。</summary>
    public void NotifyCloudFilesChanged() => FirePropertyChanged(nameof(HasCloudFiles));

    public ChatBubbleViewModel(bool isUser, string content, bool isFillable = false,
        IReadOnlyList<ChatAttachmentViewModel>? attachments = null)
    {
        IsUser = isUser;
        Content = content;
        IsFillable = isFillable;
        Attachments = attachments;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void FirePropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>三板块 AI 对话框：翻译 / 搜索 / 问答。连续对话 + 真流式（含思考过程）+ 发送/停止 + 历史会话抽屉 + 回填</summary>
public partial class AIDialogWindow : Window
{
    private readonly NoteService _noteService;
    private readonly AppSettings _settings;
    private readonly OpenAICompatibleProvider _provider;
    private bool _closed;
    private bool _drawerOpen;               // 历史抽屉展开状态
    private AgentToolRegistry? _registry; // Agent 工具注册表（AgentEnabled 时懒构建）
    private SkillCatalog? _skillCatalog;  // Skill 目录扫描（2026-09-20；带目录时间戳缓存，装完不必重启）
    private SkillRuntime? _skillRuntime;  // 内置 Python 运行时（只读状态，探测带缓存）
    private IReadOnlyList<SkillDependency>? _skillDeps; // 外部依赖表（2026-09-20；授权走应用内，不外包给用户）

    // ── 多会话并行（2026-09-21）：每个会话独立运行态，切会话不中断后台回答，后台流继续往各自 Bubbles 写 ──
    private ConversationRuntime? _active;   // 当前活跃（前台显示）的会话运行态
    private readonly Dictionary<string, ConversationRuntime> _runtimes = new(StringComparer.Ordinal);  // 按 SessionId 索引；后台正在回答的会话也常驻于此

    /// <summary>单个会话的运行态：把原先散在窗口级的会话状态收拢成每会话一个实例，使多会话可并行。
    /// 切会话 = 切 _active + 把 MessagesList.ItemsSource 指向目标 Bubbles；后台流照常往各自 Bubbles 写，切回即见。</summary>
    private sealed class ConversationRuntime
    {
        public ObservableCollection<ChatBubbleViewModel> Bubbles { get; } = new();
        public ChatSessionService Session { get; }
        public CancellationTokenSource? Cts;        // 当前回答的取消源；停止按钮 / 关窗时 Cancel
        public bool IsStreaming;                    // 该会话是否正在生成回答
        public ExplainMode Mode { get; }
        public NoteEntry? TargetNote { get; }        // 关联的目标笔记（翻译/搜索来源）
        public bool AgentRulesAdded;                // Agent 系统规则每会话只注入一次
        public string? DraftText;                   // 未发送草稿（切会话各自保留；关窗时落盘、重开回填）
        public bool Deleted;                        // 已删标记：其流跑完的 finally 不得再 Save，否则会复活已删文件

        // 本会话用户选过的文件牌号（2026-09-21，用户拍板）：
        // 卡片显示与对模型的注入都只认本会话的牌号 —— 切到别的会话看不见、模型也操作不了；
        // 切回本会话卡片恢复显示、AI 仍可操作。牌号本体仍留在 FileHandleStore（24h 过期不变），
        // TryResolve 保持全局：后台回答中的会话只要注入时见过牌号，工具调用就还能解析。
        public List<string> HandleIds { get; } = new();

        public ConversationRuntime(ChatSessionService session, ExplainMode mode, NoteEntry? targetNote)
        {
            Session = session;
            Mode = mode;
            TargetNote = targetNote;
        }
    }

    /// <summary>附件悬停预览 + 双击大图（输入区卡片与气泡卡片共用一份实例）</summary>
    private readonly AttachmentPreviewHost _preview = new();

    /// <summary>剪贴板诊断日志的节流：同一轮粘贴不刷屏</summary>
    private DateTime _lastClipboardLog = DateTime.MinValue;

    public AIDialogWindow(NoteService noteService, AppSettings settings)
    {
        _noteService = noteService;
        _settings = settings;
        // 2026-09-23 多供应商改造：改走统一解析入口（配置 → provider 的唯一映射点）
        _provider = AiModelResolver.CreateProvider(settings);
        InitializeComponent();
        DarkTitleBar.Enable(this);   // 2026-09-21：主动申请深色原生标题栏（WPF 默认白底，不申请就靠系统心情）
        // MessagesList.ItemsSource 在 Activate() 时按活跃会话绑定（多会话并行：切会话即切 Bubbles 源）
        InitInputArea();
        // 预览浮层预热：Popup 首次显示要创建宿主窗口（低配机上可感知），
        // 放到窗口加载完成后的空闲时机先开合一次，把这份开销挪到用户看不见的地方
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(_preview.Prewarm), DispatcherPriority.Background);
        // 侧边栏（2026-09-23 取代历史抽屉）。与旧抽屉同一条纪律：控件只抛事件，业务一律宿主执行。
        // 注意批量操作（多选）随旧抽屉一起退场了 —— 新侧边栏的会话菜单是单条操作。
        Sidebar.SessionSelected += item => Dispatcher.BeginInvoke(new Action(() => LoadHistorySession(item.FilePath)));
        Sidebar.SessionAction += (item, action, context) => Dispatcher.BeginInvoke(new Action(() => HandleItemAction(item, action, context)));
        Sidebar.GroupSelected += group => Dispatcher.BeginInvoke(new Action(() => HandleGroupSelected(group)));
        Sidebar.GroupAction += (group, action) => Dispatcher.BeginInvoke(new Action(() => HandleGroupAction(group, action)));
        Sidebar.NewChatRequested += () => Dispatcher.BeginInvoke(new Action(() => { CloseGroupView(); StartNewSession(_active?.Mode ?? ExplainMode.Ask, _active?.TargetNote); }));
        Sidebar.NewGroupRequested += () => Dispatcher.BeginInvoke(new Action(HandleNewGroup));
        Sidebar.RecycleBinRequested += () => Dispatcher.BeginInvoke(new Action(HandleRecycleBin));
        Deactivated += (_, _) => _preview.HoverLeave();   // 窗口失焦时鼠标可能已不在卡片上，预览要跟着收
        Closed += OnWindowClosed;

        // 云文件交付（2026-09-16）：工具在后台线程取回文件后，把卡片挂到当前气泡上。
        // 静态事件必须成对解绑（窗口会被 AIDialogHelper 重建），否则旧窗口实例泄漏。
        FileDeliveryHub.Delivered += OnFileDelivered;
        FileDeliveryHub.OpenRequested += OnFileOpenRequested;
        FileDeliveryHub.LocateRequested += OnFileLocateRequested;

        RefreshHandleChips();   // 句柄是进程级的，重开窗口时把仍有效的牌号摆回来
    }

    /// <summary>
    /// 以新模式/新目标开启一轮会话。selectedText 非空时自动发起第一轮。
    /// 全局问答入口（无 targetNote、无 selectedText）重复打开时保留原会话。
    /// </summary>
    public void OpenSession(ExplainMode mode, NoteEntry? targetNote = null, string? selectedText = null)
    {
        var isGlobalAskReopen = mode == ExplainMode.Ask
                                && targetNote == null
                                && string.IsNullOrEmpty(selectedText)
                                && _active != null
                                && _active.Mode == ExplainMode.Ask;

        if (!isGlobalAskReopen)
        {
            StartNewSession(mode, targetNote);

            if (!string.IsNullOrWhiteSpace(selectedText))
            {
                var firstMessage = mode == ExplainMode.Translate
                    ? PromptBuilder.BuildTranslatePrompt(selectedText.Trim())
                    : selectedText.Trim();
                // 用户消息统一由 SendAsync 加入会话与 UI，避免两条路径重复添加
                Dispatcher.BeginInvoke(new Action(() => SendAsync(firstMessage)));
            }
        }
        else
        {
            // 复用原会话：把前台切回当前活跃 runtime（可能正后台回答中）
            if (_active != null) Activate(_active);
        }

        // 注：空会话不在此 Save（阶段1 守卫：仅有 system 消息不落盘，避免打开窗口即生成空对话历史）
    }

    /// <summary>新建会话：建一个新 runtime 并切为活跃。**不中断当前活跃会话的后台回答**（多会话并行）。
    /// 当前活跃会话若仍空（未对话）且模式/目标笔记一致 → 复用，避免空 runtime 堆积。</summary>
    private void StartNewSession(ExplainMode mode, NoteEntry? targetNote)
    {
        // 复用仍空的当前会话：模式与目标笔记一致时直接接管，不另造一个空 runtime
        if (_active != null
            && !HasConversation(_active)
            && _active.Mode == mode
            && SameTargetNote(_active.TargetNote, targetNote))
        {
            _active.AgentRulesAdded = false;
            Activate(_active);
            return;
        }

        string? noteContext = null;
        string? noteContent = null;
        if (targetNote != null)
        {
            noteContext = targetNote.Timestamp.ToString("yyyy-MM-dd HH:mm");
            noteContent = targetNote.Content;
        }

        var session = new ChatSessionService(mode, noteContext, noteContent, _settings.AiToolResultLimit);
        var runtime = new ConversationRuntime(session, mode, targetNote);
        _runtimes[session.SessionId] = runtime;
        Activate(runtime);
    }

    /// <summary>把指定 runtime 切为活跃：消息列表指向其 Bubbles、按其状态刷新按钮与标题。</summary>
    private void Activate(ConversationRuntime runtime)
    {
        SaveActiveDraft();        // 切走前把当前输入框草稿存到旧 runtime（每会话各自保留）
        PersistActiveSession();   // 切走前把旧会话落盘，使它进历史列表、用户可点它切回看答案
        _active = runtime;
        MessagesList.ItemsSource = runtime.Bubbles;
        TitleText.Text = GetModeTitle(runtime.Mode);
        Title = GetModeTitle(runtime.Mode);
        SetBusyUi(runtime.IsStreaming);
        RefreshHandleChips();     // 牌号卡片跟会话走（2026-09-21）：切到哪个会话就摆哪个会话选的文件
        RestoreDraft(runtime);   // 回填目标 runtime 的草稿
        FocusInput();
    }

    /// <summary>把当前输入框的纯文本草稿存到活跃会话（切会话/关窗前调用）。</summary>
    private void SaveActiveDraft()
    {
        if (_active == null) return;
        var (text, _) = ExtractInput();   // 草稿只留纯文本；附件不跨会话保留（方案已确认）
        _active.DraftText = string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>切走/关窗前把活跃会话落盘（有对话才写；空会话不落盘由 ChatSessionService 守卫）。
    /// 目的：让仍在后台回答的会话进入历史列表，用户可点它切回、复用内存 runtime 看最新内容。</summary>
    private void PersistActiveSession()
    {
        if (_active == null || _active.Deleted) return;
        if (!HasConversation(_active)) return;
        try { _active.Session.Save(); } catch { /* best effort，落盘失败不阻断切换 */ }
    }

    /// <summary>把目标会话的草稿回填到输入框；无草稿则复位为空。</summary>
    private void RestoreDraft(ConversationRuntime runtime)
    {
        if (string.IsNullOrEmpty(runtime.DraftText))
        {
            ResetInput();
            return;
        }
        InsertPlainText(runtime.DraftText);
    }

    /// <summary>清空输入区并插入一段纯文本草稿（多行用 LineBreak 还原，与 ExtractInput 的换行语义对齐）。</summary>
    private void InsertPlainText(string text)
    {
        ResetInput();
        var doc = InputBox.Document;
        if (doc.Blocks.FirstBlock is not Paragraph p) return;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) p.Inlines.Add(new LineBreak());
            if (lines[i].Length > 0) p.Inlines.Add(new Run(lines[i]));
        }
        InputBox.CaretPosition = doc.ContentEnd;
        UpdatePlaceholder();
    }

    /// <summary>草稿文件路径：放数据根下（不进 chat_history，因此不被会话同步引擎扫描/上传；红线12 数据只留本机）。</summary>
    private static string DraftFilePath => FocusCapturePaths.Combine("ai_chat_draft.txt");

    /// <summary>把草稿落到本机文件；草稿为空则清掉文件，避免残留误回填。</summary>
    private static void SaveDraftToDisk(string? text)
    {
        try
        {
            var path = DraftFilePath;
            if (string.IsNullOrWhiteSpace(text))
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, Encoding.UTF8);
        }
        catch (Exception ex) { AppLog.Warn("AI", "草稿落盘失败：" + ex.Message); }
    }

    /// <summary>重开窗口时把上次关窗落盘的草稿回填到当前会话输入框，随后清盘（只回填一次）。</summary>
    internal void RestoreDraftFromDisk()
    {
        try
        {
            var path = DraftFilePath;
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path, Encoding.UTF8);
            File.Delete(path);   // 无论是否回填都清盘，避免下次重复回填
            if (string.IsNullOrWhiteSpace(text) || _active == null) return;

            var (cur, _) = ExtractInput();
            if (!string.IsNullOrEmpty(cur)) return;   // 输入框已有内容 → 不覆盖用户正在打的字

            _active.DraftText = text;
            InsertPlainText(text);
            FocusInput();
        }
        catch (Exception ex) { AppLog.Warn("AI", "草稿回填失败：" + ex.Message); }
    }

    /// <summary>runtime 是否已有实质对话（非 system 消息）</summary>
    private static bool HasConversation(ConversationRuntime runtime)
        => runtime.Session.Messages.Any(m => m.Role != ChatRoles.System);

    /// <summary>两个目标笔记是否同一（按时间戳判定；都为 null 视为一致）</summary>
    private static bool SameTargetNote(NoteEntry? a, NoteEntry? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return a.Timestamp == b.Timestamp;
    }

    private static string GetModeTitle(ExplainMode mode) => "AI " + AiModeText.Get(mode);

    private void AddBubble(ConversationRuntime runtime, bool isUser, string content, bool isFillable = false,
        IReadOnlyList<ChatAttachmentViewModel>? attachments = null)
    {
        runtime.Bubbles.Add(new ChatBubbleViewModel(isUser, content, isFillable, attachments));
        ScrollAfterDelay(runtime);
    }

    private void ScrollAfterDelay(ConversationRuntime? runtime)
    {
        // 仅当前活跃会话滚动：后台 runtime 的回调不抢前台滚动位置（内容照常写入各自 Bubbles，切回即见）
        if (runtime == null || _active != runtime) return;
        Dispatcher.BeginInvoke(new Action(ScrollToBottom), DispatcherPriority.Background);
    }

    private void ScrollToBottom()
    {
        MessagesScroll.ScrollToEnd();
    }

    // ══════════════════ 输入区：富文本 + 附件混排（2026-09-14） ══════════════════
    // 架构要点：输入区是 RichTextBox，附件是内联的原子块（InlineUIContainer）。
    // 顺序信息存在每个附件的 InsertOffset 上，上行请求时据此把正文切开、还原"文字-图-文字"的原序。

    /// <summary>
    /// 输入区初始化：空段落 + 接管粘贴/复制 + 接管拖拽 + 占位提示。
    ///
    /// 拖拽为什么要在输入框上再挂一遍：RichTextBox 内置了文本拖放处理，会先把 DragOver/Drop
    /// 标记成"已处理"，外层容器的处理器根本收不到。表现就是"鼠标停在输入框正上方放不进去、
    /// 偏上偏下（落在容器的内边距上）才能放" —— handledEventsToo: true 才能在它处理之后接管。
    /// </summary>
    private void InitInputArea()
    {
        ResetInput();
        DataObject.AddPastingHandler(InputBox, OnInputPaste);
        DataObject.AddCopyingHandler(InputBox, OnInputCopying);

        InputBox.AddHandler(UIElement.DragOverEvent, new DragEventHandler(InputArea_DragOver), true);
        InputBox.AddHandler(UIElement.DropEvent, new DragEventHandler(InputArea_Drop), true);

        InputBox.TextChanged += (_, _) => UpdatePlaceholder();
        UpdatePlaceholder();
    }

    /// <summary>清空输入区并恢复一个空段落（RichTextBox 至少要有一个 Block，否则无法输入）</summary>
    private void ResetInput()
    {
        _preview.HoverLeave();   // 卡片会被一起清掉，鼠标不会再触发 MouseLeave，就地收掉预览
        var doc = InputBox.Document;
        doc.Blocks.Clear();
        doc.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
        InputBox.CaretPosition = doc.ContentStart;
        UpdatePlaceholder();
    }

    /// <summary>占位提示只在"既没文字也没附件"时显示</summary>
    private void UpdatePlaceholder()
    {
        var text = new TextRange(InputBox.Document.ContentStart, InputBox.Document.ContentEnd).Text;
        var empty = string.IsNullOrWhiteSpace(text) && AttachmentCountInInput() == 0;
        InputPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把键盘焦点落到输入框。窗口是非模态弹出的，WPF 不会自动聚焦任何控件，必须显式调</summary>
    internal void FocusInput()
    {
        InputBox.Focus();
        Keyboard.Focus(InputBox);
    }

    /// <summary>
    /// 从输入区取出（正文, 附件有序列表）。
    /// 每个附件的 InsertOffset = 它插在正文第几个字符之后，这是"混排"顺序的唯一来源。
    /// </summary>
    private (string Text, List<ChatAttachment> Attachments) ExtractInput()
    {
        var attachments = new List<ChatAttachment>();
        var sb = new StringBuilder();

        foreach (var block in InputBox.Document.Blocks)
        {
            if (block is not Paragraph p) continue;
            foreach (var inline in p.Inlines)
            {
                switch (inline)
                {
                    case Run run:
                        sb.Append(run.Text);
                        break;
                    case LineBreak:
                        sb.Append('\n');
                        break;
                    case InlineUIContainer uc when uc.Tag is ChatAttachment a:
                        a.InsertOffset = sb.Length;
                        attachments.Add(a);
                        break;
                }
            }
            sb.Append('\n');   // 段落之间补换行
        }

        var raw = sb.ToString();
        var trimmed = raw.Trim();

        // 正文首尾空白被剪掉后要同步修正附件偏移，否则图会落到错误位置
        var startCut = raw.Length - raw.TrimStart().Length;
        if (startCut > 0)
            foreach (var a in attachments)
                a.InsertOffset = Math.Max(0, a.InsertOffset - startCut);

        return (trimmed, attachments);
    }

    private int AttachmentCountInInput() => InputBox.Document.Blocks
        .OfType<Paragraph>()
        .Sum(p => p.Inlines.OfType<InlineUIContainer>().Count());

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+C / Ctrl+X：选区含附件卡片时自己接管，原因见 HandleSelectionCopyForAttachment
        if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.C || e.Key == Key.X))
        {
            if (HandleSelectionCopyForAttachment(e.Key == Key.X)) e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers == ModifierKeys.Shift) return;   // Shift+回车 = 换行（保留默认行为）
        e.Handled = true;
        SendCurrentInput();
    }

    // ══════════════════ 复制 / 粘贴（2026-09-14 二版加固） ══════════════════

    /// <summary>
    /// 选区里含附件卡片时，复制/剪切走"只放纯文本"的简化路径。
    ///
    /// 为什么：卡片是 UIElement，默认复制会把整棵可视化树塞进剪贴板的富文本格式
    /// （实测选区里的卡片在纯文本里只留两个空格，图片本身跨应用传不出去）。
    /// 用户在输入框里按 Ctrl+C 想复制那段内容时，得到的要么是拿不到、要么是拿到一堆
    /// 别处认不出的格式 —— 表现就是"复制不出来"。主动降级成纯文本，行为可预期。
    /// </summary>
    private bool HandleSelectionCopyForAttachment(bool cut)
    {
        if (!SelectionHasAttachment()) return false;

        var text = InputBox.Selection.Text;
        if (cut) InputBox.Selection.Text = string.Empty;   // 剪切语义：原文变空

        try
        {
            // 剪贴板被其他程序占用时 SetText 会抛 CLIPBRD_E_CANT_OPEN，统一走 SafeClipboard 退避重试（失败不抛）
            if (!SafeClipboard.TrySetText(text, Clipboard.SetText))
            {
                AppLog.Error("AI", "复制选区失败：内容为空或剪贴板被其他程序占用（已退避重试）");
            }
            else
            {
                ClipboardHookService.MarkSelfCopy();   // 别让"剪贴板监控自动存笔记"把这次当成用户复制
                AppLog.Info("AI", $"复制选区（含附件卡片）：已降级为纯文本，{text.Length} 字");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("AI", "复制选区失败", ex);
        }

        UpdatePlaceholder();
        return true;
    }

    /// <summary>兜底：复制命令若从别的路径进来（右键菜单等），也把含卡片的选区降级成纯文本</summary>
    private void OnInputCopying(object sender, DataObjectCopyingEventArgs e)
    {
        if (e.IsDragDrop) return;              // 拖拽走自己的语义，不干预
        if (!SelectionHasAttachment()) return;

        e.CancelCommand();
        try
        {
            if (SafeClipboard.TrySetText(InputBox.Selection.Text, Clipboard.SetText))
                ClipboardHookService.MarkSelfCopy();
            else
                AppLog.Error("AI", "复制兜底处理失败：内容为空或剪贴板被其他程序占用（已退避重试）");
        }
        catch (Exception ex)
        {
            AppLog.Error("AI", "复制兜底处理失败", ex);
        }
    }

    /// <summary>选区里是否包含至少一个附件卡片</summary>
    private bool SelectionHasAttachment()
    {
        var selStart = InputBox.Selection.Start;
        var selEnd = InputBox.Selection.End;
        if (selStart.CompareTo(selEnd) == 0) return false;

        foreach (var block in InputBox.Document.Blocks)
        {
            if (block is not Paragraph p) continue;
            foreach (var c in p.Inlines.OfType<InlineUIContainer>())
            {
                var start = c.ElementStart;
                var end = c.ElementEnd;
                if (start == null || end == null) continue;
                if (start.CompareTo(selStart) >= 0 && end.CompareTo(selEnd) <= 0) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 粘贴拦截：剪贴板有文件或图片时接管为附件，其余情况放行走默认文本粘贴。
    ///
    /// 判据顺序（顺序本身就是规则）：
    /// ① 资源管理器复制的文件（含图片文件）→ 一定是附件
    /// ② 带实义文字 → 用户多半想粘文字（Word / 网页复制文本时剪贴板里也带位图），放行
    /// ③ 只剩位图 → 截图粘贴，接管
    ///
    /// 第 ③ 步取图不再只看 DataFormats.Bitmap：系统截图工具主要写的是 CF_DIB / PNG 格式，
    /// 只看 Bitmap 会漏掉，命中不到就什么都不做、也不提示 —— 那正是"粘不进去"的成因。
    /// 现在统一走系统剪贴板取图，并在取不到时留下可追查的日志。
    /// </summary>
    private void OnInputPaste(object sender, DataObjectPastingEventArgs e)
    {
        try
        {
            LogClipboardDiagnostics(e.DataObject);

            // ① 资源管理器里复制的文件
            if (TryGetClipboardFiles(e.DataObject) is { Length: > 0 } files)
            {
                e.CancelCommand();
                _ = AddFilesSafelyAsync(files);
                return;
            }

            // ② 带实义文字 → 放行默认文本粘贴
            if (TryGetClipboardText(e.DataObject) is { Length: > 0 })
                return;

            // ③ 只剩位图（截图）→ 当附件处理
            var image = TryGetClipboardImage(e.DataObject);
            if (image != null)
            {
                e.CancelCommand();
                _ = AddBitmapSafelyAsync(image);
                return;
            }

            // ④ 既没文字也没图：交回默认处理（可能是 RTF 等富文本），只留一行日志备查
            AppLog.Info("AI", "粘贴：未识别为附件，交回默认处理，格式=" + string.Join(",", SafeFormats(e.DataObject)));
        }
        catch (Exception ex)
        {
            AppLog.Error("AI", "粘贴处理异常", ex);
            e.CancelCommand();
            System.Windows.MessageBox.Show(this, "粘贴失败：" + ex.Message, "粘贴出错",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 把剪贴板里可见的格式写进日志。
    /// 粘贴类问题只能靠"真实剪贴板里到底有什么"来定位，光看代码永远猜不准；
    /// 同一轮里做节流，避免连续粘贴刷爆日志。
    /// </summary>
    private void LogClipboardDiagnostics(IDataObject? data)
    {
        if ((DateTime.Now - _lastClipboardLog).TotalSeconds < 1.5) return;
        _lastClipboardLog = DateTime.Now;
        try
        {
            var formats = SafeFormats(data);
            var textLen = TryGetClipboardText(data)?.Length ?? 0;
            var fileCount = TryGetClipboardFiles(data)?.Length ?? 0;
            AppLog.Info("AI", $"粘贴诊断：格式=[{string.Join(",", formats)}] 文本长度={textLen} 文件数={fileCount}");
        }
        catch
        {
            // 诊断本身不该影响粘贴
        }
    }

    private static string[] SafeFormats(IDataObject? data)
    {
        try { return data?.GetFormats() ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static string[]? TryGetClipboardFiles(IDataObject? data)
    {
        try
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
            return data.GetData(DataFormats.FileDrop) as string[];
        }
        catch { return null; }
    }

    private static string? TryGetClipboardText(IDataObject? data)
    {
        try
        {
            if (data == null || !data.GetDataPresent(DataFormats.UnicodeText)) return null;
            var s = data.GetData(DataFormats.UnicodeText) as string;
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
    }

    /// <summary>
    /// 从剪贴板取位图。两条路：
    /// 1) 粘贴事件自带的数据对象（Bitmap 格式，或只写 PNG 自定义格式的程序）—— 不碰系统剪贴板，无锁竞争
    /// 2) 系统剪贴板接口兜底 —— 覆盖只写 CF_DIB 的程序（系统截图工具就是这类）
    /// </summary>
    private static BitmapSource? TryGetClipboardImage(IDataObject? data)
    {
        try
        {
            if (data != null)
            {
                if (data.GetDataPresent(DataFormats.Bitmap)
                    && data.GetData(DataFormats.Bitmap) is BitmapSource bs
                    && EnsureFrozen(bs) is { } frozen)
                    return frozen;

                var pngName = DataFormats.GetDataFormat("PNG").Name;
                if (data.GetDataPresent(pngName)
                    && data.GetData(pngName) is Stream pngStream
                    && DecodeStream(pngStream) is { } decoded)
                    return decoded;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("AI", "从粘贴数据取图失败：" + ex.Message);
        }

        try { return WpfClipboard.GetImage(); }
        catch (Exception ex)
        {
            AppLog.Warn("AI", "从系统剪贴板取图失败：" + ex.Message);
            return null;
        }
    }

    private static BitmapSource? EnsureFrozen(BitmapSource src)
    {
        try
        {
            if (src.IsFrozen) return src;
            var clone = src.Clone();
            clone.Freeze();
            return clone;
        }
        catch { return null; }
    }

    private static BitmapSource? DecodeStream(Stream stream)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>fire-and-forget 包装：异常必须落到日志与提示，不能被静默吞掉（"点了没反应"的经典成因）</summary>
    private async Task AddFilesSafelyAsync(string[] files)
    {
        try { await AddFilesAsync(files); }
        catch (Exception ex)
        {
            AppLog.Error("AI", "添加文件附件失败", ex);
            System.Windows.MessageBox.Show(this, "添加附件失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task AddBitmapSafelyAsync(BitmapSource source)
    {
        try { await AddBitmapAsync(source); }
        catch (Exception ex)
        {
            AppLog.Error("AI", "添加图片附件失败", ex);
            System.Windows.MessageBox.Show(this, "添加图片失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>发送/停止一体按钮：回答中点击 = 停止生成，空闲时点击 = 发送</summary>
    private void BtnSend_Click(object sender, RoutedEventArgs e)
    {
        if (_active != null && _active.IsStreaming)
        {
            StopStreaming();
            return;
        }
        SendCurrentInput();
    }

    private void SendCurrentInput()
    {
        if (_active != null && _active.IsStreaming) return; // 回答中 Enter 不发送也不清空输入框，防误触丢字

        var (text, attachments) = ExtractInput();
        if (string.IsNullOrEmpty(text) && attachments.Count == 0) return;

        // 双保险：设置里关了图片发送时，即使图片已贴在输入区也不上行。
        // 提示后保留输入区内容，不擅自丢弃用户已经准备好的东西。
        if (!_settings.AiVisionEnabled && attachments.Any(a => a.Kind == ChatAttachmentKind.Image))
        {
            ShowVisionDisabledTip();
            return;
        }

        ResetInput();
        if (_active != null) _active.DraftText = null;   // 发送成功即清该会话草稿
        SendAsync(text, attachments);
    }

    // ── 附件添加入口：加号 / 粘贴 / 拖拽 三处共用 ──

    private void BtnAttach_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图片或文档",
            Filter = ChatAttachmentService.FileDialogFilter,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        _ = AddFilesAsync(dlg.FileNames);
    }

    // ── 选择文件：只签发牌号，不复制、不发给模型（2026-09-16） ──

    /// <summary>
    /// 「选择文件」入口。**只做一件事：给用户点过的文件签发一个牌号。**
    /// 与旁边的「+」是两种语义：「+」是把文件当附件发给模型看，这里是让模型能对它动手（存网盘等）。
    /// 由于牌号只在本机生成、模型无法编造，AI 的可达范围就被严格限定在用户亲手点过的文件上。
    /// </summary>
    private void BtnPickFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要交给 AI 操作的文件",
            Filter = "所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        foreach (var path in dlg.FileNames)
        {
            try { FileHandleStore.Register(path); }
            catch (Exception ex) { AppLog.Warn("AI", "登记文件句柄失败：" + ex.Message); }
        }
        RefreshHandleChips();
        FocusInput();
    }

    /// <summary>刷新已选文件卡片区（无内容时整块收起，不占高度）。</summary>
    private void RefreshHandleChips()
    {
        var items = FileHandleStore.Snapshot();
        HandleChips.ItemsSource = items;
        HandleChips.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HandleChip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // 双击 = 用系统默认程序打开（确认选对了文件）。单击不做事，避免误触。
        if (e.ClickCount < 2) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not FileHandleInfo info) return;
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(info.Path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "打开失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void HandleChipRemove_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is string id)
        {
            FileHandleStore.Remove(id);
            _active?.HandleIds.Remove(id);   // 卡片跟会话走：从当前会话摘掉
            RefreshHandleChips();
        }
    }

    // ── 云文件卡片的三个交付动作（2026-09-16） ──

    private void CloudFile_Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CloudFileCardViewModel card) OpenCloudFile(card);
    }

    private void CloudFile_Locate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CloudFileCardViewModel card) LocateCloudFile(card);
    }

    private void CloudFile_SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CloudFileCardViewModel card) SaveCloudFileAs(card);
    }

    /// <summary>
    /// 云文件卡片的第四个动作：彻底删除（2026-09-16）。
    ///
    /// <b>刻意只给用户、不给 AI</b>：破坏性动作不上工具，这是项目红线。理由很具体，不是洁癖 ——
    /// AI 手上只有牌号、看不见文件内容也看不见你的上下文，"把上周那三个没用的删了"这种话它只会自己挑，
    /// 而你无从察觉挑错；网盘删除又只进网盘回收站，恢复得自己去网盘客户端。
    /// AI 可以**引导**用户点这个按钮（说「请点卡片上的彻底删除」），但不能自己执行。
    /// </summary>
    private async void CloudFile_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not CloudFileCardViewModel card) return;

        var confirm = System.Windows.MessageBox.Show(this,
            $"彻底删除「{card.Model.Name}」？\n\n" +
            "· 本机这份会被删除，这张卡片随即失效\n" +
            "· 网盘上那份会一并删除（网盘回收站可找回）\n" +
            "· 在本应用内不可撤销",
            "彻底删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var (ok, message) = await FileRepository.DeletePermanentlyAsync(card.Model.Id);
            // 云端删不掉时 message 里已如实写明（含去哪补删），照原样交给用户，不做美化
            System.Windows.MessageBox.Show(this, message, ok ? "彻底删除" : "删除失败",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (ok) RemoveCloudFileCard(card);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "删除失败：" + ex.Message, "彻底删除",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>摘掉同一文件的所有卡片（同 id 可能挂在多个气泡上），并刷新「是否有云文件卡片」。</summary>
    private void RemoveCloudFileCard(CloudFileCardViewModel card)
    {
        // 云文件卡片可能挂在任一会话（含后台回答中）的气泡上，遍历全部 runtime 摘卡
        foreach (var b in _runtimes.Values.SelectMany(r => r.Bubbles).ToList())
        {
            var hit = b.CloudFiles.FirstOrDefault(c => c.Model.Id == card.Model.Id);
            if (hit == null) continue;
            b.CloudFiles.Remove(hit);
            b.NotifyCloudFilesChanged();
        }
    }

    private void OpenCloudFile(CloudFileCardViewModel card)
    {
        if (!EnsureCloudFileLocal(card, out var path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "打开失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LocateCloudFile(CloudFileCardViewModel card)
    {
        if (!EnsureCloudFileLocal(card, out var path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "定位失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveCloudFileAs(CloudFileCardViewModel card)
    {
        if (!EnsureCloudFileLocal(card, out var path)) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存为",
            FileName = card.Model.Name,
            Filter = "所有文件|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.Copy(path, dlg.FileName, overwrite: true);
            System.Windows.MessageBox.Show(this, "已保存到：\n" + dlg.FileName, "另存为",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "保存失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool EnsureCloudFileLocal(CloudFileCardViewModel card, out string path)
    {
        path = card.LocalPath ?? "";
        if (path.Length > 0 && File.Exists(path)) return true;
        System.Windows.MessageBox.Show(this,
            "这个文件还没取回到本机。\n\n可以让 AI 再取一次（说「把 XX 取回来」），或在「设置 → 文件与网盘」里查看记录。",
            "文件不在本机", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    // ── 工具交付回调（后台线程 → UI） ──

    private void OnFileDelivered(FileMetadata meta) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closed) return;
        var runtime = _active;
        if (runtime == null) return;
        var bubble = runtime.Bubbles.LastOrDefault(b => !b.IsUser) ?? runtime.Bubbles.LastOrDefault();
        bubble?.AddCloudFile(meta);
        ScrollAfterDelay(runtime);
    }));

    private void OnFileOpenRequested(FileMetadata meta) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closed) return;
        var card = new CloudFileCardViewModel(meta);
        if (card.LocalPath != null) OpenCloudFile(card);
    }));

    private void OnFileLocateRequested(FileMetadata meta) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closed) return;
        var card = new CloudFileCardViewModel(meta);
        if (card.LocalPath != null) LocateCloudFile(card);
    }));

    /// <summary>
    /// 界面快照专用（诊断工具用，正常流程不调用）：造几张「已选择文件」卡片，
    /// 让句柄卡片区在快照里能渲染出来。正常流程下这块只在用户真点过「选择文件」时才出现，
    /// 而它正是本次红线（AI 只能引用牌号）的界面落点，值得一张图。
    /// </summary>
    internal void SeedHandleChipsForSnapshot()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "fc-snapshot-handles");
            Directory.CreateDirectory(dir);
            foreach (var name in new[] { "季度报告.docx", "截图-20260916.png", "数据表.xlsx" })
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) File.WriteAllText(path, "snapshot");
                FileHandleStore.Register(path);
            }
            RefreshHandleChips();
        }
        catch { /* 快照辅助失败不影响主流程 */ }
    }

    /// <summary>网盘清单同步完成后由 MainWindow 调用：刷新句柄卡片（可能有已过期的）。</summary>
    internal void RefreshFileViews()
    {
        try { RefreshHandleChips(); } catch { /* 刷新失败不打断对话 */ }
    }

    /// <summary>
    /// 界面快照专用（诊断工具用，正常流程不调用）：造两张「云文件」卡片。
    ///
    /// 云文件卡片只在 AI 真把文件取回并交付时才渲染，其它快照场景覆盖不到它。
    /// 2026-09-16 给卡片加了第四个按钮（彻底删除）—— 一行四个按钮正是"被控件挤出可视区"的高危形态，
    /// 静态读 XAML 看不出来，只能出图。这里刻意不走 OnFileDelivered（它经 Dispatcher 异步排队，
    /// 快照可能在渲染前就完成了），直接同步挂卡片。
    /// </summary>
    /// <summary>快照专用：无活跃会话时建一个空的，避免快照方法访问 _active 落空。</summary>
    private void EnsureActiveForSnapshot()
    {
        if (_active == null) StartNewSession(ExplainMode.Ask, null);
    }

    internal void SeedCloudFileCardsForSnapshot()
    {
        try
        {
            EnsureActiveForSnapshot();
            var runtime = _active;
            if (runtime == null) return;
            AddBubble(runtime, false, "两份文件都取回来了：可以直接打开、在文件夹中定位、另存，确认不要了也能彻底删除。");
            var bubble = runtime.Bubbles.LastOrDefault(b => !b.IsUser);
            if (bubble == null) return;

            foreach (var (name, size) in new[]
                     {
                         ("2026中国OPC白皮书.pdf", 16103185L),
                         ("灵感_2026-09-16.md", 1737L),
                     })
            {
                bubble.AddCloudFile(new FileMetadata
                {
                    Id = "snap-cloud-" + name,
                    Name = name,
                    NetPath = "/apps/FocusCapture/files/" + name,
                    Size = size,
                    Type = FileTypes.Upload,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                });
            }
        }
        catch { /* 快照辅助失败不影响主流程 */ }
    }

    /// <summary>当前会话出现过的全部附件（链路 B 的输入：工具只能引用其中之一，不能凭空指定路径）。</summary>
    private IReadOnlyList<ChatAttachment> CurrentSessionAttachments()
    {
        var session = _active?.Session;
        if (session == null) return Array.Empty<ChatAttachment>();
        return session.Messages
            .Where(m => m.Attachments is { Count: > 0 })
            .SelectMany(m => m.Attachments!)
            .ToList();
    }

    /// <summary>
    /// 把一批文件**直接落进输入区**（悬浮球拖放保存用，2026-09-16）。
    /// 与「加号 / 粘贴 / 拖到输入框」三条入口的差别：这条是"从外面带着文件来的"，
    /// 所以先清空输入区再落 —— 复用单例窗口时，上一轮没发出去的残留附件会跟新附件堆在一起，
    /// 用户看到的是"怎么自己多出两个文件"。
    /// 调用时机硬要求：必须等窗口 Show() 之后再调（AddFilesAsync 失败要弹 MessageBox，owner 尚未显示会出问题）。
    /// </summary>
    public void AddAttachmentPaths(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0) return;
        ResetInput();
        _ = AddFilesAsync(paths);
    }

    /// <summary>把一批本地文件加进输入区；不支持的格式逐个收集原因，最后一次性告知</summary>
    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var errors = new List<string>();
        var accepted = 0;

        foreach (var path in paths)
        {
            if (AttachmentCountInInput() >= ChatAttachmentService.MaxAttachmentsPerMessage)
            {
                errors.Add($"单条消息最多 {ChatAttachmentService.MaxAttachmentsPerMessage} 个附件");
                break;
            }
            if (!ChatAttachmentService.IsSupported(path))
            {
                errors.Add($"{Path.GetFileName(path)}：不支持的格式");
                continue;
            }
            if (ChatAttachmentService.IsSupportedImage(path) && !EnsureVisionAllowed()) return;

            var (att, error) = await ChatAttachmentService.CreateFromFileAsync(path, _settings.AiImageQualityLevel);
            if (att == null)
            {
                errors.Add($"{Path.GetFileName(path)}：{error}");
                continue;
            }
            InsertAttachmentChip(att);
            accepted++;
        }

        if (accepted > 0) FocusInput();
        if (errors.Count > 0)
            System.Windows.MessageBox.Show(this, string.Join("\n", errors), "部分附件未添加",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>把剪贴板位图加进输入区（截图粘贴）</summary>
    private async Task AddBitmapAsync(BitmapSource source)
    {
        if (!EnsureVisionAllowed()) return;
        if (AttachmentCountInInput() >= ChatAttachmentService.MaxAttachmentsPerMessage)
        {
            System.Windows.MessageBox.Show(this, $"单条消息最多 {ChatAttachmentService.MaxAttachmentsPerMessage} 个附件", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = $"截图-{DateTime.Now:yyyyMMdd-HHmmss}.png";
        var att = await ChatAttachmentService.CreateFromBitmapAsync(source, name, _settings.AiImageQualityLevel);
        if (att == null)
        {
            System.Windows.MessageBox.Show(this, "图片处理失败，请重试", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        InsertAttachmentChip(att);
        FocusInput();
    }

    private void InputArea_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void InputArea_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            e.Handled = true;
            _ = AddFilesAsync(files);
        }
    }

    private bool EnsureVisionAllowed()
    {
        if (_settings.AiVisionEnabled) return true;
        ShowVisionDisabledTip();
        return false;
    }

    private void ShowVisionDisabledTip()
    {
        System.Windows.MessageBox.Show(this,
            "当前已关闭「允许发送图片」，图片不会被添加或发送。\n\n" +
            "如果所用模型支持图片输入，请在「设置 → AI 功能」中打开该开关。",
            "图片发送已关闭", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── 附件块（InlineUIContainer）：原子节点，整块删除、光标跨不过去 ──

    /// <summary>
    /// 在光标处插入附件卡片。
    /// 选富文本方案（而非"文字框 + 下方附件条"）的核心收益就在这里：
    /// 附件是文档里的原子节点，删不掉一半、复制不乱序。
    /// </summary>
    private void InsertAttachmentChip(ChatAttachment att)
    {
        var pos = InputBox.CaretPosition;
        var insertAt = pos.GetInsertionPosition(LogicalDirection.Forward) ?? pos;

        var container = new InlineUIContainer(BuildAttachmentChip(att, removable: true), insertAt)
        {
            Tag = att,
            // 卡片比一行文字高，默认的基线对齐会让卡片悬在半空、文字沉到下面（看着就是"没调平"）。
            // 改成行内垂直居中，文字与卡片的中线才在一条线上。
            BaselineAlignment = BaselineAlignment.Center,
        };

        // 插完把光标移到块之后：否则光标可能仍停在块前，用户接着打字会插到附件之前
        var after = container.ElementEnd?.GetInsertionPosition(LogicalDirection.Forward);
        if (after != null) InputBox.CaretPosition = after;
        InputBox.Focus();
        UpdatePlaceholder();
    }

    /// <summary>
    /// 构造附件卡片。输入区（removable=true，带移除叉）与气泡（removable=false）共用同一套视觉 ——
    /// 用户在输入框里看到什么形态，发出去就该是什么形态。
    /// </summary>
    private FrameworkElement BuildAttachmentChip(ChatAttachment att, bool removable)
    {
        var isImage = att.Kind == ChatAttachmentKind.Image;

        var text = new TextBlock
        {
            Text = ChatAttachmentViewModel.ChipLabel(att),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(text);

        if (removable)
        {
            var remove = new TextBlock
            {
                Text = "×",
                FontSize = 13,
                Margin = new Thickness(6, 0, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "移除该附件",
            };
            remove.MouseLeftButtonUp += (_, _) => RemoveAttachmentChip(att);
            panel.Children.Add(remove);
        }

        var chip = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
            BorderBrush = new SolidColorBrush(isImage
                ? Color.FromRgb(0x37, 0x8A, 0xDD)
                : Color.FromRgb(0x88, 0x87, 0x80)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = panel,
        };

        WireAttachmentChip(chip, att);
        return chip;
    }

    /// <summary>给卡片接上悬停预览与双击（输入区卡片与气泡卡片共用同一套行为）</summary>
    private void WireAttachmentChip(FrameworkElement chip, ChatAttachment att)
    {
        chip.MouseEnter += (_, _) => _preview.HoverEnter(att);
        chip.MouseLeave += (_, _) => _preview.HoverLeave();
        chip.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount < 2) return;
            e.Handled = true;
            OpenAttachment(att);
        };
    }

    /// <summary>双击附件：图片开大图窗，文档交给系统默认程序</summary>
    private void OpenAttachment(ChatAttachment att)
    {
        var path = ChatAttachmentService.ResolvePath(att);
        if (!File.Exists(path))
        {
            System.Windows.MessageBox.Show(this, "这个附件只存在原设备上，本机没有拷贝。", "附件不在本机",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (att.Kind == ChatAttachmentKind.Image)
        {
            var src = AttachmentPreviewHost.LoadFullImage(path);
            if (src == null)
            {
                System.Windows.MessageBox.Show(this, "图片读取失败，无法放大显示。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 先收掉悬停预览：它是独立顶层窗口，不收会浮在大图窗上面
            _preview.HoverLeave();
            new ImagePreviewWindow(att.FileName, src) { Owner = this }.ShowDialog();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "打开失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── 气泡卡片的三个事件（挂载在气泡模板里） ──

    private void AttachmentChip_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatAttachmentViewModel vm)
            _preview.HoverEnter(vm.Model);
    }

    private void AttachmentChip_MouseLeave(object sender, MouseEventArgs e) => _preview.HoverLeave();

    private void AttachmentChip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ChatAttachmentViewModel vm) return;
        if (e.ClickCount < 2) return;
        e.Handled = true;
        OpenAttachment(vm.Model);
    }

    private void RemoveAttachmentChip(ChatAttachment att)
    {
        _preview.HoverLeave();   // 卡片马上消失，鼠标不会再触发 MouseLeave
        foreach (var block in InputBox.Document.Blocks)
        {
            if (block is not Paragraph p) continue;
            var target = p.Inlines.OfType<InlineUIContainer>().FirstOrDefault(c => ReferenceEquals(c.Tag, att));
            if (target != null)
            {
                p.Inlines.Remove(target);
                break;
            }
        }
        FocusInput();
        UpdatePlaceholder();
    }

    /// <summary>
    /// 仅供 --snapshot 界面自检（2026-09-14）：在输入区放一个附件 chip、在消息区放一条带图的用户消息，
    /// 用于核验这两处新界面的真实渲染 —— 空会话快照走不到这两个分支。
    /// 正常启动路径（不带 --snapshot）永远不会调用它。
    /// </summary>
    internal void SeedAttachmentsForSnapshot()
    {
        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2D, 0x6E, 0xB4)), null, new Rect(0, 0, 512, 288));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)), null, new Rect(32, 32, 448, 64));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)), null, new Rect(32, 128, 200, 120));
            }
            var bmp = new RenderTargetBitmap(512, 288, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();

            var att = ChatAttachmentService
                .CreateFromBitmapAsync(bmp, "快照示例.png", 1)
                .GetAwaiter().GetResult();
            if (att == null) return;

            // 输入区专门摆成"文字 + 图片卡片 + 文字 + 文档卡片"：
            // 快照要看的就是卡片与同排文字到底对没对齐，以及两种卡片的配色差异。
            // InsertTextInRun 不移动插入点，写完文字要手动推到文末，否则卡片会插到文字前面。
            ResetInput();
            var seedText = "看看这张图：";
            InputBox.CaretPosition = InputBox.Document.ContentStart;
            InputBox.CaretPosition.InsertTextInRun(seedText);
            InputBox.CaretPosition = InputBox.CaretPosition.GetPositionAtOffset(seedText.Length, LogicalDirection.Forward)
                                     ?? InputBox.CaretPosition;
            InsertAttachmentChip(att);

            var docAtt = CreateSnapshotDocument();
            if (docAtt != null)
            {
                var seedText2 = " 顺便看下这份文档：";
                InputBox.CaretPosition.InsertTextInRun(seedText2);
                InputBox.CaretPosition = InputBox.CaretPosition.GetPositionAtOffset(seedText2.Length, LogicalDirection.Forward)
                                         ?? InputBox.CaretPosition;
                InsertAttachmentChip(docAtt);
            }

            var bubbleItems = new List<ChatAttachment> { att };
            if (docAtt != null) bubbleItems.Add(docAtt);
            EnsureActiveForSnapshot();
            _active!.Bubbles.Add(new ChatBubbleViewModel(true, "这是带附件的消息示例", false,
                BuildAttachmentVms(bubbleItems)));
        }
        catch
        {
            // 快照是辅助手段，任一环节失败都不该阻断整轮快照
        }
    }

    /// <summary>快照专用：造一份真实文档附件，用于核验文档卡片（灰色边框）的渲染</summary>
    private static ChatAttachment? CreateSnapshotDocument()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "fc-snapshot-sample.md");
            File.WriteAllText(path, "# 示例文档\n这是一份用于界面自检的示例文档，用来核验长文件名截断与文档卡片配色。");
            var (doc, _) = ChatAttachmentService.CreateFromFileAsync(path, 1).GetAwaiter().GetResult();
            return doc;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把消息附件转成气泡展示项（无附件返回 null，模板据此隐藏整块）</summary>
    private static IReadOnlyList<ChatAttachmentViewModel>? BuildAttachmentVms(IReadOnlyList<ChatAttachment>? attachments)
    {
        if (attachments is not { Count: > 0 }) return null;
        return attachments.Select(a => new ChatAttachmentViewModel(a)).ToList();
    }

    private void StopStreaming()
    {
        // 停止当前活跃会话的回答（发送/停止按钮用）。后台会话不被打扰，切回时再停。
        try { _active?.Cts?.Cancel(); } catch (ObjectDisposedException) { /* 已释放即已结束 */ }
    }

    /// <summary>发送一条消息（可带附件）并流式接收回复（普通/Agent 两路径统一：真流式 + 思考过程 + 可停止）</summary>
    private async void SendAsync(string text, List<ChatAttachment>? attachments = null)
    {
        var runtime = _active;
        if (runtime == null || runtime.IsStreaming) return;
        if (string.IsNullOrWhiteSpace(text) && attachments is not { Count: > 0 }) return;

        var cts = new CancellationTokenSource();
        runtime.Cts = cts;
        runtime.IsStreaming = true;
        SetBusyUi(true);

        try
        {
            AddBubble(runtime, true, text, attachments: BuildAttachmentVms(attachments));
            AddBubble(runtime, false, "", runtime.TargetNote != null && runtime.Mode != ExplainMode.Ask);
            var current = runtime.Bubbles[runtime.Bubbles.Count - 1];
            current.Content = "思考中…"; // 首包到达前的等待占位

            if (_settings.AgentEnabled && runtime.Mode == ExplainMode.Ask)
            {
                // Agent 路径：function calling 流式循环（RunAsync 内部负责把用户消息写入会话）
                await SendViaAgentAsync(runtime, text, attachments, current, cts);
            }
            else
            {
                // 普通问答路径：用户消息入会话 + 事件流式接收（正文/思考）
                // （AddUser 不可省：请求体 messages 无 user 会导致 Agnes 400 "No user query" / DeepSeek 自说自话）
                runtime.Session.AddUser(text, attachments);
                await StreamPlainReplyAsync(runtime, current, cts);
            }

            // 上下文裁剪是隐形的（设计稿 §6 坑⑤）：本轮真丢了消息就如实告诉用户。
            // 只写进气泡的**显示内容**，不进会话历史 —— 这是我们给的提示，不是模型说的话。
            // 放在两条路径的共同收尾处而不是各路径内部：写两处必然漏一处。
            var trimHint = _provider.LastTrimHint;
            if (!string.IsNullOrWhiteSpace(trimHint))
                current.Content = (current.Content ?? "") + "\n\n（" + trimHint + "）";
        }
        catch (Exception ex)
        {
            var last = runtime.Bubbles.Count > 0 ? runtime.Bubbles[runtime.Bubbles.Count - 1] : null;
            if (last != null && !last.IsUser)
            {
                last.Content += string.IsNullOrEmpty(last.Content)
                    ? $"（错误：{ex.Message}）"
                    : $"\n\n（错误：{ex.Message}）";
            }
            else
            {
                AddBubble(runtime, false, $"（错误：{ex.Message}）");
            }
        }
        finally
        {
            runtime.IsStreaming = false;
            if (ReferenceEquals(runtime.Cts, cts)) runtime.Cts = null;
            cts.Dispose();
            // 仅当该 runtime 仍是前台活跃会话时才复位发送按钮；后台完成的 runtime 不打扰前台按钮状态
            if (_active == runtime) SetBusyUi(false);
            if (!runtime.Deleted) runtime.Session.Save();   // 已删会话不复活；多会话并行各自落盘（阶段1 空会话守卫仍生效）
            // 后台 runtime 跑完时新会话已进历史列表，刷新展开的抽屉让用户看见
            if (_active != runtime) _ = Dispatcher.BeginInvoke(new Action(RefreshDrawerIfOpen));
        }
    }

    /// <summary>分组指令的注入文本（现读 + 带来源与从属标注）。
    /// 实现搬到 <see cref="ChatGroupService.BuildInstructionContext"/> —— 放服务层才守得住
    /// 「必须显式标注从属关系，不得覆盖系统红线」这条安全要求（窗口的私有方法测不到）。</summary>
    private static string BuildGroupInstructionContext(ChatSessionService session)
        => ChatGroupService.BuildInstructionContext(session.GroupId);

    /// <summary>
    /// 普通问答路径的请求消息 = 会话历史 +（可选）一条**临时附加**的分组指令 system 消息。
    ///
    /// 为什么不复用 Agent 路径的 ExtraSystemContext：那条通道挂在 AgentRunService 上，
    /// 不开 Agent 工具时根本不走。这里手动加一份不写回会话历史的副本，让两条路径行为一致。
    /// </summary>
    private static List<ChatMessage> BuildPlainRequestMessages(ConversationRuntime runtime)
    {
        var groupInstruction = BuildGroupInstructionContext(runtime.Session);
        var messages = runtime.Session.Messages.ToList();
        if (groupInstruction.Length > 0)
            messages.Add(new ChatMessage(ChatRoles.System, groupInstruction));
        return messages;
    }

    /// <summary>普通问答路径：消费 StreamChatWithToolsAsync 事件流（无 tools），正文打字机 + 思考过程展示。
    /// 用户停止时已生成的部分内容照常写入会话历史。</summary>
    private async Task StreamPlainReplyAsync(ConversationRuntime runtime, ChatBubbleViewModel current, CancellationTokenSource cts)
    {
        var sb = new StringBuilder();
        try
        {
            await foreach (var ev in _provider.StreamChatWithToolsAsync(BuildPlainRequestMessages(runtime), tools: null, cts.Token))
            {
                // 多会话并行：不再因切会话丢弃旧流；取消由 cts.Token 触发 OperationCanceledException
                switch (ev)
                {
                    case StreamChatEvent.ReasoningDelta reasoning:
                        AppendReasoning(current, reasoning.Text);
                        break;
                    case StreamChatEvent.ContentDelta delta:
                        sb.Append(delta.Text);
                        current.Content = sb.ToString();
                        ScrollAfterDelay(runtime);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止/关窗取消：已生成部分写入会话历史
            MarkStopped(current, sb.ToString());
            if (!string.IsNullOrWhiteSpace(sb.ToString()))
                runtime.Session.AddAssistant(sb.ToString());
            return;
        }

        CollapseReasoning(current);
        var full = sb.ToString();
        if (string.IsNullOrWhiteSpace(full))
        {
            current.Content = "（模型未返回内容）";
        }
        else
        {
            runtime.Session.AddAssistant(full);
        }
    }

    /// <summary>
    /// Agent 路径：function calling 流式循环。工具调用步骤实时追加到气泡；正文/思考增量打字机展示；
    /// 写操作（非只读工具）执行前经 ConfirmHandler 弹窗确认。用户停止时不把部分内容写入会话
    /// （中断可能落在 assistant(tool_calls) 与 tool 结果配对之间，写入不完整配对会让后续请求 400）。
    /// </summary>
    private async Task SendViaAgentAsync(ConversationRuntime runtime, string text, List<ChatAttachment>? attachments,
        ChatBubbleViewModel current, CancellationTokenSource cts)
    {
        EnsureAgentRegistry();
        AppendAgentRulesOnce(runtime);
        var agent = new AgentRunService(_provider, _registry!, runtime.Session, _settings.AgentMaxToolRounds)
        {
            // 必须经 UiThread 封送：工具跑在线程池线程上，直接 MessageBox.Show(this, …) 会因
            // 跨线程访问窗口对象而抛「调用线程无法访问此对象」（2026-09-20 实测，详见 UiThread 注释）。
            ConfirmHandler = desc => UiThread.AskAsync(Dispatcher, () => System.Windows.MessageBox.Show(
                this,
                $"AI 请求执行以下操作：\n\n{desc}\n\n确认执行？",
                "AI 操作确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK,
                msg => AppLog.Warn("Agent", msg)),
            WriteConfirmEnabled = _settings.AgentWriteConfirmPopup,
            // 每轮把「当前时间」+「用户当前选中的文件牌号」告诉模型。放这里而不是会话历史里：
            // 它们是随手会变的短期状态，写进历史既污染持久化数据，也会让翻旧会话时看到过期值。
            // ⚠️ 时间**绝不能**改放系统提示词 —— 那个会被持久化进会话文件（ChatSessionService.Save），
            // 跨天之后里面的日期就是错的，比不告诉模型更糟（2026-09-17）。
            ExtraSystemContext = () =>
            {
                var sb = new StringBuilder();
                sb.Append("当前时间：").Append(PromptBuilder.DescribeNow(DateTime.Now))
                  .Append("。用户说「今天/明天/上周/这个月」等相对时间时，一律以这个时间为基准。");

                var handleText = FileHandleStore.DescribeForModel(runtime.HandleIds);   // 按会话注入（2026-09-21）
                if (handleText.Length > 0) sb.Append('\n').Append(handleText);

                // Skill 清单（2026-09-20）：与「当前时间」「文件牌号」同类 —— 随手会变（装/删 Skill），
                // 但没必要写进会话历史。走这条既有通道，主循环一行都不用改。
                var skills = _skillCatalog?.GetSkills();
                if (skills is { Count: > 0 })
                    sb.Append('\n').Append(SkillManifest.Build(skills, _skillRuntime?.IsPresent ?? false));

                // 分组指令（2026-09-23）：与「当前时间」「文件牌号」同类的短期状态 —— 现读、随时可变、不进历史。
                // 刻意走这条通道而不是 AppendSystemRules：那条会把文本写进会话文件，
                // 于是用户改了指令之后，旧会话里存的还是上一版，而模型每轮又都看得见它（对不上账）。
                var groupInstruction = BuildGroupInstructionContext(runtime.Session);
                if (groupInstruction.Length > 0) sb.Append('\n').Append(groupInstruction);

                return sb.ToString();
            },
        };

        var gate = new object(); // agentSb / finished 由事件线程与 UI 线程共同访问
        var agentSb = new StringBuilder();
        var finished = false;

        agent.StatusCallback = msg =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                current.ToolSteps = string.IsNullOrEmpty(current.ToolSteps) ? "· " + msg : current.ToolSteps + "\n· " + msg;
            }));
        };
        agent.ContentDelta += t =>
        {
            lock (gate)
            {
                if (finished) return;
                agentSb.Append(t);
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (gate)
                {
                    if (finished) return;
                    current.Content = agentSb.ToString();
                }
                ScrollAfterDelay(runtime);
            }));
        };
        agent.ReasoningDelta += t =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Volatile.Read(ref finished)) return;
                AppendReasoning(current, t);
            }));
        };

        try
        {
            var reply = await agent.RunAsync(text, attachments, cts.Token);
            lock (gate) finished = true;
            CollapseReasoning(current);
            current.Content = string.IsNullOrWhiteSpace(reply) ? "（模型未返回内容）" : reply;
        }
        catch (OperationCanceledException)
        {
            lock (gate) finished = true;
            CollapseReasoning(current);
            string partial;
            lock (gate) partial = agentSb.ToString();
            MarkStopped(current, partial);
        }
    }

    /// <summary>思考过程追加：首次出现时自动展开折叠区</summary>
    private static void AppendReasoning(ChatBubbleViewModel bubble, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (string.IsNullOrEmpty(bubble.ReasoningText)) bubble.IsReasoningOpen = true;
        bubble.ReasoningText += text;
    }

    /// <summary>回答结束收起思考过程区（内容保留可再展开）</summary>
    private static void CollapseReasoning(ChatBubbleViewModel bubble)
    {
        if (bubble.HasReasoning) bubble.IsReasoningOpen = false;
    }

    /// <summary>回答被用户停止：已生成部分保留，气泡标注（已停止）</summary>
    private static void MarkStopped(ChatBubbleViewModel bubble, string partial)
    {
        bubble.Content = string.IsNullOrWhiteSpace(partial) || bubble.Content == "思考中…"
            ? "（已停止）"
            : partial + "\n\n（已停止）";
    }

    /// <summary>发送/停止按钮状态机：回答中变红色停止图标，空闲恢复发送</summary>
    private void SetBusyUi(bool busy)
    {
        if (_closed) return;   // 窗口已关闭不刷按钮（关窗后后台 runtime 完成的回调不应再动 UI）
        if (busy)
        {
            BtnSend.Content = "■ 停止";
            BtnSend.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73));
            BtnSend.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73));
            BtnSend.ToolTip = "停止生成";
        }
        else
        {
            BtnSend.Content = "发送";
            BtnSend.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
            BtnSend.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
            BtnSend.ToolTip = null;
            InputBox.Focus();
        }
        // 输入框回答期间保持可用（可预输入下一条），发送动作由 _active.IsStreaming 守卫拦截
    }

    /// <summary>历史抽屉开关：展开时刷新会话列表；宽度动画滑出/收起</summary>
    private void BtnHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!_drawerOpen)
            RefreshDrawer();
        OpenDrawer(!_drawerOpen);
    }

    // ── 抽屉布局（阶段二）：宽度参数化 + 拖拽 + 跨启动记忆 ──

    private const double DrawerMinWidth = 150;   // 防拖没了
    private const double DrawerMaxWidth = 480;

    /// <summary>展开/收起抽屉（"历史"按钮与抽屉内收起按钮共用）。
    /// 动画仍作用于 Sidebar.Width（铁律：不动 ColumnDefinition）；展开宽度 = 设置记忆宽度（240 参数化）。
    /// 收起状态不记忆——下次展开仍用记忆宽度。</summary>
    private void OpenDrawer(bool open)
    {
        _drawerOpen = open;
        DrawerSplitter.IsEnabled = false; // 动画期间禁用拖拽，避免与动画打架（铁律 2）
        if (open)
        {
            Sidebar.MinWidth = 0;   // 动画从 0 长到目标，MinWidth 边界动画结束后恢复
            RefreshDrawer();
        }
        else
        {
            Sidebar.MinWidth = 0;   // 收到 0 需先解除 MinWidth 顶住
        }

        var target = open ? Math.Clamp(_settings.AiDrawerWidth, DrawerMinWidth, DrawerMaxWidth) : 0;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            // 2026-09-21 修复"抽屉拖不动"：BeginAnimation 默认 HoldEnd —— 动画播完后仍占着 Width 属性，
            // 之后 DragDelta 对 Width 的赋值全被它覆盖，拖拽形同虚设（用户反馈：有双箭头但拖不动）。
            // 先落一个本地值再解除占用，属性不会回落。收起后输入区左下圆角恢复（抽屉不在了，窗口角归它）。
            Sidebar.Width = target;
            Sidebar.BeginAnimation(WidthProperty, null);
            InputArea.CornerRadius = _drawerOpen ? new CornerRadius(0, 0, 0, 6) : new CornerRadius(0, 0, 6, 6);
            if (_drawerOpen) Sidebar.MinWidth = DrawerMinWidth;
            DrawerSplitter.IsEnabled = true;
        };
        Sidebar.BeginAnimation(WidthProperty, anim);
    }

    /// <summary>抽屉宽度拖拽：直接改 Sidebar.Width（不改 ColumnDefinition，铁律 2），有边界。</summary>
    private void DrawerSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (!_drawerOpen) return;
        Sidebar.Width = Math.Clamp(Sidebar.ActualWidth + e.HorizontalChange, DrawerMinWidth, DrawerMaxWidth);
    }

    /// <summary>拖拽结束：宽度记忆（收起状态不记忆）</summary>
    private void DrawerSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (!_drawerOpen || Sidebar.Width <= 0) return;
        _settings.AiDrawerWidth = Sidebar.Width;
        _settings.Save();
    }

    /// <summary>刷新抽屉列表（展开中才刷新；启动下拉/操作完成后宿主调用）</summary>
    private void RefreshDrawer()
    {
        if (!_drawerOpen) return;
        Sidebar.Load(ChatSessionService.ListSessions(), ChatGroupStore.Load());
        // 昵称与头像跟着设置走：在设置里改完，下次刷新侧边栏就同步（不必重开窗口）
        Sidebar.SetUser(_settings.ChatUserNickname, ChatAssetsService.LoadUserAvatar());
        // 分组视图开着的话一并刷（在组里新建会话、删会话之后要立刻反映出来）
        RefreshGroupView();
    }

    /// <summary>同步周期完成后的外部刷新入口（MainWindow 经 AIDialogHelper 调用，拉到新会话立即上列表）</summary>
    internal void RefreshDrawerIfOpen() => RefreshDrawer();

    // ── 历史会话管理（阶段二）：条目操作 / 批量操作 / 分组管理 / 回收站 ──

    /// <summary>条目操作（三个点菜单）。改字段统一走"内存会话优先"：改的就是当前打开的会话时
    /// 直接改内存字段（否则文件与内存漂移），否则 Load 文件改后 Save。</summary>
    private void HandleItemAction(HistoryItemViewModel item, ChatItemAction action, string? context)
    {
        switch (action)
        {
            // 批量操作（多选）入口随旧抽屉一起退场：新侧边栏是单条操作。
            // 枚举值 ChatItemAction.BatchStart 保留 —— 不为一个不再发出的动作去动公共枚举。

            case ChatItemAction.Rename:
            {
                var name = PromptDialog.Show(this, "重命名会话", "会话标题（留空恢复默认预览）：");
                if (name == null) return;
                ApplySessionMeta(item, s => s.Title = name);
                RefreshDrawer();
                break;
            }

            case ChatItemAction.TogglePin:
                ApplySessionMeta(item, s => s.Pinned = !s.Pinned);
                RefreshDrawer();
                break;

            case ChatItemAction.Group:
                ApplySessionMeta(item, s => s.GroupId = context ?? "");
                RefreshDrawer();
                break;

            // 收藏与分组共用 GroupId 一个字段 —— 收藏就是内置保留分区。
            // 所以「收藏」= 把归属改成收藏；「取消收藏」= 回到未分组。
            // 归属互斥是用户定的语义：一条会话要么在某分组、要么在收藏，不会同时在两边。
            case ChatItemAction.Favorite:
                ApplySessionMeta(item, s => s.GroupId = ChatGroupStore.IsFavorite(s.GroupId)
                    ? ""
                    : ChatGroupStore.FavoriteId);
                RefreshDrawer();
                break;

            case ChatItemAction.Export:
                if (Enum.TryParse<ExportFormat>(context, out var fmt))
                    ExportOne(item, fmt);
                break;

            case ChatItemAction.Delete:
                DeleteSessions([item]);
                break;
        }
    }

    // HandleBatchAction 已删除（2026-09-23）：批量操作入口随旧抽屉退场，新侧边栏是单条操作。
    // 它用到的 DeleteSessions / ExportMany 都还在（单条删除与单条导出走它们），功能没丢。

    /// <summary>新建分组（侧边栏「新分组」）。查重与落盘统一走 ChatGroupService，与其它入口行为一致。</summary>
    private void HandleNewGroup()
    {
        var name = PromptDialog.Show(this, "新建分组", "分组名称：");
        if (string.IsNullOrWhiteSpace(name)) return;

        ChatGroupService.Create(name, out var result);
        if (result == ChatGroupService.CreateResult.NameExists)
        {
            MessageBox.Show(this, "已存在同名分组", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RefreshDrawer();
    }

    /// <summary>点侧边栏里的某个分组（收藏也走这里）→ 主区切到分组视图</summary>
    private void HandleGroupSelected(SidebarGroupHeader group) => OpenGroupView(group.GroupId, group.Name);

    /// <summary>分组三点菜单：重命名 / 置顶此分组 / 删除此分组</summary>
    private void HandleGroupAction(SidebarGroupHeader group, GroupMenuAction action)
    {
        switch (action)
        {
            case GroupMenuAction.Rename:
            {
                var name = PromptDialog.Show(this, "重命名分组", "新名称：", group.Name);
                if (string.IsNullOrWhiteSpace(name) || name == group.Name) return;
                if (!ChatGroupService.Rename(group.GroupId, name, out var error) && error.Length > 0)
                    MessageBox.Show(this, error, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            }

            // 「置顶此分组」= 把该分组内的会话全部置顶。
            // 没做「分组本身排序靠前」是因为那要给 ChatGroup 加字段、再配一套跨端合并裁决；
            // 而用户的原话是"置顶此分组"，组内会话都浮到「置顶」区在语义上也成立。
            // 若他要的是分组行排到分组列表最前，换实现即可（不动数据模型）—— 已记入待确认清单。
            case GroupMenuAction.Pin:
                SetPinForGroup(group.GroupId, pinned: true);
                break;

            case GroupMenuAction.Delete:
            {
                if (MessageBox.Show(this,
                        $"删除分组「{group.Name}」？组内会话将回到未分组（会话本身不删除）。",
                        "删除分组", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                    return;
                ChatGroupService.DeleteGroup(group.GroupId, out _);
                break;
            }
        }
        RefreshDrawer();
    }

    /// <summary>把某分组内所有会话批量置顶（逐条 Load→改→Save，Rev 自增随同步管道传到他端）。</summary>
    private static void SetPinForGroup(string groupId, bool pinned)
    {
        var dir = FocusCapturePaths.Combine("chat_history");
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var svc = ChatSessionService.Load(file);
                if (svc == null || !string.Equals(svc.GroupId, groupId, StringComparison.Ordinal)) continue;
                if (svc.Pinned == pinned) continue;   // 已是该状态就不写盘（省掉无谓的 Rev 自增与上传）
                svc.Pinned = pinned;
                svc.Save();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FocusCapture] 批量置顶跳过文件: {file}: {ex.Message}");
            }
        }
    }

    // ── 分组视图（2026-09-23）：点侧边栏里的分组进来 ──

    private string _activeGroupId = "";   // 当前打开的分组（空 = 不在分组视图里）

    /// <summary>
    /// 打开分组视图：主区切成该分组的会话列表。
    /// 输入区**不动** —— 在分组视图里输入就是在该分组下新建会话，
    /// 这正是用户要的"开对话之前先选好分组"，而不是对话完了再归类。
    /// </summary>
    private void OpenGroupView(string groupId, string groupName)
    {
        _activeGroupId = groupId;

        var isFavorite = ChatGroupStore.IsFavorite(groupId);
        GroupViewTitle.Text = isFavorite ? "★  " + groupName : groupName;

        var instruction = ChatGroupService.GetInstruction(groupId);
        GroupInstructionText.Text = instruction.Length > 0 ? "分组指令：" + instruction : "";
        GroupInstructionText.Visibility = instruction.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 收藏是**视图**不是真分组：不能改名 / 删除 / 加指令。按钮直接隐藏，不给"点了没反应"的假入口。
        var editable = isFavorite ? Visibility.Collapsed : Visibility.Visible;
        BtnGroupInstruction.Visibility = editable;
        BtnGroupRename.Visibility = editable;
        BtnGroupDelete.Visibility = editable;

        GroupViewPanel.Visibility = Visibility.Visible;
        MessagesScroll.Visibility = Visibility.Collapsed;

        RefreshGroupView();
        InputBox.Focus();
    }

    /// <summary>退出分组视图，回到普通对话</summary>
    private void CloseGroupView()
    {
        if (_activeGroupId.Length == 0) return;
        _activeGroupId = "";
        GroupViewPanel.Visibility = Visibility.Collapsed;
        MessagesScroll.Visibility = Visibility.Visible;
    }

    /// <summary>刷新分组视图的会话列表（只列本分组；顺序由 ListSessions 保证 = 置顶优先 → 时间倒序）</summary>
    private void RefreshGroupView()
    {
        if (_activeGroupId.Length == 0) return;

        var items = ChatSessionService.ListSessions()
            .Where(s => string.Equals(s.GroupId, _activeGroupId, StringComparison.Ordinal))
            .Select(s => new HistoryItemViewModel(
                s.Id, s.FilePath, s.SavedAt, AiModeText.Get(s.Mode),
                string.IsNullOrWhiteSpace(s.Title) ? s.Preview : s.Title,
                s.Pinned, s.GroupId, "", s.Preview))
            .ToList();

        GroupSessionList.ItemsSource = items;
        GroupSessionEmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnGroupViewClose_Click(object sender, RoutedEventArgs e) => CloseGroupView();

    private void GroupSessionRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HistoryItemViewModel item }) return;
        CloseGroupView();                       // 打开某条会话 = 退出分组视图，回到对话
        LoadHistorySession(item.FilePath);
    }

    /// <summary>添加 / 修改分组指令（多行输入）</summary>
    private void BtnGroupInstruction_Click(object sender, RoutedEventArgs e)
    {
        var current = ChatGroupService.GetInstruction(_activeGroupId);
        var text = PromptDialog.Show(this, "添加指令",
            "在这个分组里，你说的话默认是对谁说的？\n" +
            "例：我的一切指令默认对象都是得到大脑（于是你说「上传笔记」即可，不必每次带「到得到大脑」）。\n" +
            "注意：它每次发消息都会附给模型，但**不能覆盖**系统规则，两者冲突时以系统规则为准。",
            current, multiline: true);
        if (text == null) return;               // 取消

        ChatGroupService.SetInstruction(_activeGroupId, text);
        RefreshDrawer();
        OpenGroupView(_activeGroupId, ChatGroupService.GetGroupName(_activeGroupId));   // 重刷标题与指令展示
    }

    private void BtnGroupRename_Click(object sender, RoutedEventArgs e)
    {
        var current = ChatGroupService.GetGroupName(_activeGroupId);
        var name = PromptDialog.Show(this, "编辑分组名", "新名称：", current);
        if (string.IsNullOrWhiteSpace(name) || name == current) return;

        if (!ChatGroupService.Rename(_activeGroupId, name, out var error))
        {
            if (error.Length > 0)
                MessageBox.Show(this, error, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RefreshDrawer();
        OpenGroupView(_activeGroupId, name);
    }

    private void BtnGroupDelete_Click(object sender, RoutedEventArgs e)
    {
        var groupId = _activeGroupId;
        var name = ChatGroupService.GetGroupName(groupId);
        if (name.Length == 0) { CloseGroupView(); return; }

        if (MessageBox.Show(this,
                $"删除分组「{name}」？组内会话将回到未分组（会话本身不删除）。",
                "删除分组", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        ChatGroupService.DeleteGroup(groupId, out _);
        CloseGroupView();
        RefreshDrawer();
    }

    // ── 快照专用（只在 --snapshot 分支调用，正常启动不受影响；与其它 Seed*ForSnapshot 同套做法）──

    private string _snapshotGroupId = "";   // 快照造出来的分组 Id（分组视图那张图要用）

    /// <summary>
    /// 快照：造几条会话与分组并展开侧边栏。
    /// 沙箱里一个会话都没有 —— 不塞数据这张图只有"新对话 / 新分组"两个按钮，等于没验。
    /// 刻意不走 OpenDrawer（180ms 动画）：快照要的是稳定终态，不是动画中间帧。
    /// </summary>
    internal void SeedSidebarForSnapshot()
    {
        // 每个快照场景都 new 一个窗口，但**沙箱是同一个** —— 不清掉上一个场景造的数据，
        // 第二张图里就会出现两套一模一样的会话（实测踩过：10c 的侧边栏里每条都重复了一遍，
        // 而且"得到大脑"撞名建不出来 → 分组视图压根没打开，图上看不出这个错）。
        // 只在沙箱里清：快照工具启动时把 RootOverride 指向临时目录，这里再加一道判断兜底。
        if (!string.IsNullOrEmpty(FocusCapturePaths.RootOverride))
        {
            var chatDir = FocusCapturePaths.Combine("chat_history");
            if (Directory.Exists(chatDir))
                foreach (var file in Directory.EnumerateFiles(chatDir, "*.json")) File.Delete(file);

            var groupsFile = FocusCapturePaths.Combine("chat_groups.json");
            if (File.Exists(groupsFile)) File.Delete(groupsFile);
        }

        var devGroup = ChatGroupService.Create("项目开发", out _);
        var brainGroup = ChatGroupService.Create("得到大脑", out _);
        if (brainGroup != null)
        {
            ChatGroupService.SetInstruction(brainGroup.Id, "我的一切指令默认对象都是得到大脑");
            _snapshotGroupId = brainGroup.Id;
        }

        void MakeSession(string text, bool pinned, string? groupId)
        {
            var svc = new ChatSessionService(ExplainMode.Ask);
            svc.AddUser(text);
            svc.AddAssistant("好的，记下了。");
            if (!string.IsNullOrEmpty(groupId)) svc.GroupId = groupId;
            svc.Pinned = pinned;
            svc.Save();
        }

        // 覆盖三个分区：最近（未分组）/ 置顶（跨分组）/ 分组内
        MakeSession("帮我整理一下这周的待办", false, null);
        MakeSession("灵感：银发经济的专题要不要做", false, null);
        MakeSession("把这份资料归档到项目里", true, devGroup?.Id);
        MakeSession("上传笔记", false, brainGroup?.Id);

        _settings.ChatUserNickname = "彭杰";

        _drawerOpen = true;
        Sidebar.Width = 220;
        Sidebar.MinWidth = DrawerMinWidth;
        RefreshDrawer();
    }

    /// <summary>快照：在侧边栏数据之上再点进某个分组（验分组视图的排版）</summary>
    internal void SeedGroupViewForSnapshot()
    {
        SeedSidebarForSnapshot();
        if (_snapshotGroupId.Length > 0) OpenGroupView(_snapshotGroupId, "得到大脑");
    }

    private void HandleRecycleBin()
    {
        new ChatTrashWindow { Owner = this }.ShowDialog();
        RefreshDrawer();
    }

    /// <summary>改单会话元数据：当前打开的会话改内存字段（保持内存与文件一致），否则 Load → 改 → Save。
    /// Save 自动自增 Rev + 触发 SessionChanged（同步管道入口），重命名/置顶/分组无需额外接线。</summary>
    private void ApplySessionMeta(HistoryItemViewModel item, Action<ChatSessionService> mutate)
    {
        // 优先改内存里的 runtime（含后台回答中的）：它的下次 Save 会带上新 meta，不会被覆盖丢失
        if (_runtimes.TryGetValue(item.Id, out var rt))
        {
            mutate(rt.Session);
            rt.Session.Save();
            return;
        }
        var svc = ChatSessionService.Load(item.FilePath);
        if (svc == null) return;
        mutate(svc);
        svc.Save();
    }

    /// <summary>删除（单项/批量共用）：确认弹窗 → 当前打开的会话先切断内存引用（新会话接管，防后续 Save 复活文件）
    /// → 经 AIDialogHelper.SessionDeleted 走 MarkDeleted（trash + 删除清单 + Notify，闭环①）。</summary>
    private void DeleteSessions(IReadOnlyList<HistoryItemViewModel> items)
    {
        var tip = items.Count == 1
            ? "删除该会话？会移入会话回收站。"
            : $"删除选中的 {items.Count} 个会话？将移入会话回收站。";
        if (System.Windows.MessageBox.Show(this, tip, "删除会话",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        foreach (var item in items)
        {
            // 内存里有 runtime（含后台回答中）→ 先 cancel 它再移除，避免流还在往已删会话写
            if (_runtimes.TryGetValue(item.Id, out var rt))
            {
                rt.Deleted = true;   // 标记已删：其流跑完的 finally 不得再 Save，否则会把移入回收站的文件复活
                try { rt.Cts?.Cancel(); } catch (ObjectDisposedException) { /* 已结束 */ }
                _runtimes.Remove(item.Id);
                if (_active == rt)
                    StartNewSession(rt.Mode, null);   // 活跃会话被删 → 新建空会话接管前台
            }
            AIDialogHelper.SessionDeleted?.Invoke(item.Id);
        }
    }

    /// <summary>单会话导出：SaveFileDialog 选位置（默认文件名 = 标题/预览）</summary>
    private void ExportOne(HistoryItemViewModel item, ExportFormat format)
    {
        var raw = ChatSessionService.LoadFile(item.FilePath);
        if (raw == null)
        {
            System.Windows.MessageBox.Show(this, "会话文件损坏或无法读取", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出会话",
            Filter = "所有文件|*.*",
            FileName = NoteExportService.SanitizeFileName(ChatExportService.ResolveTitle(raw)) + ChatExportService.ExtensionOf(format),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var path = File.Exists(dlg.FileName) ? NoteExportService.GetUniquePath(dlg.FileName) : dlg.FileName;
            ChatExportService.ExportTo(raw, format, path);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"导出失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>批量导出：选目录 → 每个会话各导出一个文件（同名自动 _1/_2 不覆盖）</summary>
    private void ExportMany(IReadOnlyList<HistoryItemViewModel> items, ExportFormat format)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择导出目录（选中的每个会话各导出一个文件，重名自动加序号不覆盖）",
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var ok = 0;
        var fail = 0;
        foreach (var item in items)
        {
            try
            {
                var raw = ChatSessionService.LoadFile(item.FilePath);
                if (raw == null) { fail++; continue; }
                var name = NoteExportService.SanitizeFileName(ChatExportService.ResolveTitle(raw));
                if (string.IsNullOrEmpty(name)) name = "会话";
                var path = NoteExportService.GetUniquePath(Path.Combine(dlg.SelectedPath, name + ChatExportService.ExtensionOf(format)));
                ChatExportService.ExportTo(raw, format, path);
                ok++;
            }
            catch { fail++; }
        }

        System.Windows.MessageBox.Show(this,
            $"导出完成：成功 {ok} 个{(fail > 0 ? $"，失败 {fail} 个" : "")}。\n目录：{dlg.SelectedPath}",
            "批量导出", MessageBoxButton.OK,
            fail > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    /// <summary>加载历史会话回看（可继续对话）。**不中断当前活跃会话的后台回答**：若该会话已在内存（活跃/后台跑），直接切过去保留运行态。</summary>
    private void LoadHistorySession(string filePath)
    {
        var loaded = ChatSessionService.Load(filePath);
        if (loaded == null)
        {
            System.Windows.MessageBox.Show(this, "会话文件损坏或无法读取", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 已是本窗口内存中的 runtime（活跃或后台跑）→ 直接切过去，保留运行态与已生成气泡
        if (_runtimes.TryGetValue(loaded.SessionId, out var existing))
        {
            Activate(existing);
            return;
        }

        var runtime = new ConversationRuntime(loaded, loaded.Mode, null)
        {
            // Agent 会话的 system 消息里已含规则文本，避免继续对话时重复注入
            AgentRulesAdded = loaded.Messages.Count > 0
                && loaded.Messages[0].Role == ChatRoles.System
                && loaded.Messages[0].Content.Contains("[Agent 工具规则]")
        };
        _runtimes[loaded.SessionId] = runtime;
        foreach (var m in loaded.Messages)
        {
            if (m.Role == ChatRoles.User) runtime.Bubbles.Add(new ChatBubbleViewModel(true, m.Content, attachments: BuildAttachmentVms(m.Attachments)));
            else if (m.Role == ChatRoles.Assistant) runtime.Bubbles.Add(new ChatBubbleViewModel(false, m.Content));
            // tool / assistant(tool_calls) 中间消息不渲染为气泡
        }
        Activate(runtime);
    }

    private void BtnNewSession_Click(object sender, RoutedEventArgs e)
    {
        CloseGroupView();   // 在分组视图里点「新会话」= 明确要回到普通对话
        // 新会话保留当前模式与目标笔记；StartNewSession 内部已处理复用空会话，空会话不落盘
        StartNewSession(_active?.Mode ?? ExplainMode.Ask, _active?.TargetNote);
    }

    /// <summary>Agent 模式系统规则（每个会话只注入一次）：以工具结果为事实来源 + 写操作先在对话中征询</summary>
    private void AppendAgentRulesOnce(ConversationRuntime runtime)
    {
        if (runtime.AgentRulesAdded) return;
        runtime.Session.AppendSystemRules(
            "[Agent 工具规则]\n" +
            "1. 工具执行返回的结果是唯一事实来源：工具返回成功才可以说完成；返回失败必须如实告知。严禁在没有调用工具、或工具未返回成功的情况下宣称已完成任何操作。\n" +
            "2. 执行任何写操作（新增/修改/删除笔记或待办）之前，必须先在回复中列出将要执行的具体动作，等用户明确同意后再调用工具执行。\n" +
            "3. 没有对应工具的能力就直说做不到，不要编造替代方案的结果。\n" +
            "4. 引用或修改某条笔记/待办时，用列表/搜索工具输出中方括号里的时间戳作为 ref_time 定位。\n" +
            "5. 只根据工具真正返回的内容作答：文件读不出文字时（如扫描件 PDF、图片）必须如实说读不了，绝不许编造文件里没有的内容；工具返回的是部分数据（只列了前 N 行/条）时要说明这是部分。");
        runtime.AgentRulesAdded = true;
    }

    /// <summary>装配工具注册表：本地工具 + 各外发目的地能力（新增目的地在此注册一行）</summary>
    private void EnsureAgentRegistry()
    {
        if (_registry != null) return;
        var registry = new AgentToolRegistry();
        registry.Register(new SearchNotesTool(_noteService));
        registry.Register(new ListTodosTool(_noteService));
        registry.Register(new SaveQuickNoteTool(_noteService));
        registry.Register(new CreateTodoTool(_noteService));
        registry.Register(new CompleteTodoTool(_noteService));
        registry.Register(new ReopenTodoTool(_noteService));
        // 改内容（2026-09-17）：原地替换 + 旧内容进回收站。此前只有"改状态"，用户说"把那条改一下"AI 只能答做不到。
        registry.Register(new UpdateNoteTool(_noteService));
        registry.Register(new DeleteNoteTool(_noteService));

        // 查询与统计（2026-09-17）：AI 此前看不到「某天记了什么」「这个月记了多少」
        registry.Register(new ListNotesByDateTool(_noteService));
        registry.Register(new NoteStatsTool(_noteService));

        // 回收站（2026-09-17）：只给「看」与「恢复」。
        // 明确不给「彻底删除 / 清空回收站」—— 破坏性动作不给 AI，这是项目既有红线。
        registry.Register(new ListRecycleBinTool(_noteService));
        registry.Register(new RestoreDeletedTool(_noteService));

        // 导出（2026-09-17）：落到设置里的默认导出文件夹，**不上传网盘**（用户明确要求）
        registry.Register(new ExportNotesTool(_noteService, _settings));

        // 文件类工具（2026-09-16，五条链路）：
        // 这是本次更新的基座 —— 没有这几个工具，网盘接入就退化成一个需要手动操作的同步盘。
        // 注意它们**没有路径参数**：链路的可达范围由用户点过的牌号（handle）与本地元数据编号（file_id）决定，
        // 模型编不出这两样东西，因此拿不到没被授权过的本机文件。
        registry.Register(new StoreFileToCloudTool(CurrentSessionAttachments));
        registry.Register(new FindCloudFilesTool());
        registry.Register(new FetchCloudFileTool());
        registry.Register(new ReadCloudFileTool());
        // 文件记录维护 + 文档读取（2026-09-17）：read_cloud_file 已支持 docx/xlsx/pdf；
        // update_file_meta 只改本机显示名/标签，**不动云端文件名**（描述里已写明，防模型谎报"已改名"）。
        registry.Register(new UpdateFileMetaTool());
        registry.Register(new ReadSpreadsheetTool());
        registry.Register(new ReadPdfTool());

        var getNote = new GetNoteDestination(_settings);
        foreach (var capability in getNote.Capabilities)
            registry.Register(new OutboundTool(getNote, capability));

        // Skill 运行时（2026-09-20）：扫描 Skills\ 目录 + 挂两个工具（load_skill / run_skill_script）。
        // 核心逻辑全在 Services/Skills/ 里，这里只是装配 —— 出问题可整目录删掉回退，不牵存量功能。
        _skillCatalog = new SkillCatalog(FocusCapturePaths.Combine("Skills"), msg => AppLog.Warn("Skill", msg));
        _skillRuntime = new SkillRuntime(AppContext.BaseDirectory);
        _skillDeps = SkillDependencies.All(AppContext.BaseDirectory);   // 外部依赖表（应用自带优先，其次 PATH）

        var scriptRunner = new SkillScriptRunner(_skillCatalog, _skillRuntime, dependencies: _skillDeps)
        {
            TrustedSkills = new HashSet<string>(_settings.SkillTrusted, StringComparer.OrdinalIgnoreCase),
            OnTrusted = RememberSkillTrust,
            TrustPrompt = ConfirmSkillTrustAsync,
            AuthPrompt = ConfirmSkillAuthAsync,
        };
        registry.Register(new LoadSkillTool(_skillCatalog, _skillDeps));
        registry.Register(new RunSkillScriptTool(scriptRunner));

        _registry = registry;
    }

    /// <summary>
    /// Skill 准入确认（2026-09-20）：某个 Skill 第一次要跑脚本时问一次，允许后记进设置，之后不再问。
    ///
    /// <para>
    /// 为什么不每次弹：会烦死人。为什么不能不弹：脚本执行的风险比"新增一条笔记"高一个量级，
    /// 沿用写工具"默认不弹窗"的既有策略不合适。**准入式确认是这一整套安全模型的地基** ——
    /// 脚本跑起来之后读什么、连什么，宿主管不了，所以防线只能放在"谁被允许跑"上。
    /// </para>
    /// 
    /// <para>撤销入口在「设置 → Skill」；只能授权不能撤销的安全机制是残缺的。</para>
    /// </summary>
    /// <para>
    /// ⚠ <b>必须经 <see cref="UiThread"/> 封送回 UI 线程</b> —— 本方法是被
    /// <see cref="SkillScriptRunner"/> 在**工具线程**（线程池，非 UI 线程）上调用的，
    /// 直接在这里 <c>MessageBox.Show(this, …)</c> 会抛「调用线程无法访问此对象」。
    /// </para>
    /// <summary>
    /// Skill 依赖授权（2026-09-20）：某个 Skill 依赖的外部 CLI（如 lark-cli）还没登录时，
    /// **在应用内**把标准设备码流走完 —— 弹二维码、用户扫一次、窗口自动关闭。
    ///
    /// <para>
    /// 为什么必须有这一环：授权原本是完全空白的，模型只能照着 CLI 输出里的提示自己发挥 ——
    /// 实测它让用户去终端敲 <c>lark-cli auth login</c>，还把开发机上的安装目录拼进了命令里。
    /// **该由宿主做的事外包给用户，比 bug 本身更贵。**
    /// </para>
    /// <para>
    /// ⚠ 本方法在**工具线程**（线程池）上被调用 —— 必须经 <see cref="UiThread"/> 封送回 UI 线程，
    /// 否则 <c>ShowDialog</c> 会因跨线程访问窗口对象而抛「调用线程无法访问此对象」。
    /// </para>
    /// <para>返回 false = 用户没完成授权；执行器据此拒绝执行脚本，绝不假装成功。</para>
    /// </summary>
    private Task<bool> ConfirmSkillAuthAsync(SkillDependency dep, DependencyStatus status) =>
        UiThread.AskAsync(Dispatcher, () =>
        {
            var window = new SkillAuthWindow(dep, status) { Owner = this };
            return window.ShowDialog() == true;
        }, msg => AppLog.Warn("Skill", msg));

    private Task<bool> ConfirmSkillTrustAsync(SkillInfo skill) =>
        UiThread.AskAsync(Dispatcher, () => ShowSkillTrustDialog(skill), msg => AppLog.Warn("Skill", msg));

    /// <summary>准入确认弹窗本体 —— <b>只允许被 <see cref="ConfirmSkillTrustAsync"/> 在 UI 线程上调用</b></summary>
    private bool ShowSkillTrustDialog(SkillInfo skill)
    {
        var scripts = skill.ScriptFiles.Count > 0 ? string.Join("、", skill.ScriptFiles) : "（无）";
        var msg =
            $"允许 Skill「{skill.Name}」在你的电脑上执行脚本吗？\n\n" +
            $"目录：{skill.RootPath}\n" +
            $"脚本：{scripts}\n\n" +
            "允许后，这个 Skill 再次执行脚本时不再询问（可在「设置 → Skill」里撤销）。\n\n" +
            "注意：脚本运行起来之后做什么，本应用拦不住 —— 请只允许你信任来源的 Skill。";

        var ok = System.Windows.MessageBox.Show(this, msg, "Skill 执行确认",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

        AppLog.Info("Skill", $"准入确认：{skill.Name} → {(ok ? "允许" : "用户取消")}");
        return ok;
    }

    /// <summary>把刚授权的 Skill 名落盘（去重后 Save）</summary>
    private void RememberSkillTrust(string skillName)
    {
        if (_settings.SkillTrusted.Any(s => string.Equals(s, skillName, StringComparison.OrdinalIgnoreCase)))
            return;

        _settings.SkillTrusted.Add(skillName);
        try
        {
            _settings.Save();
            AppLog.Info("Skill", $"已记住授权：{skillName}");
        }
        catch (Exception ex)
        {
            // 记不住不影响本次执行（内存里已加入），只是下次会再问一遍
            AppLog.Warn("Skill", $"保存 Skill 授权失败：{ex.Message}");
        }
    }

    /// <summary>回填-追加到原笔记（受沉浸式锁定约束）</summary>
    private void BtnFillAppend_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatBubbleViewModel bubble } btn) return;
        var target = _active?.TargetNote;
        if (target == null || bubble.IsUser || bubble.IsFilled) return;

        if (ImmersiveSessionService.IsLocked(target.Timestamp))
        {
            System.Windows.MessageBox.Show(this, "沉浸式输入进行中，暂不可回填", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 优先气泡内选中文字，无选中则全文
        var fillText = GetBubbleSelectedText(btn, bubble);
        if (string.IsNullOrEmpty(fillText)) return;

        if (_noteService.AppendToNote(target, fillText))
        {
            bubble.IsFilled = true; // 按钮文本由 DataTrigger 自动更新为"已回填"并禁用
        }
        else
        {
            System.Windows.MessageBox.Show(this, "回填失败：未找到笔记文件或写入失败", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>回填-单独形成一条新笔记（不关联原笔记，无锁定约束）</summary>
    private void BtnFillNew_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatBubbleViewModel bubble } btn) return;
        if (bubble.IsUser || bubble.IsFilled) return;

        var fillText = GetBubbleSelectedText(btn, bubble);
        if (string.IsNullOrEmpty(fillText)) return;

        if (_noteService.SaveAiNote(fillText) != null)
        {
            bubble.IsFilled = true;
        }
        else
        {
            System.Windows.MessageBox.Show(this, "保存失败：笔记写入出错", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>读取气泡内选中文字；无选中返回全文。从按钮上溯到气泡 Border 找 BubbleText（只读 TextBox）</summary>
    private static string GetBubbleSelectedText(Button btn, ChatBubbleViewModel bubble)
    {
        try
        {
            DependencyObject? current = btn;
            while (current != null && current is not Border)
                current = VisualTreeHelper.GetParent(current);
            var box = current is Border b ? FindVisualChild<TextBox>(b) : null;
            var selected = box?.SelectedText?.Trim();
            if (!string.IsNullOrEmpty(selected)) return selected;
        }
        catch
        {
            // 可视化树查找失败时回退全文
        }
        return bubble.Content.Trim();
    }

    /// <summary>在可视树中查找指定类型的第一个后代</summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            if (FindVisualChild<T>(child) is T found) return found;
        }
        return null;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        SaveActiveDraft();                      // 关窗前把当前输入框草稿存到活跃会话
        SaveDraftToDisk(_active?.DraftText);    // 只落盘活跃会话那份草稿（方案确认的边界）
        // 关窗即停止所有后台回答（多会话并行：每个 runtime 各自 cancel + 落盘）
        foreach (var r in _runtimes.Values)
        {
            try { r.Cts?.Cancel(); } catch (ObjectDisposedException) { /* 已结束 */ }
            try { r.Session.Save(); } catch { /* best effort */ }
        }
        _runtimes.Clear();
        _active = null;
        FileDeliveryHub.Delivered -= OnFileDelivered;
        FileDeliveryHub.OpenRequested -= OnFileOpenRequested;
        FileDeliveryHub.LocateRequested -= OnFileLocateRequested;
        try { _preview.Dispose(); } catch { /* 预览浮层释放失败不影响关闭 */ }
        AIDialogHelper.NotifyClosed();
    }

    public bool IsClosed => _closed;
}

/// <summary>三处入口共用的对话框单例管理（标题栏 / 悬浮球 / 托盘 / 右键 / 浮动工具条）</summary>
public static class AIDialogHelper
{
    private static AIDialogWindow? _dialog;
    private static NoteService? _noteService;
    private static AppSettings? _settings;
    private static Window? _owner;

    /// <summary>删除会话管道（阶段二删除 UI → 阶段一 MarkDeleted：trash + 删除清单 + Notify）。
    /// 由 MainWindow 创建 ChatSyncEngine 后注入；未注入（未配同步）时仅本地行为退化为无删除——
    /// 历史管理必须可用，故 AIDialogWindow 在此兜底直接移文件（不动同步清单）。</summary>
    public static Action<string>? SessionDeleted;

    /// <summary>删除会话（UI 唯一入口）：有同步引擎走 MarkDeleted 闭环①；无引擎直接移 trash（纯本地）。</summary>
    public static void DeleteSession(string sessionId)
    {
        if (SessionDeleted != null) { SessionDeleted.Invoke(sessionId); return; }
        try
        {
            var svc = ChatSessionService.LoadByAnyId(sessionId);
            if (svc != null)
            {
                Directory.CreateDirectory(ChatSessionService.TrashDir);
                File.Move(svc, Path.Combine(ChatSessionService.TrashDir, Path.GetFileName(svc)));
            }
        }
        catch { /* 删除失败下次对账兜底；本地无同步时无清单可补 */ }
    }

    public static void Initialize(NoteService noteService, AppSettings settings, Window? owner)
    {
        _noteService = noteService;
        _settings = settings;
        _owner = owner;
    }

    public static void NotifyClosed() => _dialog = null;

    /// <summary>应用退出时关闭单例对话框</summary>
    public static void CloseAll()
    {
        if (_dialog != null && !_dialog.IsClosed)
        {
            try { _dialog.Close(); } catch { /* best effort */ }
        }
        _dialog = null;
    }

    /// <summary>同步周期完成后刷新展开中的历史抽屉（后台线程调用安全，内部 marshal 回 UI）</summary>
    public static void RefreshOpenDrawer()
    {
        var dialog = _dialog;
        if (dialog == null) return;
        dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!dialog.IsClosed) dialog.RefreshDrawerIfOpen();
        }));
    }

    /// <summary>网盘文件清单同步完成后刷新打开中的对话框（句柄可能已过期、云文件卡片状态可能变化）</summary>
    public static void NotifyFileListChanged()
    {
        var dialog = _dialog;
        if (dialog == null) return;
        dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!dialog.IsClosed) dialog.RefreshFileViews();
        }));
    }

    /// <summary>打开 AI 对话框；Key 为空时提示并返回</summary>
    /// <param name="attachmentPaths">
    /// 要预置到输入区的本地文件（悬浮球拖放保存的「用 AI 问答打开」，2026-09-16 新增）。
    /// 原签名只有 (mode, targetNote, selectedText)，塞不进文件附件 —— 所以这里扩展了第四个参数。
    /// </param>
    public static void Open(ExplainMode mode, NoteEntry? targetNote = null, string? selectedText = null,
        IReadOnlyList<string>? attachmentPaths = null)
    {
        if (!LicenseGate.EnsureAllowed(LicenseGate.FeatureAiChat, "AI 问答")) return;
        if (_noteService == null || _settings == null) return;

        // 2026-09-23 多供应商改造：不再直判「AiApiKey / AiModel 是否为空」（那是单供应商时代的写法，
        // 多供应商下这两个字段可能只是历史残留），改为按「当前使用模型」的解析结果判断。
        // 提示仍分两种，比原来更具体：缺整体配置 / 缺该供应商的 Key。
        var activeModel = AiModelResolver.ResolveActive(_settings);
        if (activeModel == null)
        {
            System.Windows.MessageBox.Show(_owner ?? Application.Current.MainWindow,
                "请先在 设置 → AI 模型 中添加模型供应商并选择模型", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(activeModel.ApiKey))
        {
            System.Windows.MessageBox.Show(_owner ?? Application.Current.MainWindow,
                "请先在 设置 → AI 模型 中填写该供应商的 API Key", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 旧实例不可用（null / 已关闭 / 已关闭但标志未更新的 IsVisible=false）一律重建——
        // 否则复用已关闭的 Window 调 OpenSession 会抛异常，导致对话框完全弹不出来（"点了没反应"）
        if (_dialog == null || _dialog.IsClosed || !_dialog.IsVisible)
        {
            _dialog = new AIDialogWindow(_noteService, _settings);
            if (_owner != null && _owner.IsVisible)
                _dialog.Owner = _owner;
        }

        try
        {
            _dialog.OpenSession(mode, targetNote, selectedText);
        }
        catch
        {
            // OpenSession 异常（旧窗口状态异常等）→ 重建后重试一次，绝不静默失败
            _dialog = new AIDialogWindow(_noteService, _settings);
            if (_owner != null && _owner.IsVisible) _dialog.Owner = _owner;
            _dialog.OpenSession(mode, targetNote, selectedText);
        }

        _dialog.Show();
        if (_dialog.WindowState == WindowState.Minimized) _dialog.WindowState = WindowState.Normal;
        _dialog.Activate();
        _dialog.Focus();

        // 2026-09-14 修复：输入框必须显式聚焦，且要等窗口显示并完成布局之后再聚焦。
        // 此前只调了窗口级 Activate()/Focus()，用户看到的现象是"打开后打字没反应，得先用鼠标点一下输入框"。
        _dialog.Dispatcher.BeginInvoke(new Action(_dialog.FocusInput), DispatcherPriority.Input);

        // 草稿回填（问题三，2026-09-21）：上次关窗落盘的未发送文本，重开时回填到当前会话输入框。
        // 同为 Input 优先级、排在 FocusInput 之后 —— 回填完光标落在文末仍是聚焦态。
        _dialog.Dispatcher.BeginInvoke(new Action(_dialog.RestoreDraftFromDisk), DispatcherPriority.Input);

        // 拖放保存（2026-09-16）：附件在**窗口显示之后**才落。
        // 走 Normal 优先级，早于上面 Input 优先级的聚焦 —— 先落附件、再把光标还给输入框。
        if (attachmentPaths is { Count: > 0 })
        {
            var seed = _dialog;
            seed.Dispatcher.BeginInvoke(new Action(() => seed.AddAttachmentPaths(attachmentPaths)));
        }
    }
}
