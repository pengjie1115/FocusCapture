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
using FocusCapture.Windows.Controls;

namespace FocusCapture.Windows;

/// <summary>
/// 气泡里的单个附件展示项（2026-09-14）。
/// 图片出缩略图，文档出文件名卡片；本体不在本机（跨端拉来的会话）时降级为文字占位，不显示裂图。
/// </summary>
public class ChatAttachmentViewModel
{
    /// <summary>对应的数据模型（点击查看/打开时用）</summary>
    public ChatAttachment Model { get; }

    /// <summary>展示名：类型 + 文件名 + 体积 + 图片尺寸</summary>
    public string DisplayName { get; }

    /// <summary>缩略图（仅图片且本机有本体时非空）</summary>
    public ImageSource? Thumbnail { get; }
    public bool HasThumbnail => Thumbnail != null;

    /// <summary>副标题说明（附件不在本机 / 文档抽取说明）</summary>
    public string MissingHint { get; } = "";
    public bool HasMissingHint => MissingHint.Length > 0;

    public ChatAttachmentViewModel(ChatAttachment model)
    {
        Model = model;

        // 第一行只放文件名（放类型+体积会撑爆气泡宽度被截断），元信息挪到副标题行
        DisplayName = model.FileName;

        var path = ChatAttachmentService.ResolvePath(model);
        if (!File.Exists(path))
        {
            MissingHint = "仅存于原设备，本机没有此附件";
            return;
        }

        if (model.Kind == ChatAttachmentKind.Image)
        {
            Thumbnail = LoadThumbnail(path);
            MissingHint = $"图片 · {FormatSize(model.SizeBytes)} · {model.PixelWidth}×{model.PixelHeight}";
        }
        else
        {
            MissingHint = string.IsNullOrEmpty(model.ExtractNote) ? "正文已随消息发送" : model.ExtractNote;
        }
    }

    /// <summary>缩略图按需解码（限制解码宽度，避免大图全尺寸解码吃内存）；OnLoad + Freeze 免锁文件、可跨线程</summary>
    private static ImageSource? LoadThumbnail(string path)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.DecodePixelWidth = 360;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
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
    private readonly ObservableCollection<ChatBubbleViewModel> _bubbles = new();
    private ChatSessionService? _session;
    private NoteEntry? _targetNote;
    private bool _isStreaming;
    private bool _closed;
    private int _sessionGeneration; // 新会话时递增，旧流据此自我中止
    private CancellationTokenSource? _cts;   // 当前回答的取消源（发送按钮停止 / 新会话切换时取消）
    private bool _drawerOpen;               // 历史抽屉展开状态
    private AgentToolRegistry? _registry; // Agent 工具注册表（AgentEnabled 时懒构建）
    private bool _agentRulesAdded;        // Agent 系统规则每会话只注入一次

    public AIDialogWindow(NoteService noteService, AppSettings settings)
    {
        _noteService = noteService;
        _settings = settings;
        _provider = new OpenAICompatibleProvider(settings.AiBaseUrl, settings.AiApiKey, settings.AiModel, settings.AiMaxTokens);
        InitializeComponent();
        MessagesList.ItemsSource = _bubbles;
        InitInputArea();
        HistoryPanel.SessionSelected += item => Dispatcher.BeginInvoke(new Action(() => LoadHistorySession(item.FilePath)));
        HistoryPanel.ItemAction += (item, action, context) => Dispatcher.BeginInvoke(new Action(() => HandleItemAction(item, action, context)));
        HistoryPanel.BatchAction += (action, items, context) => Dispatcher.BeginInvoke(new Action(() => HandleBatchAction(action, items, context)));
        HistoryPanel.GroupsManageRequested += () => Dispatcher.BeginInvoke(new Action(HandleGroupsManage));
        HistoryPanel.RecycleBinRequested += () => Dispatcher.BeginInvoke(new Action(HandleRecycleBin));
        HistoryPanel.CollapseRequested += () => Dispatcher.BeginInvoke(new Action(() => OpenDrawer(false))); // 抽屉内收起按钮：复用同一动画逻辑
        Closed += OnWindowClosed;
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
                                && _session != null
                                && _mode == ExplainMode.Ask;

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

        TitleText.Text = GetModeTitle(mode);
        Title = GetModeTitle(mode);
        _session?.Save();
    }

    private ExplainMode _mode;

    /// <summary>新建会话核心操作：清空对话上下文（目标笔记上下文保留），复位于 UI 入口与标题栏按钮。</summary>
    private void StartNewSession(ExplainMode mode, NoteEntry? targetNote)
    {
        StopStreaming();         // 中断进行中的回答
        _sessionGeneration++;
        _isStreaming = false;
        _targetNote = targetNote;
        _mode = mode;

        string? noteContext = null;
        string? noteContent = null;
        if (targetNote != null)
        {
            noteContext = targetNote.Timestamp.ToString("yyyy-MM-dd HH:mm");
            noteContent = targetNote.Content;
        }

        _session = new ChatSessionService(mode, noteContext, noteContent, _settings.AiToolResultLimit);
        _bubbles.Clear();
    }

    private static string GetModeTitle(ExplainMode mode) => "AI " + AiModeText.Get(mode);

    private void AddBubble(bool isUser, string content, bool isFillable = false,
        IReadOnlyList<ChatAttachmentViewModel>? attachments = null)
    {
        _bubbles.Add(new ChatBubbleViewModel(isUser, content, isFillable, attachments));
        ScrollAfterDelay();
    }

    private void ScrollAfterDelay()
    {
        Dispatcher.BeginInvoke(new Action(ScrollToBottom), DispatcherPriority.Background);
    }

    private void ScrollToBottom()
    {
        MessagesScroll.ScrollToEnd();
    }

    // ══════════════════ 输入区：富文本 + 附件混排（2026-09-14） ══════════════════
    // 架构要点：输入区是 RichTextBox，附件是内联的原子块（InlineUIContainer）。
    // 顺序信息存在每个附件的 InsertOffset 上，上行请求时据此把正文切开、还原"文字-图-文字"的原序。

    /// <summary>输入区初始化：备好空段落 + 接管粘贴（剪贴板里的图片/文件要当附件，而不是纯文本）</summary>
    private void InitInputArea()
    {
        ResetInput();
        DataObject.AddPastingHandler(InputBox, OnInputPaste);
    }

    /// <summary>清空输入区并恢复一个空段落（RichTextBox 至少要有一个 Block，否则无法输入）</summary>
    private void ResetInput()
    {
        var doc = InputBox.Document;
        doc.Blocks.Clear();
        doc.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
        InputBox.CaretPosition = doc.ContentStart;
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
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers == ModifierKeys.Shift) return;   // Shift+回车 = 换行（保留默认行为）
        e.Handled = true;
        SendCurrentInput();
    }

    /// <summary>发送/停止一体按钮：回答中点击 = 停止生成，空闲时点击 = 发送</summary>
    private void BtnSend_Click(object sender, RoutedEventArgs e)
    {
        if (_isStreaming)
        {
            StopStreaming();
            return;
        }
        SendCurrentInput();
    }

    private void SendCurrentInput()
    {
        if (_isStreaming) return; // 回答中 Enter 不发送也不清空输入框，防误触丢字

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

    /// <summary>
    /// 粘贴拦截：剪贴板有文件或图片时接管为附件，其余情况放行走默认文本粘贴。
    ///
    /// 关键规则：**同时含文字与位图时优先文字** —— Word / 网页复制文本时剪贴板里也会带位图，
    /// 不加这个判断会把用户想粘的文字变成一张图。
    /// </summary>
    private void OnInputPaste(object sender, DataObjectPastingEventArgs e)
    {
        // 1) 资源管理器里 Ctrl+C 复制的文件
        if (e.DataObject.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.DataObject.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                e.CancelCommand();
                _ = AddFilesAsync(files);
                return;
            }
        }

        // 2) 同时带文字 → 用户意图多半是粘文字（Word / 网页复制场景），放行默认粘贴
        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText)
            && e.DataObject.GetData(DataFormats.UnicodeText) is string s && !string.IsNullOrEmpty(s))
            return;

        // 3) 只有位图 → 截图粘贴
        if (e.DataObject.GetDataPresent(DataFormats.Bitmap))
        {
            BitmapSource? src = null;
            try { src = e.DataObject.GetData(DataFormats.Bitmap) as BitmapSource; } catch { /* 取不到走退路 */ }
            src ??= TryGetClipboardImage();
            if (src != null)
            {
                e.CancelCommand();
                _ = AddBitmapAsync(src);
            }
        }
    }

    private static BitmapSource? TryGetClipboardImage()
    {
        try { return WpfClipboard.GetImage(); }
        catch { return null; }
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
            "如果所用模型支持图片输入，请在「设置 → AI 模型」中打开该开关。",
            "图片发送已关闭", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── 附件块（InlineUIContainer）：原子节点，整块删除、光标跨不过去 ──

    /// <summary>
    /// 在光标处插入附件块。
    /// 选富文本方案（而非"文字框 + 下方附件条"）的核心收益就在这里：
    /// 附件是文档里的原子节点，删不掉一半、复制不乱序。
    /// </summary>
    private void InsertAttachmentChip(ChatAttachment att)
    {
        var pos = InputBox.CaretPosition;
        var insertAt = pos.GetInsertionPosition(LogicalDirection.Forward) ?? pos;

        var container = new InlineUIContainer(BuildAttachmentChip(att), insertAt) { Tag = att };

        // 插完把光标移到块之后：否则光标可能仍停在块前，用户接着打字会插到附件之前
        var after = container.ElementEnd?.GetInsertionPosition(LogicalDirection.Forward);
        if (after != null) InputBox.CaretPosition = after;
        InputBox.Focus();
    }

    private FrameworkElement BuildAttachmentChip(ChatAttachment att)
    {
        var isImage = att.Kind == ChatAttachmentKind.Image;
        var label = att.FileName.Length > 16 ? att.FileName[..16] + "…" : att.FileName;

        var text = new TextBlock
        {
            Text = (isImage ? "图片 " : "文件 ") + label,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            VerticalAlignment = VerticalAlignment.Center,
        };

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

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(text);
        panel.Children.Add(remove);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
            BorderBrush = new SolidColorBrush(isImage
                ? Color.FromRgb(0x37, 0x8A, 0xDD)
                : Color.FromRgb(0x88, 0x87, 0x80)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(2, 1, 2, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = panel,
            ToolTip = BuildAttachmentTooltip(att),
        };
    }

    /// <summary>悬停预览：图片直接出图（对齐 WorkBuddy 的交互），文档出文件信息</summary>
    private static object BuildAttachmentTooltip(ChatAttachment att)
    {
        var panel = new StackPanel { MaxWidth = 340 };
        panel.Children.Add(new TextBlock
        {
            Text = att.FileName,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            TextWrapping = TextWrapping.Wrap,
        });

        var path = ChatAttachmentService.ResolvePath(att);
        var thumb = att.Kind == ChatAttachmentKind.Image && File.Exists(path)
            ? LoadBitmap(path, 480)
            : null;

        if (thumb != null)
        {
            panel.Children.Add(new Image
            {
                Source = thumb,
                MaxWidth = 320,
                MaxHeight = 240,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = att.Kind == ChatAttachmentKind.Image
                    ? "图片不在本机"
                    : $"文件 · {ChatAttachmentViewModel.FormatSize(att.SizeBytes)}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        return new ToolTip { Content = panel, Padding = new Thickness(8) };
    }

    /// <summary>按需解码位图（限制解码宽度省内存；OnLoad + Freeze 免锁文件、可跨线程）</summary>
    private static BitmapSource? LoadBitmap(string path, int decodeWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void RemoveAttachmentChip(ChatAttachment att)
    {
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
    }

    /// <summary>气泡里点附件：本机有本体就用系统默认程序打开（看图 / 看原文件都走这条）</summary>
    private void AttachmentThumb_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ChatAttachmentViewModel vm) return;

        var path = ChatAttachmentService.ResolvePath(vm.Model);
        if (!File.Exists(path))
        {
            System.Windows.MessageBox.Show(this, "这个附件只存在原设备上，本机没有拷贝。", "附件不在本机",
                MessageBoxButton.OK, MessageBoxImage.Information);
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

            InsertAttachmentChip(att);
            _bubbles.Add(new ChatBubbleViewModel(true, "这是带附件的消息示例", false,
                BuildAttachmentVms(new List<ChatAttachment> { att })));
        }
        catch
        {
            // 快照是辅助手段，任一环节失败都不该阻断整轮快照
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
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* 已释放即已结束 */ }
    }

    /// <summary>发送一条消息（可带附件）并流式接收回复（普通/Agent 两路径统一：真流式 + 思考过程 + 可停止）</summary>
    private async void SendAsync(string text, List<ChatAttachment>? attachments = null)
    {
        if (_session == null || _isStreaming) return;
        if (string.IsNullOrWhiteSpace(text) && attachments is not { Count: > 0 }) return;

        var generation = _sessionGeneration;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _isStreaming = true;
        SetBusyUi(true);

        try
        {
            AddBubble(true, text, attachments: BuildAttachmentVms(attachments));
            AddBubble(false, "", _targetNote != null && _mode != ExplainMode.Ask);
            var current = _bubbles[^1];
            current.Content = "思考中…"; // 首包到达前的等待占位

            if (_settings.AgentEnabled && _mode == ExplainMode.Ask)
            {
                // Agent 路径：function calling 流式循环（RunAsync 内部负责把用户消息写入会话）
                await SendViaAgentAsync(text, attachments, current, generation, cts);
            }
            else
            {
                // 普通问答路径：用户消息入会话 + 事件流式接收（正文/思考）
                // （AddUser 不可省：请求体 messages 无 user 会导致 Agnes 400 "No user query" / DeepSeek 自说自话）
                _session.AddUser(text, attachments);
                await StreamPlainReplyAsync(current, generation, cts);
            }
        }
        catch (Exception ex)
        {
            if (generation != _sessionGeneration) return;
            var last = _bubbles.Count > 0 ? _bubbles[^1] : null;
            if (last != null && !last.IsUser)
            {
                last.Content += string.IsNullOrEmpty(last.Content)
                    ? $"（错误：{ex.Message}）"
                    : $"\n\n（错误：{ex.Message}）";
            }
            else
            {
                AddBubble(false, $"（错误：{ex.Message}）");
            }
        }
        finally
        {
            _isStreaming = false; // 即使会话已切换也必须复位，否则新会话永远发不出消息
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            if (generation == _sessionGeneration)
            {
                SetBusyUi(false);
                _session?.Save();
            }
        }
    }

    /// <summary>普通问答路径：消费 StreamChatWithToolsAsync 事件流（无 tools），正文打字机 + 思考过程展示。
    /// 用户停止时已生成的部分内容照常写入会话历史。</summary>
    private async Task StreamPlainReplyAsync(ChatBubbleViewModel current, int generation, CancellationTokenSource cts)
    {
        var sb = new StringBuilder();
        try
        {
            await foreach (var ev in _provider.StreamChatWithToolsAsync(_session!.Messages, tools: null, cts.Token))
            {
                if (generation != _sessionGeneration) return; // 会话已切换，丢弃旧流
                switch (ev)
                {
                    case StreamChatEvent.ReasoningDelta reasoning:
                        AppendReasoning(current, reasoning.Text);
                        break;
                    case StreamChatEvent.ContentDelta delta:
                        sb.Append(delta.Text);
                        current.Content = sb.ToString();
                        ScrollAfterDelay();
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (generation != _sessionGeneration) return;
            MarkStopped(current, sb.ToString());
            if (!string.IsNullOrWhiteSpace(sb.ToString()))
                _session!.AddAssistant(sb.ToString());
            return;
        }

        if (generation != _sessionGeneration) return;
        CollapseReasoning(current);
        var full = sb.ToString();
        if (string.IsNullOrWhiteSpace(full))
        {
            current.Content = "（模型未返回内容）";
        }
        else
        {
            _session.AddAssistant(full);
        }
    }

    /// <summary>
    /// Agent 路径：function calling 流式循环。工具调用步骤实时追加到气泡；正文/思考增量打字机展示；
    /// 写操作（非只读工具）执行前经 ConfirmHandler 弹窗确认。用户停止时不把部分内容写入会话
    /// （中断可能落在 assistant(tool_calls) 与 tool 结果配对之间，写入不完整配对会让后续请求 400）。
    /// </summary>
    private async Task SendViaAgentAsync(string text, List<ChatAttachment>? attachments,
        ChatBubbleViewModel current, int generation, CancellationTokenSource cts)
    {
        EnsureAgentRegistry();
        AppendAgentRulesOnce();
        var agent = new AgentRunService(_provider, _registry!, _session!, _settings.AgentMaxToolRounds)
        {
            ConfirmHandler = desc => Task.FromResult(System.Windows.MessageBox.Show(
                this,
                $"AI 请求执行以下操作：\n\n{desc}\n\n确认执行？",
                "AI 操作确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK),
            WriteConfirmEnabled = _settings.AgentWriteConfirmPopup,
        };

        var gate = new object(); // agentSb / finished 由事件线程与 UI 线程共同访问
        var agentSb = new StringBuilder();
        var finished = false;

        agent.StatusCallback = msg =>
        {
            if (generation != _sessionGeneration) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation != _sessionGeneration) return;
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
                if (generation != _sessionGeneration) return;
                lock (gate)
                {
                    if (finished) return;
                    current.Content = agentSb.ToString();
                }
                ScrollAfterDelay();
            }));
        };
        agent.ReasoningDelta += t =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation != _sessionGeneration || Volatile.Read(ref finished)) return;
                AppendReasoning(current, t);
            }));
        };

        try
        {
            var reply = await agent.RunAsync(text, attachments, cts.Token);
            if (generation != _sessionGeneration) return;
            lock (gate) finished = true;
            CollapseReasoning(current);
            current.Content = string.IsNullOrWhiteSpace(reply) ? "（模型未返回内容）" : reply;
        }
        catch (OperationCanceledException)
        {
            if (generation != _sessionGeneration) return;
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
        // 输入框回答期间保持可用（可预输入下一条），发送动作由 _isStreaming 守卫拦截
    }

    /// <summary>历史抽屉开关：展开时刷新会话列表；宽度动画滑出/收起</summary>
    private void BtnHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!_drawerOpen)
            RefreshDrawer();
        OpenDrawer(!_drawerOpen);
    }

    // ── 抽屉布局（阶段二）：宽度参数化 + 拖拽 + 跨启动记忆 ──

    private const double DrawerMinWidth = 150;   // 防拖没了（方案文档 §6）
    private const double DrawerMaxWidth = 480;

    /// <summary>展开/收起抽屉（"历史"按钮与抽屉内收起按钮共用）。
    /// 动画仍作用于 HistoryPanel.Width（铁律：不动 ColumnDefinition）；展开宽度 = 设置记忆宽度（240 参数化）。
    /// 收起状态不记忆——下次展开仍用记忆宽度。</summary>
    private void OpenDrawer(bool open)
    {
        _drawerOpen = open;
        DrawerSplitter.IsEnabled = false; // 动画期间禁用拖拽，避免与动画打架（铁律 2）
        if (open)
        {
            HistoryPanel.MinWidth = 0;   // 动画从 0 长到目标，MinWidth 边界动画结束后恢复
            RefreshDrawer();
        }
        else
        {
            HistoryPanel.MinWidth = 0;   // 收到 0 需先解除 MinWidth 顶住
        }

        var target = open ? Math.Clamp(_settings.AiDrawerWidth, DrawerMinWidth, DrawerMaxWidth) : 0;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            if (_drawerOpen) HistoryPanel.MinWidth = DrawerMinWidth;
            DrawerSplitter.IsEnabled = true;
        };
        HistoryPanel.BeginAnimation(WidthProperty, anim);
    }

    /// <summary>抽屉宽度拖拽：直接改 HistoryPanel.Width（不改 ColumnDefinition，铁律 2），有边界。</summary>
    private void DrawerSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (!_drawerOpen) return;
        HistoryPanel.Width = Math.Clamp(HistoryPanel.ActualWidth + e.HorizontalChange, DrawerMinWidth, DrawerMaxWidth);
    }

    /// <summary>拖拽结束：宽度记忆（收起状态不记忆）</summary>
    private void DrawerSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (!_drawerOpen || HistoryPanel.Width <= 0) return;
        _settings.AiDrawerWidth = HistoryPanel.Width;
        _settings.Save();
    }

    /// <summary>刷新抽屉列表（展开中才刷新；启动下拉/操作完成后宿主调用）</summary>
    private void RefreshDrawer()
    {
        if (!_drawerOpen) return;
        HistoryPanel.Load(ChatSessionService.ListSessions(), ChatGroupStore.Load());
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
            case ChatItemAction.BatchStart:
                HistoryPanel.EnterBatchMode();
                break;

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

            case ChatItemAction.Export:
                if (Enum.TryParse<ExportFormat>(context, out var fmt))
                    ExportOne(item, fmt);
                break;

            case ChatItemAction.Delete:
                DeleteSessions([item]);
                break;
        }
    }

    /// <summary>批量操作（多选操作条）。结束统一退出多选模式并刷新。</summary>
    private void HandleBatchAction(ChatBatchAction action, IReadOnlyList<HistoryItemViewModel> items, string? context)
    {
        if (items.Count == 0) return;
        switch (action)
        {
            case ChatBatchAction.Delete:
                DeleteSessions(items);
                HistoryPanel.ExitBatchMode();
                RefreshDrawer();
                break;

            case ChatBatchAction.Group:
                foreach (var item in items)
                    ApplySessionMeta(item, s => s.GroupId = context ?? "");
                HistoryPanel.ExitBatchMode();
                RefreshDrawer();
                break;

            case ChatBatchAction.Export:
                if (!Enum.TryParse<ExportFormat>(context, out var fmt)) return;
                ExportMany(items, fmt);
                HistoryPanel.ExitBatchMode();
                break;
        }
    }

    private void HandleGroupsManage()
    {
        new ChatGroupsWindow { Owner = this }.ShowDialog();
        RefreshDrawer();
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
        if (_session != null && _session.SessionId == item.Id)
        {
            mutate(_session);
            _session.Save();
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
            if (_session != null && _session.SessionId == item.Id)
                StartNewSession(_mode, null);
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

    /// <summary>加载历史会话回看（可继续对话）。历史 JSON 未存原笔记引用：「追加到原笔记」不可用，「存为新笔记」正常。</summary>
    private void LoadHistorySession(string filePath)
    {
        var loaded = ChatSessionService.Load(filePath);
        if (loaded == null)
        {
            System.Windows.MessageBox.Show(this, "会话文件损坏或无法读取", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_isStreaming) StopStreaming();
        _sessionGeneration++;
        _isStreaming = false;
        _session = loaded;
        _mode = loaded.Mode;
        _targetNote = null;

        // Agent 会话的 system 消息里已含规则文本，避免继续对话时重复注入
        _agentRulesAdded = _session.Messages.Count > 0
            && _session.Messages[0].Role == ChatRoles.System
            && _session.Messages[0].Content.Contains("[Agent 工具规则]");

        _bubbles.Clear();
        foreach (var m in _session.Messages)
        {
            if (m.Role == ChatRoles.User) AddBubble(true, m.Content, attachments: BuildAttachmentVms(m.Attachments));
            else if (m.Role == ChatRoles.Assistant) AddBubble(false, m.Content);
            // tool / assistant(tool_calls) 中间消息不渲染为气泡
        }

        TitleText.Text = GetModeTitle(_mode);
        Title = GetModeTitle(_mode);
        FocusInput();   // 切完历史会话把焦点还给输入框，省掉"再点一下才能打字"
    }

    private void BtnNewSession_Click(object sender, RoutedEventArgs e)
    {
        StartNewSession(_mode, _targetNote); // 清空对话上下文，保留当前目标笔记
        _session?.Save();
    }

    /// <summary>Agent 模式系统规则（每个会话只注入一次）：以工具结果为事实来源 + 写操作先在对话中征询</summary>
    private void AppendAgentRulesOnce()
    {
        if (_session == null || _agentRulesAdded) return;
        _session.AppendSystemRules(
            "[Agent 工具规则]\n" +
            "1. 工具执行返回的结果是唯一事实来源：工具返回成功才可以说完成；返回失败必须如实告知。严禁在没有调用工具、或工具未返回成功的情况下宣称已完成任何操作。\n" +
            "2. 执行任何写操作（新增/修改/删除笔记或待办）之前，必须先在回复中列出将要执行的具体动作，等用户明确同意后再调用工具执行。\n" +
            "3. 没有对应工具的能力就直说做不到，不要编造替代方案的结果。\n" +
            "4. 引用或修改某条笔记/待办时，用列表/搜索工具输出中方括号里的时间戳作为 ref_time 定位。");
        _agentRulesAdded = true;
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
        registry.Register(new DeleteNoteTool(_noteService));

        var getNote = new GetNoteDestination(_settings);
        foreach (var capability in getNote.Capabilities)
            registry.Register(new OutboundTool(getNote, capability));

        _registry = registry;
    }

    /// <summary>回填-追加到原笔记（受沉浸式锁定约束）</summary>
    private void BtnFillAppend_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatBubbleViewModel bubble } btn) return;
        if (_targetNote == null || bubble.IsUser || bubble.IsFilled) return;

        if (ImmersiveSessionService.IsLocked(_targetNote.Timestamp))
        {
            System.Windows.MessageBox.Show(this, "沉浸式输入进行中，暂不可回填", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 优先气泡内选中文字，无选中则全文
        var fillText = GetBubbleSelectedText(btn, bubble);
        if (string.IsNullOrEmpty(fillText)) return;

        if (_noteService.AppendToNote(_targetNote, fillText))
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

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        StopStreaming();
        try { _session?.Save(); } catch { /* best effort */ }
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

    /// <summary>打开 AI 对话框；Key 为空时提示并返回</summary>
    public static void Open(ExplainMode mode, NoteEntry? targetNote = null, string? selectedText = null)
    {
        if (!LicenseGate.EnsureAllowed(LicenseGate.FeatureAiChat, "AI 问答")) return;
        if (_noteService == null || _settings == null) return;

        if (string.IsNullOrWhiteSpace(_settings.AiApiKey))
        {
            System.Windows.MessageBox.Show(_owner ?? Application.Current.MainWindow,
                "请先在设置中配置 API Key", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.AiModel))
        {
            System.Windows.MessageBox.Show(_owner ?? Application.Current.MainWindow,
                "请先在设置 → AI 模型中填写模型名称", "提示",
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
    }
}
