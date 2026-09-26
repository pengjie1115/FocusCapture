using FocusCapture.Models;
using FocusCapture.Services;
using FocusCapture.Services.AI;

namespace FocusCapture.Windows;

/// <summary>
/// 「AI 整理」的共用流程（2026-09-26 新增）：<b>整理 → 预览对照 → 按出口落库</b>，三个入口共用这一份。
///
/// <para><b>为什么抽成一处</b>：灵感速览面板 / 待办汇总面板 / 全屏编辑窗三处都要这个功能，
/// 各写一遍必然漂移成三套行为（本项目在导入流程上已经栽过一次 —— 见 Windows/ImportFlow.cs 的来历）。
/// 各入口的差异只在"落库后怎么刷新自己"，所以刷新留给调用方，其余全部收在这里。</para>
///
/// <para><b>失败一律不抛</b>：超长、未配模型、网络失败都弹一句人话给用户，返回 null 表示"什么都没做"。</para>
/// </summary>
public static class AiTidyFlow
{
    /// <summary>流程结果：用户选了哪个出口、最终文本、以及**数据是否真的变了**（调用方据此决定要不要刷新列表）。</summary>
    public sealed record FlowResult(TidyChoice Choice, string Text, bool DataChanged);

    /// <summary>
    /// 跑完整流程。返回 null = 没做成（已弹提示）或用户关掉了预览窗 —— 两种情况调用方都无需刷新。
    /// </summary>
    /// <param name="entry">被整理的那条（用于预览窗标题与落库定位）。</param>
    /// <param name="text">送给模型的正文（行内编辑态下应是编辑框里正在改的内容，而不是已落盘的旧文本）。</param>
    public static async Task<FlowResult?> RunAsync(Window? owner, NoteService notes, AppSettings? settings,
        IChatProvider? activeProvider, NoteEntry entry, string? text)
    {
        var provider = NoteTidyService.ResolveProvider(activeProvider, settings);
        var outcome = await NoteTidyService.TidyAsync(provider, text).ConfigureAwait(true);
        if (!outcome.Ok)
        {
            Warn(owner, outcome.Error ?? "整理失败。");
            return null;
        }

        var modelLabel = ResolveModelLabel(settings, provider);

        TidyChoice choice;
        string finalText;
        try
        {
            var preview = new AiTidyPreviewWindow((text ?? "").Trim(), outcome.Text, modelLabel) { Owner = owner };
            if (preview.ShowDialog() != true) return null;   // 关窗 / Esc = 什么都不做
            choice = preview.Choice;
            finalText = preview.ResultText;
        }
        catch (Exception ex)
        {
            // owner 已关闭等边缘情况：宁可什么都不做，也不能把异常抛到全局变成崩溃框
            AppLog.Error("AiTidy", "预览窗打开失败", ex);
            return null;
        }

        return Apply(notes, entry, choice, finalText);
    }

    /// <summary>
    /// 按出口落库（不含界面刷新）：
    /// <list type="bullet">
    /// <item><b>复制</b> —— 只写剪贴板（走 SafeClipboard，被占用时退避重试、绝不抛，红线 9）。</item>
    /// <item><b>另存为新笔记</b> —— 原文不动，新增一条独立笔记（来源标「AI 整理」，与「AI 回填」区分开）。</item>
    /// <item><b>替换原文</b> —— 原地改行（TodoEditService.SaveEdited）：旧行进回收站可恢复、发删除墓碑防云端旧行回灌。</item>
    /// </list>
    /// </summary>
    public static FlowResult? Apply(NoteService notes, NoteEntry entry, TidyChoice choice, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        switch (choice)
        {
            case TidyChoice.Copy:
                ClipboardHookService.MarkSelfCopy();
                SafeClipboard.TrySetText(text, WpfClipboard.SetText);
                return new FlowResult(choice, text, DataChanged: false);

            case TidyChoice.SaveAsNew:
                return notes.SaveNote(text, "AI 整理") != null
                    ? new FlowResult(choice, text, DataChanged: true)
                    : null;

            case TidyChoice.Replace:
                if (!TodoEditService.SaveEdited(notes, entry, text)) return null;
                // 存储层状态同步（与 QuickViewWindow.SaveEditNote 同口径）：
                // 旧 EditedContent（历史编辑痕迹的合并结果）必须清掉，否则展示层会拿旧文本盖住新正文
                entry.Content = text;
                if (entry.Type != NoteType.Todo) entry.EditedContent = null;
                return new FlowResult(choice, text, DataChanged: true);

            default:
                return null;
        }
    }

    /// <summary>预览窗右上角显示的模型名 —— 用户能当场核对"我选的整理模型真的生效了"。</summary>
    private static string ResolveModelLabel(AppSettings? settings, IChatProvider? provider)
    {
        if (provider == null) return "";
        if (settings == null) return provider.Model;
        // 显式选了整理模型 → 显示它（与设置页同一套展示口径：以用户填的显示名为准，重名才补供应商）；
        // 没选（跟随活跃模型）→ 显示实际在用的那个模型的 ID
        var picked = AiModelResolver.TryResolveExact(settings, settings.AiTidyModelKey);
        return picked != null ? AiModelResolver.DisplayNameFor(settings, picked) : provider.Model;
    }

    /// <summary>弹提示。owner 可能已关闭（面板被收掉），此时退回无宿主的弹框，不抛。</summary>
    private static void Warn(Window? owner, string message)
    {
        try
        {
            if (owner != null && owner.IsLoaded)
                System.Windows.MessageBox.Show(owner, message, "AI 整理", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                System.Windows.MessageBox.Show(message, "AI 整理", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Error("AiTidy", "提示弹框失败", ex);
        }
    }
}
