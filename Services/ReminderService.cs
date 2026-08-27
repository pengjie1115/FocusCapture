using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using FocusCapture.Models;

namespace FocusCapture.Services;

/// <summary>
/// v3.5 待办提醒服务（Phase 3）：定时器 + 弹窗调度 + 角标统计。
/// DispatcherTimer 每 30 秒 tick（禁止 Thread.Sleep 阻塞），tick 内读文件放后台线程避免 UI 卡顿，
/// 结果回到 UI 线程调用弹窗/汇总/角标回调。
/// </summary>
public class ReminderService
{
    private readonly NoteService _notes;
    private readonly AppSettings _settings;
    private readonly Action<List<NoteEntry>> _showDuePopups;  // 到点弹窗（同分钟合并传入）
    private readonly Action _showDailySummary;                // 每日汇总弹窗
    private readonly Action<int, bool> _updateBadge;          // 角标(count, hasRead)

    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _dueShown = new();       // 防重复：key 用 DueTime.ToString("yyyy-MM-dd HH:mm")|content
    private readonly HashSet<string> _dailyShownAt = new();   // 每日汇总已弹的触发分钟（yyyy-MM-dd HH:mm）

    private bool _stopped;

    public ReminderService(NoteService notes, AppSettings settings,
        Action<List<NoteEntry>> showDuePopups,
        Action showDailySummary,
        Action<int, bool> updateBadge)
    {
        _notes = notes;
        _settings = settings;
        _showDuePopups = showDuePopups;
        _showDailySummary = showDailySummary;
        _updateBadge = updateBadge;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) => await OnTickAsync();
    }

    public void Start()
    {
        _stopped = false;
        _timer.Start();
        Refresh(); // 启动时立刻算一次角标（Phase 3 验收 9：启动即初始化，无需等第一个 tick）
    }

    public void Stop()
    {
        _stopped = true;
        _timer.Stop();
    }

    /// <summary>数据变更后刷新角标（NotesChanged 订阅；到点检查由定时的 tick 负责）。</summary>
    public void Refresh() => RefreshBadgeDirect();

    /// <summary>
    /// 每 30 秒 tick：后台线程全量读 md → 过滤到点待办 → 按分钟分组弹窗；另做每日汇总触发与角标刷新。
    /// </summary>
    private async Task OnTickAsync()
    {
        if (_stopped) return;

        // 读文件放后台线程，避免 UI 线程每 30 秒全量读 md 文件卡顿（文件多时明显）
        var entries = await Task.Run(() =>
        {
            try { return _notes.LoadAllEntries(); }
            catch (Exception ex) { Debug.WriteLine($"[FocusCapture] Reminder 后台载入失败: {ex.Message}"); return new List<NoteEntry>(); }
        });

        if (_stopped) return;
        var now = DateTime.Now;

        // 到点待办：Type=Todo && TodoStatus=Open && DueTime ∈ (now-30s, now+30s] 且进程内未弹过
        var due = entries.Where(e =>
            e.Type == NoteType.Todo && e.TodoStatus == TodoStatus.Open &&
            e.DueTime.HasValue && e.DueTime.Value > now.AddSeconds(-30) && e.DueTime.Value <= now.AddSeconds(30))
            .ToList();

        List<NoteEntry> toShow = new();
        foreach (var e in due)
        {
            var key = $"{e.DueTime:yyyy-MM-dd HH:mm}|{e.EditedContent ?? e.Content}";
            if (_dueShown.Add(key)) toShow.Add(e);
        }

        // 同分钟合并成一次弹窗列表
        if (toShow.Count > 0)
        {
            var groups = toShow.GroupBy(e => e.DueTime!.Value.ToString("yyyy-MM-dd HH:mm"));
            foreach (var g in groups) _showDuePopups?.Invoke(g.ToList());
        }

        // 每日汇总：启用且命中 DailySummaryTime 且该触发分钟当日未弹过
        if (_settings.DailySummaryEnabled && TryMatchHhmm(now, _settings.DailySummaryTime))
        {
            var mark = now.ToString("yyyy-MM-dd HH:mm");
            if (_dailyShownAt.Add(mark)) _showDailySummary?.Invoke();
        }

        // 角标刷新：复用后台线程已载入的 entries，避免 UI 线程每 30s 再读一次文件
        UpdateBadgeFrom(entries);
    }

    /// <summary>Refresh()（NotesChanged）单独触发时重新载入并刷新角标，不做到点弹窗检查。</summary>
    private void RefreshBadgeDirect()
    {
        try { UpdateBadgeFrom(_notes.LoadAllEntries()); }
        catch (Exception ex) { Debug.WriteLine($"[FocusCapture] 角标刷新失败: {ex.Message}"); }
    }

    /// <summary>count=未办总数（Open+Read），hasRead=是否存在 Read。</summary>
    private void UpdateBadgeFrom(List<NoteEntry> entries)
    {
        var todos = entries.Where(e => e.Type == NoteType.Todo).ToList();
        var open = todos.Count(e => e.TodoStatus == TodoStatus.Open);
        var read = todos.Count(e => e.TodoStatus == TodoStatus.Read);
        _updateBadge?.Invoke(open + read, read > 0);
    }

    /// <summary>当前时刻是否命中 "HH:mm"（DailSummaryTime，00:00~23:59）。</summary>
    private static bool TryMatchHhmm(DateTime now, string hhmm)
    {
        var m = System.Text.RegularExpressions.Regex.Match(hhmm.Trim(), @"^(\d{1,2}):(\d{2})$");
        if (!m.Success) return false;
        int hh = int.Parse(m.Groups[1].Value);
        int mm = int.Parse(m.Groups[2].Value);
        if (hh < 0 || hh > 23 || mm < 0 || mm > 59) return false;
        return now.Hour == hh && now.Minute == mm;
    }
}