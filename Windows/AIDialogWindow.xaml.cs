using System.Collections.ObjectModel;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFilled)));
        }
    }

    public ChatBubbleViewModel(bool isUser, string content, bool isFillable = false)
    {
        IsUser = isUser;
        Content = content;
        IsFillable = isFillable;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>三板块 AI 对话框：翻译 / 搜索 / 问答，连续对话 + 流式输出 + 回填</summary>
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

    public AIDialogWindow(NoteService noteService, AppSettings settings)
    {
        _noteService = noteService;
        _settings = settings;
        _provider = new OpenAICompatibleProvider(settings.AiBaseUrl, settings.AiApiKey, settings.AiModel);
        InitializeComponent();
        MessagesList.ItemsSource = _bubbles;
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
        string? firstMessage = null;

        if (!isGlobalAskReopen)
        {
            _sessionGeneration++;
            _targetNote = targetNote;
            _mode = mode;

            string? noteContext = null;
            string? noteContent = null;
            if (targetNote != null)
            {
                noteContext = targetNote.Timestamp.ToString("yyyy-MM-dd HH:mm");
                noteContent = targetNote.Content;
            }

            _session = new ChatSessionService(mode, noteContext, noteContent);
            _bubbles.Clear();

            if (!string.IsNullOrWhiteSpace(selectedText))
            {
                firstMessage = mode == ExplainMode.Translate
                    ? PromptBuilder.BuildTranslatePrompt(selectedText.Trim())
                    : selectedText.Trim();
                // 用户消息统一由 SendAsync 加入会话与 UI，避免两条路径重复添加
            }

            if (!string.IsNullOrWhiteSpace(selectedText))
            {
                Dispatcher.BeginInvoke(new Action(() => SendAsync(firstMessage!)));
            }
        }

        TitleText.Text = GetModeTitle(mode);
        Title = GetModeTitle(mode);
        _session?.Save();
    }

    private ExplainMode _mode;

    private static string GetModeTitle(ExplainMode mode) => mode switch
    {
        ExplainMode.Translate => "AI 翻译",
        ExplainMode.Search => "AI 搜索",
        _ => "AI 问答",
    };

    private void AddBubble(bool isUser, string content, bool isFillable = false)
    {
        _bubbles.Add(new ChatBubbleViewModel(isUser, content, isFillable));
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

    private void BtnSend_Click(object sender, RoutedEventArgs e) => SendCurrentInput();

    private void SendCurrentInput()
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        InputBox.Text = "";
        SendAsync(text);
    }

    /// <summary>发送一条消息并流式接收回复（真流式，逐块追加）</summary>
    private async void SendAsync(string text)
    {
        if (_session == null || _isStreaming) return;
        if (string.IsNullOrWhiteSpace(text)) return;

        var generation = _sessionGeneration;
        _isStreaming = true;
        BtnSend.IsEnabled = false;
        InputBox.IsEnabled = false;

        try
        {
            // 用户消息入会话 + 入 UI（修复：此前仅第一轮 selectedText 路径添加，输入框路径完全缺失，
            // 导致请求体 messages 无 user —— Agnes 400 "No user query" / DeepSeek 自说自话）
            _session.AddUser(text);
            AddBubble(true, text);

            AddBubble(false, "", _targetNote != null && _mode != ExplainMode.Ask);
            var current = _bubbles[^1];

            var sb = new StringBuilder();
            await foreach (var chunk in _provider.StreamAsync(_session.Messages))
            {
                if (generation != _sessionGeneration) return; // 会话已切换，丢弃旧流
                sb.Append(chunk);
                current.Content = sb.ToString();
            }

            var full = sb.ToString();
            if (generation != _sessionGeneration) return;
            if (string.IsNullOrWhiteSpace(full))
            {
                current.Content = "（模型未返回内容）";
            }
            else
            {
                _session.AddAssistant(full);
                _session.Save();
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
            if (generation == _sessionGeneration)
            {
                BtnSend.IsEnabled = true;
                InputBox.IsEnabled = true;
                InputBox.Focus();
                _session?.Save();
            }
        }
    }

    private void BtnFill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatBubbleViewModel bubble } btn) return;
        if (_targetNote == null || bubble.IsUser || bubble.IsFilled) return;

        if (ImmersiveSessionService.IsLocked(_targetNote.Timestamp))
        {
            System.Windows.MessageBox.Show(this, "沉浸式输入进行中，暂不可回填", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var fillText = bubble.Content.Trim();
        if (string.IsNullOrEmpty(fillText)) return;

        if (_noteService.AppendAiFill(_targetNote, fillText))
        {
            bubble.IsFilled = true;
            btn.Content = "已回填";
            btn.IsEnabled = false;
        }
        else
        {
            System.Windows.MessageBox.Show(this, "回填失败：未找到笔记文件或写入失败", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
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
        if (_noteService == null || _settings == null) return;

        if (string.IsNullOrWhiteSpace(_settings.AiApiKey))
        {
            System.Windows.MessageBox.Show(_owner ?? Application.Current.MainWindow,
                "请先在设置中配置 AI 模型", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_dialog == null || _dialog.IsClosed)
        {
            _dialog = new AIDialogWindow(_noteService, _settings);
            if (_owner != null && _owner.IsVisible)
                _dialog.Owner = _owner;
        }

        _dialog.OpenSession(mode, targetNote, selectedText);
        if (!_dialog.IsVisible) _dialog.Show();
        if (_dialog.WindowState == WindowState.Minimized) _dialog.WindowState = WindowState.Normal;
        _dialog.Activate();
        _dialog.Focus();
    }
}
