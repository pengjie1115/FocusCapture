using System.Collections.ObjectModel;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;

namespace FocusCapture.Windows;

/// <summary>
/// AI 整理预览对照窗（2026-09-26 新增；2026-09-27 规则化改造）：
/// 左原文只读、右整理结果（文本流 或 提取待办的行列表），底部规则条 + 出口。
///
/// <para><b>为什么必须有这一屏</b>：AI 整理会改写用户自己的原始记录，而模型整理错（漏要点、顺手改写、
/// 把两件事并成一件）是<b>看不出来</b>的 —— 直接落库等于让模型替用户做决定。先对照再选择，
/// 且结果可编辑（用户顺手改两个字就能用，不必重来一次）。</para>
///
/// <para><b>窗口只负责"选哪个规则 + 选哪个出口 + 最终文本是什么"，不碰数据层</b>：落库由
/// <see cref="AiTidyFlow"/> 统一做（各调用方的刷新方式不同），避免这个窗口依赖 NoteService。</para>
///
/// <para><b>2026-09-27 新增</b>：① 底部规则条 —— 点规则按<b>原文</b>重新整理（结果可被覆盖，
/// 已手改过会先确认）；② 提取待办规则下结果区切成待办行列表（行内日期 / 时间框），
/// 出口多一个「创建待办」；③ 「自定义」跳设置（非模态，本窗保持打开，回来自动刷新规则条）。</para>
///
/// <para><b>非模态</b>（2026-09-27 用户拍板：预览窗开着时灵感速览要能操作）：用 Show 不用 ShowDialog，
/// 不再依赖 DialogResult —— 选了出口就把属性落好再 Close，由 AiTidyFlow 在 Closed 后读取。</para>
/// </summary>
public partial class AiTidyPreviewWindow : Window
{
    /// <summary>预览窗的输入包：原文、首整理结果、可用规则、模型与 provider（重跑用）。
    /// Rules / ShowCustomEntry 可写 —— 预览窗与设置窗非模态并存，用户改完设置回来时按 settings 刷新。</summary>
    public sealed class TidyPreviewRequest
    {
        public string Original { get; init; } = "";
        public string InitialTidied { get; init; } = "";
        public TidyRule InitialRule { get; init; } = new();
        public IReadOnlyList<TidyRule> Rules { get; set; } = Array.Empty<TidyRule>();
        public bool ShowCustomEntry { get; set; } = true;
        public string? ModelLabel { get; init; }
        public IChatProvider? Provider { get; init; }
        public AppSettings? Settings { get; init; }
    }

    /// <summary>待办行视图模型（提取待办形态下的一行：文字 + 截止日期 + 截止时间文字）。</summary>
    public sealed class TodoRowVm
    {
        public string Text { get; set; } = "";
        public DateTime? Date { get; set; }
        public string TimeText { get; set; } = "";
    }

    /// <summary>用户选的出口（关窗 / Esc = None）。</summary>
    public TidyChoice Choice { get; private set; } = TidyChoice.None;

    /// <summary>用户最终确认的文本（可能被手动微调过；提取待办形态 = 各行重组的文本）。</summary>
    public string ResultText { get; private set; } = "";

    /// <summary>「创建待办」出口的草稿列表（仅该出口下有内容）。</summary>
    public List<TidyTodoDraft> TodoItems { get; private set; } = new();

    private readonly TidyPreviewRequest _req;
    private TidyRule _currentRule;
    private readonly ObservableCollection<TodoRowVm> _todoRows = new();
    private bool _rerunning;          // 重跑进行中：规则条与出口一并禁用，防拿到半截结果
    private bool _resultDirty;        // 结果被用户手改过（换规则覆盖前要确认）
    private bool _applyingResult;     // 程序性写结果时抑制 dirty 标记

    public AiTidyPreviewWindow(TidyPreviewRequest request)
    {
        InitializeComponent();
        // 深色标题栏：WPF 不主动申请，系统深色模式下也可能渲染成白底（项目已踩过）
        DarkTitleBar.Enable(this);
        _req = request;
        _currentRule = request.InitialRule;
        TodoList.ItemsSource = _todoRows;
        OriginalBox.Text = request.Original ?? "";
        ApplyTextResult(request.InitialTidied ?? "");
        ModelText.Text = string.IsNullOrWhiteSpace(request.ModelLabel) ? "" : "整理模型：" + request.ModelLabel;
        RebuildRuleBar();
        ResultBox.TextChanged += (_, _) => { if (!_applyingResult) _resultDirty = true; };
        Activated += (_, _) => RefreshChipsFromSettings();   // 跳设置改完规则回来 → 规则条自动跟上（非模态下两窗同时开着）
    }

    // ═══════════════ 规则条 ═══════════════

    /// <summary>重建规则条（首次 + 从设置回来时）。规则来自 settings 的活引用 —— 设置里改了显隐 / 排序 / 增删，这里照实刷新。</summary>
    private void RebuildRuleBar()
    {
        RuleBar.Children.Clear();
        foreach (var rule in _req.Rules)
            RuleBar.Children.Add(MakeRuleChip(rule));
        BtnCustom.Visibility = _req.ShowCustomEntry ? Visibility.Visible : Visibility.Collapsed;
        HighlightCurrentChip();
    }

    /// <summary>窗口被再次激活时同步一次规则条（设置窗是非模态单例，用户可能刚在里面增删改过规则）。</summary>
    private void RefreshChipsFromSettings()
    {
        var settings = _req.Settings;
        if (settings == null) return;
        _req.Rules = TidyRuleCatalog.ResolveVisible(settings);
        _req.ShowCustomEntry = settings.TidyShowCustomEntry;
        RebuildRuleBar();
    }

    private Button MakeRuleChip(TidyRule rule)
    {
        var b = new Button
        {
            Content = rule.Name,
            FontSize = 12,
            Height = 26,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(1),
            Tag = rule,
            ToolTip = rule.IsBuiltin ? "内置规则" : "自定义规则",
        };
        b.Click += async (_, _) => await UseRuleAsync(rule);
        return b;
    }

    private void HighlightCurrentChip()
    {
        foreach (var child in RuleBar.Children.OfType<Button>())
        {
            if (child.Tag is not TidyRule rule) continue;
            var current = rule.Id == _currentRule.Id;
            child.BorderBrush = new SolidColorBrush(current ? Color.FromRgb(0x4C, 0xAF, 0x50) : Color.FromRgb(0x55, 0x55, 0x55));
            child.Foreground = new SolidColorBrush(current ? Color.FromRgb(0x4C, 0xAF, 0x50) : Color.FromRgb(0xCC, 0xCC, 0xCC));
            child.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ═══════════════ 换规则重跑 ═══════════════

    /// <summary>
    /// 点规则 chip：按<b>原文</b>按新规则重新整理。已手改过的结果先确认再覆盖；
    /// 新规则是提取待办 → 结果区切待办行列表。失败保留原结果并报人话（规则已切走，但结果不丢）。
    /// </summary>
    private async Task UseRuleAsync(TidyRule rule)
    {
        if (_rerunning || rule.Id == _currentRule.Id) return;
        if (_req.Provider == null)
        {
            StatusText.Text = "当前环境没有可用的整理模型，无法换规则重跑。";
            return;
        }
        if (_resultDirty && ResultBox.Text.Trim().Length > 0)
        {
            var confirm = System.Windows.MessageBox.Show(this,
                "换规则会覆盖当前整理结果（包括你手动改过的部分），继续吗？",
                "AI 整理", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }

        _rerunning = true;
        SetBottomBarEnabled(false);
        StatusText.Text = $"正在按「{rule.Name}」重新整理…";
        try
        {
            var outcome = await NoteTidyService.TidyAsync(_req.Provider, _req.Original, rule).ConfigureAwait(true);
            if (!outcome.Ok)
            {
                StatusText.Text = outcome.Error ?? "整理失败。";
                return;   // 结果区保留上一个规则的结果，用户可继续选出口或再换规则
            }
            _currentRule = rule;
            if (TidyRuleCatalog.KindOf(rule) == TidyRuleKind.ExtractTodo)
                ApplyTodoResult(outcome.Text);
            else
                ApplyTextResult(outcome.Text);
            StatusText.Text = "";
        }
        finally
        {
            _rerunning = false;
            SetBottomBarEnabled(true);
            HighlightCurrentChip();
        }
    }

    private void SetBottomBarEnabled(bool enabled)
    {
        foreach (var child in RuleBar.Children.OfType<Button>()) child.IsEnabled = enabled;
        BtnCustom.IsEnabled = enabled;
        BtnCopy.IsEnabled = enabled;
        BtnSaveAsNew.IsEnabled = enabled;
        BtnReplace.IsEnabled = enabled;
        BtnCreateTodos.IsEnabled = enabled;
        BtnClose.IsEnabled = enabled;
    }

    // ═══════════════ 结果区两种形态 ═══════════════

    private void ApplyTextResult(string text)
    {
        _applyingResult = true;
        try
        {
            ResultBox.Text = text;
            _resultDirty = false;
        }
        finally { _applyingResult = false; }
        ResultBox.Visibility = Visibility.Visible;
        TodoHost.Visibility = Visibility.Collapsed;
        BtnCreateTodos.Visibility = Visibility.Collapsed;
        ResultHeader.Text = "整理结果 · " + _currentRule.Name;
    }

    /// <summary>
    /// 提取待办形态：回包逐行解析成待办行（文字 + 截止时间回填到行内日期 / 时间框）。
    /// 解析出 0 条（含「未发现待办」）→ 状态栏说明 + 退回纯文本展示，「创建待办」不出现 —— 兜底不许缺席。
    /// </summary>
    private void ApplyTodoResult(string text)
    {
        var parsed = NoteTidyPrompt.ParseTodoLines(text);
        if (NoteTidyPrompt.IsNoTodoMarker(text) || parsed.Count == 0)
        {
            // 兜底：解析出 0 条（含「未发现待办」）→ 退回纯文本展示，「创建待办」不出现
            ApplyTextResult(text);
            StatusText.Text = NoteTidyPrompt.IsNoTodoMarker(text)
                ? "这段内容里没有识别到待办。"
                : "模型这次没有按行返回待办，已按普通文本展示 —— 可以复制 / 另存，或换个规则再试。";
            return;
        }

        _applyingResult = true;
        try
        {
            _todoRows.Clear();
            foreach (var (t, due) in parsed)
                _todoRows.Add(new TodoRowVm
                {
                    Text = t,
                    Date = due?.Date,
                    TimeText = due is { } d && d.TimeOfDay != TimeSpan.Zero ? d.ToString("HH:mm") : "",
                });
            _resultDirty = false;
        }
        finally { _applyingResult = false; }
        ResultBox.Visibility = Visibility.Collapsed;
        TodoHost.Visibility = Visibility.Visible;
        BtnCreateTodos.Visibility = Visibility.Visible;
        ResultHeader.Text = "整理结果 · " + _currentRule.Name + "（可改文字 / 补日期时间）";
    }

    /// <summary>重组当前待办行为文本（复制 / 另存 / 替换出口在待办形态下也走文本口径）。</summary>
    private string BuildTodoText()
    {
        var lines = new List<string>();
        foreach (var r in _todoRows)
        {
            var t = (r.Text ?? "").Trim();
            if (t.Length == 0) continue;
            var due = CombineDue(r);
            lines.Add(due != null ? $"【{due:yyyy-MM-dd HH:mm}】{t}" : t);
        }
        return string.Join("\n", lines);
    }

    /// <summary>行内日期 + 时间合成截止时间；只填了时间没填日期返回 null（校验在出口处做）。</summary>
    private static DateTime? CombineDue(TodoRowVm r)
    {
        if (r.Date == null) return null;
        var due = r.Date.Value.Date;
        var m = Regex.Match((r.TimeText ?? "").Trim(), @"^(\d{1,2}):(\d{2})$");
        if (m.Success) due = due.AddHours(int.Parse(m.Groups[1].Value)).AddMinutes(int.Parse(m.Groups[2].Value));
        return due;
    }

    // ═══════════════ 出口 ═══════════════

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();   // Choice 保持 None = 什么都没做
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e) => Take(TidyChoice.Copy);
    private void BtnSaveAsNew_Click(object sender, RoutedEventArgs e) => Take(TidyChoice.SaveAsNew);
    private void BtnReplace_Click(object sender, RoutedEventArgs e) => Take(TidyChoice.Replace);
    private void BtnCreateTodos_Click(object sender, RoutedEventArgs e) => Take(TidyChoice.CreateTodos);
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>取出结果并关窗（非模态：不再用 DialogResult，属性落好即走）。空结果不许出口。</summary>
    private void Take(TidyChoice choice)
    {
        if (_rerunning) return;

        string text;
        var drafts = new List<TidyTodoDraft>();
        if (TodoHost.Visibility == Visibility.Visible)
        {
            // 提取待办形态：先校验行内时间填写，再按行组装
            var rows = _todoRows.Select((r, i) => (Row: r, Index: i + 1)).Where(x => (x.Row.Text ?? "").Trim().Length > 0).ToList();
            if (rows.Count == 0)
            {
                StatusText.Text = "没有可创建的待办 —— 每行文字都空着，请先补上内容或直接关掉这个窗口。";
                return;
            }
            foreach (var (row, index) in rows)
            {
                var time = (row.TimeText ?? "").Trim();
                if (time.Length > 0 && row.Date == null)
                {
                    StatusText.Text = $"第 {index} 条填了时间但没填日期 —— 时间要跟日期一起填，或把时间清掉。";
                    return;
                }
                if (time.Length > 0 && CombineDue(row) == null)
                {
                    StatusText.Text = $"第 {index} 条的时间格式不对（要 HH:mm，例如 09:30）。";
                    return;
                }
                drafts.Add(new TidyTodoDraft(row.Text.Trim(), CombineDue(row)));
            }
            text = BuildTodoText();
        }
        else
        {
            text = (ResultBox.Text ?? "").Trim();
            if (text.Length == 0)
            {
                StatusText.Text = "整理结果不能为空 —— 请先补上内容，或直接关掉这个窗口。";
                return;
            }
        }

        Choice = choice;
        ResultText = text;
        TodoItems = drafts;
        Close();
    }

    private void BtnRemoveTodoRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is TodoRowVm vm) _todoRows.Remove(vm);
    }

    // ═══════════════ 跳设置 + 快照 ═══════════════

    /// <summary>
    /// 「自定义」入口：打开设置 → 整理规则板块。<b>非模态</b>（2026-09-27 拍板）：
    /// 设置窗本来就是非模态单例，本预览窗保持打开，用户改完规则切回来规则条自动刷新。
    /// </summary>
    private void BtnCustom_Click(object sender, RoutedEventArgs e)
    {
        if (WpfApp.Current.MainWindow is MainWindow main)
        {
            main.OpenSettingsToTidyRules();
            return;
        }
        StatusText.Text = "找不到主窗口，请手动打开 设置 → 整理规则。";
    }

    /// <summary>快照专用：不调模型，直接切到提取待办形态并填入样例行（出图守护要看行内日期 / 时间框长什么样）。</summary>
    public void SeedTodoModeForSnapshot()
    {
        var rule = _req.Rules.FirstOrDefault(r => TidyRuleCatalog.KindOf(r) == TidyRuleKind.ExtractTodo)
                   ?? _currentRule;
        _currentRule = rule;
        ApplyTodoResult("【2026-09-27 10:00】把整理好的方案发到项目群\n给供应商回电话问加急费");
        HighlightCurrentChip();
    }
}
