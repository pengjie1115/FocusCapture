using System.Collections.ObjectModel;
using System.Threading;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;
using FocusCapture.Services.Agent;
using FocusCapture.Services.Destinations;
using FocusCapture.Windows.Controls;

namespace FocusCapture.Windows;

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

    public ChatBubbleViewModel(bool isUser, string content, bool isFillable = false)
    {
        IsUser = isUser;
        Content = content;
        IsFillable = isFillable;
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
        HistoryPanel.SessionSelected += item => Dispatcher.BeginInvoke(new Action(() => LoadHistorySession(item.FilePath)));
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

    private void AddBubble(bool isUser, string content, bool isFillable = false)
    {
        _bubbles.Add(new ChatBubbleViewModel(isUser, content, isFillable));
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

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            SendCurrentInput();
        }
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
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        InputBox.Text = "";
        SendAsync(text);
    }

    private void StopStreaming()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* 已释放即已结束 */ }
    }

    /// <summary>发送一条消息并流式接收回复（普通/Agent 两路径统一：真流式 + 思考过程 + 可停止）</summary>
    private async void SendAsync(string text)
    {
        if (_session == null || _isStreaming) return;
        if (string.IsNullOrWhiteSpace(text)) return;

        var generation = _sessionGeneration;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _isStreaming = true;
        SetBusyUi(true);

        try
        {
            AddBubble(true, text);
            AddBubble(false, "", _targetNote != null && _mode != ExplainMode.Ask);
            var current = _bubbles[^1];
            current.Content = "思考中…"; // 首包到达前的等待占位

            if (_settings.AgentEnabled && _mode == ExplainMode.Ask)
            {
                // Agent 路径：function calling 流式循环（RunAsync 内部负责把用户消息写入会话）
                await SendViaAgentAsync(text, current, generation, cts);
            }
            else
            {
                // 普通问答路径：用户消息入会话 + 事件流式接收（正文/思考）
                // （AddUser 不可省：请求体 messages 无 user 会导致 Agnes 400 "No user query" / DeepSeek 自说自话）
                _session.AddUser(text);
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
    private async Task SendViaAgentAsync(string text, ChatBubbleViewModel current, int generation, CancellationTokenSource cts)
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
            var reply = await agent.RunAsync(text, cts.Token);
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
            HistoryPanel.Load(ChatSessionService.ListSessions());

        _drawerOpen = !_drawerOpen;
        var anim = new DoubleAnimation(_drawerOpen ? 240 : 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        HistoryPanel.BeginAnimation(WidthProperty, anim);
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
            if (m.Role == ChatRoles.User) AddBubble(true, m.Content);
            else if (m.Role == ChatRoles.Assistant) AddBubble(false, m.Content);
            // tool / assistant(tool_calls) 中间消息不渲染为气泡
        }

        TitleText.Text = GetModeTitle(_mode);
        Title = GetModeTitle(_mode);
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
    }
}
