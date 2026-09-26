using FocusCapture.Services;

namespace FocusCapture.Windows;

/// <summary>「AI 整理」预览窗的三个出口（2026-09-26：用户拍板不做「追加为子条目」，故只有三个）。</summary>
public enum TidyChoice
{
    /// <summary>没选（关窗 / Esc）—— 什么都不做</summary>
    None,
    /// <summary>只把整理结果复制到剪贴板，笔记不动</summary>
    Copy,
    /// <summary>原文不动，整理结果另存成一条新笔记</summary>
    SaveAsNew,
    /// <summary>用整理结果原地替换这条内容（旧文本先进回收站，可恢复）</summary>
    Replace,
}

/// <summary>
/// AI 整理预览对照窗（2026-09-26 新增）：左原文只读、右整理结果可编辑，底部三个出口。
///
/// <para><b>为什么必须有这一屏</b>：AI 整理会改写用户自己的原始记录，而模型整理错（漏要点、顺手改写、
/// 把两件事并成一件）是<b>看不出来</b>的 —— 直接落库等于让模型替用户做决定。先对照再选择，
/// 且结果框可编辑（用户顺手改两个字就能用，不必重来一次）。</para>
///
/// <para>窗口只负责"选哪个出口 + 最终文本是什么"，<b>不碰数据层</b>：落库由各调用方按自己的场景做
/// （三处的刷新方式不同），避免这个窗口依赖 NoteService。</para>
/// </summary>
public partial class AiTidyPreviewWindow : Window
{
    /// <summary>用户选的出口（关窗 = None）。</summary>
    public TidyChoice Choice { get; private set; } = TidyChoice.None;

    /// <summary>用户在结果框里最终确认的文本（可能被手动微调过）。</summary>
    public string ResultText { get; private set; } = "";

    public AiTidyPreviewWindow(string original, string tidied, string? modelLabel = null)
    {
        InitializeComponent();
        // 深色标题栏：WPF 不主动申请，系统深色模式下也可能渲染成白底（项目已踩过）
        DarkTitleBar.Enable(this);
        OriginalBox.Text = original ?? "";
        ResultBox.Text = tidied ?? "";
        ModelText.Text = string.IsNullOrWhiteSpace(modelLabel) ? "" : "整理模型：" + modelLabel;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();   // Choice 保持 None = 什么都没做
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e) => Take(TidyChoice.Copy);
    private void BtnSaveAsNew_Click(object sender, RoutedEventArgs e) => Take(TidyChoice.SaveAsNew);
    private void BtnReplace_Click(object sender, RoutedEventArgs e) => Take(TidyChoice.Replace);
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>取出结果并关窗。空结果不许出口（否则会把一条内容改成空，或被写进回收站）。</summary>
    private void Take(TidyChoice choice)
    {
        var text = (ResultBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            StatusText.Text = "整理结果不能为空 —— 请先补上内容，或直接关掉这个窗口。";
            return;
        }

        Choice = choice;
        ResultText = text;
        DialogResult = true;
    }
}
