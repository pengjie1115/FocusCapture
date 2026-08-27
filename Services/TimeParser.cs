using System.Text.RegularExpressions;

namespace FocusCapture.Services;

/// <summary>本地规则时间解析：能规则解决的绝不调 LLM。解析出的时间若已过当前时刻 → 返回 false（不设提醒）。</summary>
public static class TimeParser
{
    // 规则 1：绝对日期时间 yyyy-MM-dd HH:mm / yyyy-M-d H:mm
    private static readonly Regex AbsDateTimeRe = new(@"\d{4}-\d{1,2}-\d{1,2} \d{1,2}:\d{2}", RegexOptions.Compiled);

    // 规则 2：日期 + 时间词（8月27日 9点 / 8月27日9:30 / 8月27日上午9点）
    private static readonly Regex MonthDayTimeRe = new(
        @"(\d{1,2})月(\d{1,2})日\s*(\d{1,2}):(\d{2})|(\d{1,2})月(\d{1,2})日\s*(上午|下午|早上|晚上|中午)?\s*(\d{1,2})点(半)?",
        RegexOptions.Compiled);

    // 规则 3：相对日 + 时间（今天|明天|后天 [时段] X点|X点半|X:XX）
    private static readonly Regex RelDayTimeRe = new(
        @"(今天|明天|后天)\s*(上午|下午|早上|晚上|中午)?\s*(\d{1,2}):(\d{2})|(今天|明天|后天)\s*(上午|下午|早上|晚上|中午)?\s*(\d{1,2})点(半)?",
        RegexOptions.Compiled);

    // 规则 4：星期 + 时间（周X|星期X [时段] X点）
    private static readonly Regex WeekTimeRe = new(
        @"(?:周|星期)([一二三四五六日天])\s*(上午|下午|早上|晚上|中午)?\s*(\d{1,2}):(\d{2})|(?:周|星期)([一二三四五六日天])\s*(上午|下午|早上|晚上|中午)?\s*(\d{1,2})点(半)?",
        RegexOptions.Compiled);

    // 规则 5：时段 + 数字（上午|下午|早上|晚上|中午 X点|X点半|X:XX）
    private static readonly Regex PeriodTimeRe = new(
        @"(上午|下午|早上|晚上|中午)\s*(\d{1,2}):(\d{2})|(上午|下午|早上|晚上|中午)\s*(\d{1,2})点(半)?",
        RegexOptions.Compiled);

    // 规则 6：数字时钟（X点 / X点Y分 / X:XX，24 小时制）
    private static readonly Regex ClockTimeRe = new(
        @"(\d{1,2})点(\d{1,2})分|(\d{1,2})点(半)?|(\d{1,2}):(\d{2})",
        RegexOptions.Compiled);

    // 规则 7：相对时长（N分钟后 / N小时后 / N天后）
    private static readonly Regex DurationRe = new(
        @"(\d+)\s*分钟(?:后)?|(\d+)\s*小时(?:后)?|(\d+)\s*天(?:后)?",
        RegexOptions.Compiled);

    /// <summary>
    /// 从文本中解析绝对/相对时间表达。成功 true + 绝对时间；失败 false。
    /// 多个规则都能命中时取文本中位置最靠前的时间表达；解析出的时间已过当前时刻 → false（不设提醒）。
    /// </summary>
    public static bool TryParse(string text, out DateTime time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        var bestIndex = int.MaxValue;
        DateTime? best = null;

        // 规则 1：绝对日期时间
        var m1 = AbsDateTimeRe.Match(text);
        if (m1.Success && DateTime.TryParse(m1.Value, out var t1))
            Consider(m1.Index, t1);

        // 规则 2：日期 + 时间词（今年日期已过 → 顺延明年，如 9 月输入"8月31日"指明年）
        var m2 = MonthDayTimeRe.Match(text);
        if (m2.Success)
        {
            DateTime t2;
            if (m2.Groups[1].Success)
            {
                t2 = BuildDateTime(DateTime.Now.Year, m2.Groups[1].Value, m2.Groups[2].Value,
                    int.Parse(m2.Groups[3].Value), int.Parse(m2.Groups[4].Value));
            }
            else
            {
                var hour = ApplyPeriod(int.Parse(m2.Groups[8].Value), m2.Groups[7].Value);
                var minute = m2.Groups[9].Success ? 30 : 0;
                t2 = BuildDateTime(DateTime.Now.Year, m2.Groups[5].Value, m2.Groups[6].Value, hour, minute);
            }
            Consider(m2.Index, t2);
        }

        // 规则 3：相对日 + 时间
        var m3 = RelDayTimeRe.Match(text);
        if (m3.Success)
        {
            DateTime t3;
            var dayOffset = m3.Groups[1].Success ? DayOffset(m3.Groups[1].Value) : DayOffset(m3.Groups[5].Value);
            if (m3.Groups[1].Success)
            {
                t3 = DateTime.Today.AddDays(dayOffset).AddHours(int.Parse(m3.Groups[3].Value)).AddMinutes(int.Parse(m3.Groups[4].Value));
            }
            else
            {
                var hour = ApplyPeriod(int.Parse(m3.Groups[7].Value), m3.Groups[6].Value);
                var minute = m3.Groups[8].Success ? 30 : 0;
                t3 = DateTime.Today.AddDays(dayOffset).AddHours(hour).AddMinutes(minute);
            }
            Consider(m3.Index, t3);
        }

        // 规则 4：星期 + 时间（今天已是该星期且目标时刻未过 → 今天；否则下周）
        var m4 = WeekTimeRe.Match(text);
        if (m4.Success)
        {
            DateTime t4;
            var target = ChineseWeekdayToInt(m4.Groups[1].Success ? m4.Groups[1].Value : m4.Groups[5].Value);
            var diff = (target - (int)DateTime.Now.DayOfWeek + 7) % 7;
            var date = DateTime.Today.AddDays(diff);
            int hour, minute;
            if (m4.Groups[1].Success)
            {
                hour = int.Parse(m4.Groups[3].Value);
                minute = int.Parse(m4.Groups[4].Value);
            }
            else
            {
                hour = ApplyPeriod(int.Parse(m4.Groups[7].Value), m4.Groups[6].Value);
                minute = m4.Groups[8].Success ? 30 : 0;
            }
            // 今天已是该星期但目标时刻已过 → 顺延下周
            if (diff == 0 && new DateTime(date.Year, date.Month, date.Day, hour, minute, 0) <= DateTime.Now)
                date = date.AddDays(7);
            t4 = date.AddHours(hour).AddMinutes(minute);
            Consider(m4.Index, t4);
        }

        // 规则 5：时段 + 数字（当天 12 小时制）
        var m5 = PeriodTimeRe.Match(text);
        if (m5.Success)
        {
            int hour, minute;
            if (m5.Groups[1].Success)
            {
                hour = int.Parse(m5.Groups[2].Value);
                minute = int.Parse(m5.Groups[3].Value);
            }
            else
            {
                hour = ApplyPeriod(int.Parse(m5.Groups[5].Value), m5.Groups[4].Value);
                minute = m5.Groups[6].Success ? 30 : 0;
            }
            Consider(m5.Index, DateTime.Today.AddHours(hour).AddMinutes(minute));
        }

        // 规则 6：数字时钟（24 小时制，无时段；已过 → 由最终边界判定 false）
        var m6 = ClockTimeRe.Match(text);
        if (m6.Success)
        {
            int hour, minute;
            if (m6.Groups[1].Success) { hour = int.Parse(m6.Groups[1].Value); minute = int.Parse(m6.Groups[2].Value); }
            else if (m6.Groups[3].Success) { hour = int.Parse(m6.Groups[3].Value); minute = m6.Groups[4].Success ? 30 : 0; }
            else { hour = int.Parse(m6.Groups[5].Value); minute = int.Parse(m6.Groups[6].Value); }
            Consider(m6.Index, DateTime.Today.AddHours(hour).AddMinutes(minute));
        }

        // 规则 7：相对时长（N分钟后 / N小时后 / N天后）
        var m7 = DurationRe.Match(text);
        if (m7.Success)
        {
            DateTime t7;
            if (m7.Groups[1].Success) t7 = DateTime.Now.AddMinutes(int.Parse(m7.Groups[1].Value));
            else if (m7.Groups[2].Success) t7 = DateTime.Now.AddHours(int.Parse(m7.Groups[2].Value));
            else t7 = DateTime.Now.AddDays(int.Parse(m7.Groups[3].Value));
            Consider(m7.Index, t7);
        }

        // 规则 8：无时间词不解析（含"明天/后天"但无时间词 → 无任何规则命中 → false，不做 09:00 默认）

        if (best == null) return false;
        if (best.Value <= DateTime.Now) return false;   // 已过时间不设提醒
        time = best.Value;
        return true;

        void Consider(int index, DateTime t)
        {
            if (index < bestIndex) { bestIndex = index; best = t; }
        }
    }

    /// <summary>按年月日时分构造绝对时间；今年该日期已过 → 顺延明年（日期表达指未来最近一次）</summary>
    private static DateTime BuildDateTime(int year, string monthStr, string dayStr, int hour, int minute)
    {
        var t = new DateTime(year, int.Parse(monthStr), int.Parse(dayStr), hour, minute, 0);
        if (t < DateTime.Now) t = t.AddYears(1);
        return t;
    }

    private static int DayOffset(string rel) => rel switch
    {
        "今天" => 0,
        "明天" => 1,
        "后天" => 2,
        _ => 0
    };

    private static int ChineseWeekdayToInt(string s) => s switch
    {
        "一" => 1, "二" => 2, "三" => 3, "四" => 4, "五" => 5, "六" => 6,
        "日" or "天" => 0,
        _ => -1
    };

    /// <summary>12 小时制时段 → 24 小时制小时（下午/晚上 +12；上午/早上不变；中午 12 保持 12）</summary>
    private static int ApplyPeriod(int hour, string period) => period switch
    {
        "下午" or "晚上" or "中午" => hour == 12 ? 12 : hour + 12,
        "上午" or "早上" => hour == 12 ? 0 : hour,
        _ => hour
    };

    /// <summary>DateTime → 自然语言（"今天 09:00" / "明天 上午9点" / "8月27日 20:00"），供建议条/弹窗文案用</summary>
    public static string FormatNaturalTime(DateTime time)
    {
        var now = DateTime.Now;
        var today = now.Date;
        if (time.Date == today)
            return $"今天 {time:HH:mm}";
        if (time.Date == today.AddDays(1))
            return $"明天 {FormatHourCn(time)}";
        return $"{time.Month}月{time.Day}日 {time:HH:mm}";
    }

    private static string FormatHourCn(DateTime t)
    {
        var h = t.Hour;
        var m = t.Minute;
        string period;
        if (h == 12) period = "中午";
        else if (h >= 5 && h < 12) period = "上午";
        else if (h >= 13 && h < 18) period = "下午";
        else if (h >= 18 && h < 24) period = "晚上";
        else period = "凌晨";
        var hh = h > 12 ? h - 12 : h;
        return m == 0 ? $"{period}{hh}点" : $"{period}{hh}点{m}分";
    }
}
