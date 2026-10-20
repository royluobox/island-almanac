using System.Text.Json;
using System.Text.Json.Serialization;

namespace IslandAlmanac.Core;

internal enum ReminderRepeat
{
    Once = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
    Yearly = 4,
}

internal sealed class Reminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..10];
    public string Title { get; set; } = string.Empty;
    public ReminderRepeat Repeat { get; set; } = ReminderRepeat.Once;

    /// <summary>起算日期（单次＝当天；重复＝从这天开始生效）。</summary>
    public DateOnly StartDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    public TimeOnly Time { get; set; } = new(9, 0);

    /// <summary>每周重复的星期（0=周日 … 6=周六）。</summary>
    public List<int> Weekdays { get; set; } = new();

    /// <summary>每月重复的几号（1-31，遇到短月自动落到月末）。</summary>
    public int DayOfMonth { get; set; }

    public int LeadMinutes { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>上一次已提醒的具体时刻，用来避免同一次重复触发两遍。</summary>
    public DateTime? LastFired { get; set; }

    /// <summary>「稍后提醒」的目标时刻。设置后优先于正常周期触发一次，触发后自动清空。</summary>
    public DateTime? SnoozeUntil { get; set; }

    /// <summary>单次提醒被标记完成的时刻；完成后不再触发，但保留在清单里可查看/删除。</summary>
    public DateTime? CompletedAt { get; set; }

    [JsonIgnore]
    public bool IsDone => CompletedAt is not null;

    [JsonIgnore]
    public bool IsSnoozed => SnoozeUntil is not null;

    [JsonIgnore]
    public string RepeatText => Repeat switch
    {
        ReminderRepeat.Once => "单次",
        ReminderRepeat.Daily => "每天",
        ReminderRepeat.Weekly => Weekdays.Count == 0
            ? "每周"
            : "每周" + string.Concat(Weekdays.OrderBy(w => w).Select(w => DateText.WeekChar(w))),
        ReminderRepeat.Monthly => $"每月 {DayOfMonth} 日",
        ReminderRepeat.Yearly => $"每年 {StartDate.Month}月{StartDate.Day}日",
        _ => "单次",
    };

    [JsonIgnore]
    public string LeadText => LeadMinutes switch
    {
        0 => "准时",
        < 60 => $"提前 {LeadMinutes} 分钟",
        < 1440 => $"提前 {LeadMinutes / 60} 小时",
        _ => $"提前 {LeadMinutes / 1440} 天",
    };
}

/// <summary>日期/时间的中文格式化小工具。</summary>
internal static class DateText
{
    private static readonly string[] WeekNames = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    internal static string WeekChar(int dow) => dow switch
    {
        0 => "日",
        1 => "一",
        2 => "二",
        3 => "三",
        4 => "四",
        5 => "五",
        6 => "六",
        _ => "?",
    };

    internal static string Week(DateOnly date) => WeekNames[(int)date.DayOfWeek];

    internal static string Md(DateOnly date) => $"{date.Month}月{date.Day}日";

    internal static string Time(TimeOnly time) => time.ToString("HH:mm");

    /// <summary>「09:00」「今天 09:00」「明天 09:00」「10月25日 09:00」。</summary>
    internal static string Friendly(DateTime when, DateTime now)
    {
        var days = (when.Date - now.Date).Days;
        return days switch
        {
            0 => $"今天 {when:HH:mm}",
            1 => $"明天 {when:HH:mm}",
            2 => $"后天 {when:HH:mm}",
            _ => $"{when.Month}月{when.Day}日 {when:HH:mm}",
        };
    }

    /// <summary>「2 小时」「15 分钟」「3 天」。</summary>
    internal static string Span(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "不到 1 分钟";
        if (span.TotalHours < 1) return $"{(int)Math.Round(span.TotalMinutes)} 分钟";
        if (span.TotalDays < 1) return $"{(int)Math.Round(span.TotalHours)} 小时";
        return $"{(int)Math.Round(span.TotalDays)} 天";
    }
}

/// <summary>提醒事项的本地存储（JSON 文件，原子写入）。</summary>
internal sealed class ReminderStore
{
    private readonly List<Reminder> _items = new();
    private readonly object _gate = new();

    internal IReadOnlyList<Reminder> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    internal Reminder? Find(string id)
    {
        lock (_gate) return _items.FirstOrDefault(r => r.Id == id);
    }

    internal void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.RemindersFile)) return;

            var json = File.ReadAllText(AppPaths.RemindersFile);
            var items = JsonSerializer.Deserialize<List<Reminder>>(json, Options);
            if (items is null) return;

            lock (_gate)
            {
                _items.Clear();
                _items.AddRange(items.Where(r => !string.IsNullOrWhiteSpace(r.Title)));
            }
        }
        catch (Exception ex)
        {
            Diag.Warn($"读取提醒失败：{ex.Message}");
        }
    }

    internal void Save(Reminder reminder)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(r => r.Id == reminder.Id);
            if (index >= 0) _items[index] = reminder;
            else _items.Add(reminder);
        }

        Persist();
    }

    internal bool Delete(string id)
    {
        var removed = false;
        lock (_gate) removed = _items.RemoveAll(r => r.Id == id) > 0;
        if (removed) Persist();
        return removed;
    }

    internal void Persist()
    {
        try
        {
            AppPaths.Ensure();

            List<Reminder> snapshot;
            lock (_gate)
            {
                snapshot = _items
                    .OrderBy(r => r.Time)
                    .ThenBy(r => r.Title, StringComparer.CurrentCulture)
                    .ToList();
            }

            var json = JsonSerializer.Serialize(snapshot, Options);
            var tmp = AppPaths.RemindersFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.RemindersFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Diag.Warn($"保存提醒失败：{ex.Message}");
        }
    }

    // ---------------- 重复规则 ----------------

    /// <summary>下一次「该提醒」的时刻：稍后提醒优先，否则按重复规则；已完成/停用返回 null。</summary>
    internal static DateTime? Next(Reminder reminder, DateTime now)
    {
        if (!reminder.Enabled) return null;
        if (reminder.CompletedAt is not null) return null;
        if (reminder.SnoozeUntil is { } snooze && snooze > now) return snooze;

        return NextOccurrence(reminder, now);
    }

    /// <summary>只按重复规则算下一次发生时刻，不考虑稍后提醒与完成状态（跳过本次时要用）。</summary>
    internal static DateTime? NextOccurrence(Reminder reminder, DateTime now)
    {
        if (!reminder.Enabled) return null;

        var anchor = reminder.StartDate.ToDateTime(TimeOnly.MinValue);
        var time = reminder.Time.ToTimeSpan();
        var today = now.Date;

        switch (reminder.Repeat)
        {
            case ReminderRepeat.Once:
            {
                var once = anchor + time;
                return once > now ? once : null;
            }

            case ReminderRepeat.Daily:
            {
                var candidate = today + time;
                return candidate > now ? candidate : candidate.AddDays(1);
            }

            case ReminderRepeat.Weekly:
            {
                if (reminder.Weekdays.Count == 0) return null;

                var set = reminder.Weekdays.ToHashSet();
                for (var i = 0; i < 15; i++)
                {
                    var day = today.AddDays(i);
                    if (day < anchor || !set.Contains((int)day.DayOfWeek)) continue;

                    var candidate = day + time;
                    if (candidate > now) return candidate;
                }

                return null;
            }

            case ReminderRepeat.Monthly:
            {
                var dayOfMonth = Math.Clamp(reminder.DayOfMonth, 1, 31);

                for (var i = 0; i < 26; i++)
                {
                    var month = new DateTime(today.Year, today.Month, 1).AddMonths(i);
                    var day = new DateTime(month.Year, month.Month,
                        Math.Min(dayOfMonth, DateTime.DaysInMonth(month.Year, month.Month)));

                    if (day < anchor) continue;

                    var candidate = day + time;
                    if (candidate > now) return candidate;
                }

                return null;
            }

            case ReminderRepeat.Yearly:
            {
                for (var i = 0; i < 8; i++)
                {
                    var year = today.Year + i;
                    var day = new DateTime(year, reminder.StartDate.Month,
                        Math.Min(reminder.StartDate.Day, DateTime.DaysInMonth(year, reminder.StartDate.Month)));

                    if (day < anchor) continue;

                    var candidate = day + time;
                    if (candidate > now) return candidate;
                }

                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>这条提醒在指定日期是否会触发（用于月历上的小圆点）。</summary>
    internal static bool OccursOn(Reminder reminder, DateOnly date)
    {
        if (!reminder.Enabled) return false;
        if (date < reminder.StartDate) return false;

        return reminder.Repeat switch
        {
            ReminderRepeat.Once => date == reminder.StartDate,
            ReminderRepeat.Daily => true,
            ReminderRepeat.Weekly => reminder.Weekdays.Contains((int)date.ToDateTime(TimeOnly.MinValue).DayOfWeek),
            ReminderRepeat.Monthly => date.Day == Math.Min(Math.Max(reminder.DayOfMonth, 1), DateTime.DaysInMonth(date.Year, date.Month)),
            ReminderRepeat.Yearly => date.Month == reminder.StartDate.Month
                                     && date.Day == Math.Min(reminder.StartDate.Day, DateTime.DaysInMonth(date.Year, date.Month)),
            _ => false,
        };
    }

    /// <summary>这条提醒在 [from, to] 区间内的所有发生时刻（含端点日），按时间升序。</summary>
    internal static IReadOnlyList<DateTime> OccurrencesBetween(Reminder reminder, DateOnly from, DateOnly to)
    {
        var list = new List<DateTime>();
        if (!reminder.Enabled || reminder.CompletedAt is not null) return list;

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (day < reminder.StartDate) continue;
            if (!OccursOn(reminder, day)) continue;

            list.Add(day.ToDateTime(reminder.Time));
        }

        return list;
    }

    /// <summary>全部提醒里，下一次最早发生的那一条。</summary>
    internal (Reminder Reminder, DateTime When)? Nearest(DateTime now)
    {
        (Reminder, DateTime)? best = null;

        foreach (var item in Items)
        {
            var next = Next(item, now);
            if (next is null) continue;
            if (best is null || next < best.Value.Item2) best = (item, next.Value);
        }

        return best;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
