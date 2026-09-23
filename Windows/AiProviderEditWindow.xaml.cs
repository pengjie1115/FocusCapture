using System.Diagnostics;
using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;

namespace FocusCapture.Windows;

/// <summary>
/// 模型供应商编辑页（2026-09-23 多供应商改造）。
///
/// <para><b>改的是草稿副本</b>：点「取消」原配置一个字都不动。
/// 若直接改真实对象，「取消」就成了一句空话 —— 用户点了取消，配置却已经变了。</para>
///
/// <para>用 <c>ShowDialog</c> 是刻意的：编辑期间用户只该干这一件事。
/// 这跟「长驻面板一律 Show()」不冲突 —— 那条针对的是常驻面板（模态会禁用应用内所有窗口）。
/// 关闭窗口（右上角 ×）视为取消：<see cref="Result"/> 保持 null。</para>
/// </summary>
public partial class AiProviderEditWindow : Window
{
    private readonly AiProviderEntry _draft;
    private bool _keyVisible;

    /// <summary>装载/回显期间抑制 TextChanged 落值（与 SettingsWindow 的 _suppressEvents 同套做法）。</summary>
    private bool _loading;

    /// <summary>保存后的结果；取消或直接关窗时为 null。</summary>
    public AiProviderEntry? Result { get; private set; }

    public AiProviderEditWindow(AiProviderEntry draft)
    {
        _draft = draft;
        InitializeComponent();
        DarkTitleBar.Enable(this);

        _loading = true;
        try
        {
            EditNameInput.Text = _draft.Name;
            EditBaseUrlInput.Text = _draft.BaseUrl;
            EditKeyInput.Password = _draft.ApiKey;
            RebuildModelList();
        }
        finally { _loading = false; }

        RefreshPresetHint();
    }

    // ══════════════ 基本信息 ══════════════

    private void EditName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _draft.Name = EditNameInput.Text.Trim();
    }

    private void EditBaseUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _draft.BaseUrl = EditBaseUrlInput.Text.Trim();
        RefreshPresetHint();
    }

    private void EditKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _draft.ApiKey = EditKeyInput.Password;
    }

    private void EditKeyPlain_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _draft.ApiKey = EditKeyPlain.Text;
    }

    private void EditKeyToggle_Click(object sender, RoutedEventArgs e)
    {
        _keyVisible = !_keyVisible;
        var prev = _loading;
        _loading = true;   // 同步期间不落值，避免来回写
        try
        {
            if (_keyVisible)
            {
                EditKeyPlain.Text = EditKeyInput.Password;
                EditKeyInput.Visibility = Visibility.Collapsed;
                EditKeyPlain.Visibility = Visibility.Visible;
                EditKeyPlain.Focus();
                EditKeyPlain.CaretIndex = EditKeyPlain.Text.Length;
            }
            else
            {
                EditKeyInput.Password = EditKeyPlain.Text;
                EditKeyPlain.Visibility = Visibility.Collapsed;
                EditKeyInput.Visibility = Visibility.Visible;
            }
        }
        finally { _loading = prev; }
    }

    /// <summary>「申请 Key」只在 BaseUrl 命中预置表时出现 —— 自定义地址没有对应的申请页可跳。</summary>
    private void RefreshPresetHint()
    {
        var preset = AiProviders.MatchByUrl(_draft.BaseUrl);
        EditPresetHint.Text = preset is null
            ? "自定义地址（未匹配到预置供应商）"
            : $"匹配到预置供应商：{preset.Name}";
        EditKeyApplyLink.Visibility = preset?.KeyApplyUrl is { Length: > 0 }
            ? Visibility.Visible : Visibility.Collapsed;
        EditKeyApplyLink.Tag = preset;
    }

    private void EditKeyApplyLink_Click(object sender, RoutedEventArgs e)
    {
        if (EditKeyApplyLink.Tag is not AiProviderPreset preset) return;
        if (string.IsNullOrEmpty(preset.KeyApplyUrl)) return;
        try { Process.Start(new ProcessStartInfo(preset.KeyApplyUrl) { UseShellExecute = true }); }
        catch { /* 无默认浏览器或被取消，静默（弹模态框会吃掉后续点击） */ }
    }

    // ══════════════ 模型目录 ══════════════

    private void BtnAddModel_Click(object sender, RoutedEventArgs e)
    {
        _draft.Models.Add(new AiModelEntry());
        RebuildModelList();
    }

    private void RebuildModelList()
    {
        EditModelList.Children.Clear();
        foreach (var m in _draft.Models) EditModelList.Children.Add(BuildModelRow(m));
        EditEmptyHint.Visibility = _draft.Models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EditModelCountText.Text = _draft.Models.Count == 0 ? "模型目录" : $"模型目录（{_draft.Models.Count}）";
    }

    /// <summary>
    /// 建一行模型。**只读/编辑两套元素一次建好、只切 Visibility，不重建列表** ——
    /// 重建会换掉条目对象引用，正在编辑的目标与草稿一起丢（本项目踩过这个坑）。
    /// </summary>
    private FrameworkElement BuildModelRow(AiModelEntry model)
    {
        var root = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var idBox = new TextBox
        {
            Text = model.Id,
            ToolTip = "模型 ID —— 原样发给供应商的标识，如 deepseek-chat",
            Style = (Style)FindResource("DarkTextBox"),
        };
        idBox.TextChanged += (_, _) => { if (!_loading) model.Id = idBox.Text.Trim(); };
        Grid.SetColumn(idBox, 0);
        head.Children.Add(idBox);

        var nameBox = new TextBox
        {
            Text = model.DisplayName,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "显示名 —— 只给界面看，留空则回退显示模型 ID",
            Style = (Style)FindResource("DarkTextBox"),
        };
        nameBox.TextChanged += (_, _) => { if (!_loading) model.DisplayName = nameBox.Text.Trim(); };
        Grid.SetColumn(nameBox, 1);
        head.Children.Add(nameBox);

        // 展开区：上下文窗口 + 最大输出 Token（**留空 = 不限制**，不做猜测预填）
        var expandPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 2) };
        expandPanel.Children.Add(BuildTokenRow("上下文窗口", model.ContextWindow, TokenCountParser.MaxContextWindow,
            "超出这个长度的历史消息会被丢弃；留空 = 不裁剪（本项目改造前的行为）",
            v => model.ContextWindow = v));
        expandPanel.Children.Add(BuildTokenRow("最大输出 Token", model.MaxOutputTokens, TokenCountParser.MaxOutputTokens,
            "单次回答的长度上限；留空 = 不传该字段，由供应商默认值决定",
            v => model.MaxOutputTokens = v));

        var expandBtn = new Button
        {
            Content = "⌄", Width = 30, Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center,
            ToolTip = "展开：单独设置该模型的上下文窗口与最大输出 Token",
        };
        expandBtn.Click += (_, _) =>
        {
            var show = expandPanel.Visibility != Visibility.Visible;
            expandPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            expandBtn.Content = show ? "⌃" : "⌄";
        };
        Grid.SetColumn(expandBtn, 2);
        head.Children.Add(expandBtn);

        var delBtn = new Button
        {
            Content = "✕", Width = 30, Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center,
            ToolTip = "从该供应商移除这个模型（不影响供应商本身）",
        };
        delBtn.Click += (_, _) =>
        {
            _draft.Models.Remove(model);
            RebuildModelList();
        };
        Grid.SetColumn(delBtn, 3);
        head.Children.Add(delBtn);

        root.Children.Add(head);
        root.Children.Add(expandPanel);
        return root;
    }

    /// <summary>一行「标签 + 输入框 + 灰色说明」。数值解析统一走 TokenCountParser，非法输入不落值（保留原值）。</summary>
    private FrameworkElement BuildTokenRow(string label, int current, int max, string hint, Action<int> apply)
    {
        var panel = new StackPanel();

        var row = new DockPanel();
        panel.Children.Add(row);

        var labelBlock = new TextBlock
        {
            Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
            FontSize = 12,
        };
        DockPanel.SetDock(labelBlock, Dock.Left);
        row.Children.Add(labelBlock);

        var box = new TextBox
        {
            Text = current > 0 ? current.ToString() : "",
            ToolTip = "可填 384K / 1M 这类写法；留空 = 不限制",
            Style = (Style)FindResource("DarkTextBox"),
        };
        box.TextChanged += (_, _) =>
        {
            if (_loading) return;
            // 非法输入刻意**不落值**：用户打字中途（如刚要补 K）不该把配置改成 0
            if (TokenCountParser.TryParse(box.Text, out var v)) apply(Math.Clamp(v, 0, max));
        };
        row.Children.Add(box);

        panel.Children.Add(new TextBlock
        {
            Text = hint, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(110, 2, 0, 0),
        });
        return panel;
    }

    // ══════════════ 测试连接 ══════════════

    private async void BtnEditTest_Click(object sender, RoutedEventArgs e)
    {
        var modelId = _draft.Models.Select(m => m.Id).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        if (string.IsNullOrWhiteSpace(_draft.BaseUrl))
        {
            ShowTestResult("请先填写 API 地址", false);
            return;
        }
        if (modelId is null)
        {
            ShowTestResult("请先添加一个模型（要有模型 ID）再测试", false);
            return;
        }

        BtnEditTest.IsEnabled = false;
        ShowTestResult("连接中…", null);
        try
        {
            var maxTokens = _draft.Models.First(m => m.Id == modelId).MaxOutputTokens;
            var provider = new OpenAICompatibleProvider(_draft.BaseUrl, _draft.ApiKey, modelId, maxTokens);
            var ok = await provider.TestConnectionAsync();
            if (ok)
            {
                // 记的是「上次检测通过 + 时间」，不是「可用」的保证 —— 余额耗尽 / Key 被撤销都查不出来
                _draft.LastTestedAt = DateTime.Now.ToString("s");
                _draft.LastTestStatus = "Ok";
                _draft.LastTestMessage = "";
            }
            ShowTestResult(ok ? "连接成功" : "连接失败", ok);
        }
        catch (Exception ex)
        {
            // 失败**不写状态**：这里只做一次性连通测试，三分类（网络/Key/供应商/余额）归步骤 5 的健康探测
            ShowTestResult(ex.Message, false);
        }
        finally
        {
            BtnEditTest.IsEnabled = true;
        }
    }

    private void ShowTestResult(string text, bool? ok)
    {
        EditTestResult.Text = text;
        EditTestResult.Foreground = new SolidColorBrush(ok switch
        {
            true => Color.FromRgb(0x4C, 0xAF, 0x50),
            false => Color.FromRgb(0xE5, 0x39, 0x35),
            null => Color.FromRgb(0xCC, 0xCC, 0xCC),
        });
    }

    // ══════════════ 取消 / 保存 ══════════════

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Result = null;          // 草稿整个丢弃：真实配置由调用方持有，一个字都没被改过
        DialogResult = false;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_draft.BaseUrl))
        {
            ShowTestResult("API 地址不能为空 —— 没有地址这条供应商什么也做不了", false);
            return;
        }
        if (string.IsNullOrWhiteSpace(_draft.Name))
            _draft.Name = AiProviders.MatchByUrl(_draft.BaseUrl)?.Name ?? AiProviders.Custom;

        // 模型 ID 为空的条目是用户点了「添加模型」但还没填的占位行，不算数据
        var placeholders = _draft.Models.RemoveAll(m => string.IsNullOrWhiteSpace(m.Id));
        if (placeholders > 0) AppLog.Info("AI", $"保存供应商时丢弃 {placeholders} 条未填模型 ID 的占位行");

        Result = _draft;
        DialogResult = true;
    }
}
