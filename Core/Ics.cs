using System.Globalization;
using System.Text;

namespace IslandAlmanac.Core;

/// <summary>一条日程的具体发生（重复规则已展开）。只读数据，不参与编辑。</summary>
internal sealed record ScheduleItem(
    string Uid,
    string Title,
    string Feed,
    DateTime Start,
    DateTime End,
    bool AllDay)
{
    /// <summary>这一天是否被这条日程覆盖（跨天/全天都会命中）。</summary>
    internal bool CoversDay(DateOnly day)
    {
        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        return Start < dayEnd && End > dayStart;
    }

    /// <summary>日历格里显示的短标签。</summary>
    internal string Chip => AllDay ? Title : $"{Start:HH:mm} {Title}";
}

/// <summary>
/// 精简版 iCalendar 解析器：只为「把别人的日历读成只读日程」服务。
/// 支持 VEVENT、SUMMARY、DTSTART/DTEND（含 TZID / Z / VALUE=DATE）、
/// RRULE（DAILY / WEEKLY / MONTHLY / YEARLY + INTERVAL / BYDAY / BYMONTHDAY / COUNT / UNTIL）、EXDATE。
/// 明确不支持 BYSETPOS、BYYEARDAY、RECURRENCE-ID 覆盖 —— 遇到就跳过并留日志，不做半截实现。
/// </summary>
internal static class IcsParser
{
    private const int MaxSeriesIterations = 4000;
    private const int MaxOccurrencesPerEvent = 600;

    internal static string? ParseCalendarName(string text) =>
        Unfold(text)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("X-WR-CALNAME", StringComparison.OrdinalIgnoreCase))
            .Select(line => ContentLine.Parse(line))
            .Where(p => p is not null)
            .Select(p => Unescape(p!.Value.Value))
            .FirstOrDefault();

    /// <summary>把整份 ICS 展开成 [windowStart, windowEnd] 内的具体发生。</summary>
    internal static List<ScheduleItem> Parse(
        string text,
        string feedName,
        DateOnly windowStart,
        DateOnly windowEnd)
    {
        var result = new List<ScheduleItem>();
        var windowFrom = windowStart.ToDateTime(TimeOnly.MinValue);
        var windowTo = windowEnd.ToDateTime(TimeOnly.MinValue).AddDays(1);

        EventBuilder? current = null;

        foreach (var raw in Unfold(text))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = new EventBuilder();
                continue;
            }

            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null) Emit(current, feedName, windowFrom, windowTo, result);
                current = null;
                continue;
            }

            if (current is null) continue;

            var prop = ContentLine.Parse(line);
            if (prop is null) continue;

            current.Add(prop.Value.Name, prop.Value.Params, prop.Value.Value);
        }

        return result
            .Where(item => item.End > windowFrom && item.Start < windowTo)
            .GroupBy(item => $"{item.Uid}|{item.Start:O}|{item.Title}")
            .Select(g => g.First())
            .OrderBy(item => item.Start)
            .ToList();
    }

    // ---------------- VEVENT → 展开 ----------------

    private static void Emit(
        EventBuilder evt,
        string feedName,
        DateTime windowFrom,
        DateTime windowTo,
        List<ScheduleItem> sink)
    {
        var title = evt.Summary.Length == 0 ? "（无标题）" : evt.Summary;
        var status = evt.Status;
        if (status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) return;

        if (evt.Start is not { } startInfo) return;

        var start = startInfo.Value;
        var allDay = startInfo.AllDay;
        var end = evt.End is { } endInfo
            ? endInfo.Value
            : allDay ? start.AddDays(1) : start.AddMinutes(30);

        if (end < start) end = start;
        if (end == start) end = allDay ? start.AddDays(1) : start.AddMinutes(30);

        var exdates = new HashSet<DateTime>(evt.ExDates);

        if (evt.RRule is null)
        {
            if (!exdates.Contains(start)) sink.Add(new ScheduleItem(evt.Uid, title, feedName, start, end, allDay));
            return;
        }

        var rule = RecurrenceRule.Parse(evt.RRule);
        if (rule is null)
        {
            Diag.Warn($"日程「{title}」的重复规则暂不支持，只取首次：{evt.RRule}");
            sink.Add(new ScheduleItem(evt.Uid, title, feedName, start, end, allDay));
            return;
        }

        var duration = end - start;
        var emitted = 0;

        foreach (var occurrence in rule.Expand(start, allDay, windowFrom, windowTo))
        {
            if (exdates.Contains(occurrence)) continue;
            if (occurrence >= windowTo) break;

            var occEnd = occurrence + duration;
            if (occEnd <= windowFrom) continue;

            sink.Add(new ScheduleItem(evt.Uid, title, feedName, occurrence, occEnd, allDay));
            if (++emitted >= MaxOccurrencesPerEvent) break;
        }
    }

    // ---------------- 基础工具 ----------------

    /// <summary>拆行 + 反折叠（折行以「换行 + 空格/制表」续接）。</summary>
    private static IEnumerable<string> Unfold(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && builder.Length > 0)
            {
                builder.Append(line, 1, line.Length - 1);
                continue;
            }

            if (builder.Length > 0) yield return builder.ToString();
            builder.Clear();
            builder.Append(line);
        }

        if (builder.Length > 0) yield return builder.ToString();
    }

    private static string Unescape(string value) => value
        .Replace("\\n", "\n")
        .Replace("\\N", "\n")
        .Replace("\\,", ",")
        .Replace("\\;", ";")
        .Replace("\\\\", "\\")
        .Trim();

    private sealed class EventBuilder
    {
        internal string Uid = Guid.NewGuid().ToString("n")[..8];
        internal string Summary = string.Empty;
        internal string Status = string.Empty;
        internal string? RRule;
        internal (DateTime Value, bool AllDay)? Start;
        internal (DateTime Value, bool AllDay)? End;
        internal readonly List<DateTime> ExDates = new();

        internal void Add(string name, Dictionary<string, string> ps, string value)
        {
            switch (name.ToUpperInvariant())
            {
                case "UID":
                    Uid = value.Trim();
                    break;
                case "SUMMARY":
                    Summary = Unescape(value);
                    break;
                case "STATUS":
                    Status = value.Trim();
                    break;
                case "RRULE":
                    RRule = value.Trim();
                    break;
                case "DTSTART":
                    Start = ParseMoment(ps, value);
                    break;
                case "DTEND":
                    End = ParseMoment(ps, value);
                    break;
                case "EXDATE":
                    foreach (var piece in value.Split(','))
                    {
                        if (ParseMoment(ps, piece) is { } moment) ExDates.Add(moment.Value);
                    }

                    break;
            }
        }
    }

    /// <summary>把内容行拆成 名称 / 参数 / 值。参数值里的冒号由引号保护。</summary>
    private readonly record struct ContentLine(string Name, Dictionary<string, string> Params, string Value)
    {
        internal static ContentLine? Parse(string line)
        {
            var inQuotes = false;
            var colon = -1;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"') inQuotes = !inQuotes;
                else if (c == ':' && !inQuotes)
                {
                    colon = i;
                    break;
                }
            }

            if (colon <= 0) return null;

            var head = line[..colon];
            var value = line[(colon + 1)..];
            var pieces = head.Split(';');
            var name = pieces[0].Trim();

            var ps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < pieces.Length; i++)
            {
                var eq = pieces[i].IndexOf('=');
                if (eq <= 0) continue;
                ps[pieces[i][..eq].Trim()] = pieces[i][(eq + 1)..].Trim().Trim('"');
            }

            return new ContentLine(name, ps, value);
        }
    }

    // ---------------- 日期时间 ----------------

    private static (DateTime Value, bool AllDay)? ParseMoment(Dictionary<string, string> ps, string value)
    {
        value = value.Trim();
        if (value.Length == 0) return null;

        var isDateOnly = ps.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase)
                         || (value.Length == 8 && !value.Contains('T'));

        if (isDateOnly)
        {
            if (DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                return (DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified), true);
            }

            return null;
        }

        var utc = value.EndsWith('Z');
        var core = utc ? value[..^1] : value;

        if (!DateTime.TryParseExact(core, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
        {
            if (!DateTime.TryParseExact(core, "yyyyMMdd'T'HHmm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out local))
            {
                return null;
            }
        }

        if (utc) return (DateTime.SpecifyKind(local, DateTimeKind.Utc).ToLocalTime(), false);

        var zone = ps.TryGetValue("TZID", out var tzid) ? ResolveZone(tzid) : null;
        if (zone is null) return (local, false);

        try
        {
            var asUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
            return (asUtc.ToLocalTime(), false);
        }
        catch
        {
            return (local, false);
        }
    }

    private static TimeZoneInfo? ResolveZone(string tzid)
    {
        foreach (var candidate in new[] { tzid, tzid.Replace('/', '-') })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch { /* 换下一个写法 */ }
        }

        if (tzid.Contains("China", StringComparison.OrdinalIgnoreCase) ||
            tzid.Contains("Shanghai", StringComparison.OrdinalIgnoreCase) ||
            tzid.Contains("Beijing", StringComparison.OrdinalIgnoreCase))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
            catch { /* 落回本地时间 */ }
        }

        return null;
    }

    // ---------------- RRULE ----------------

    private sealed class RecurrenceRule
    {
        private string _freq = "DAILY";
        private int _interval = 1;
        private int _count = -1;
        private DateTime? _until;
        private List<int> _weekdays = new();
        private List<int> _monthDays = new();

        internal static RecurrenceRule? Parse(string text)
        {
            var rule = new RecurrenceRule();

            foreach (var piece in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = piece.IndexOf('=');
                if (eq <= 0) continue;

                var key = piece[..eq].Trim().ToUpperInvariant();
                var value = piece[(eq + 1)..].Trim();

                switch (key)
                {
                    case "FREQ":
                        rule._freq = value.ToUpperInvariant();
                        break;
                    case "INTERVAL":
                        if (int.TryParse(value, out var interval) && interval > 0) rule._interval = interval;
                        break;
                    case "COUNT":
                        if (int.TryParse(value, out var count) && count > 0) rule._count = count;
                        break;
                    case "UNTIL":
                        if (ParseMoment(new Dictionary<string, string>(), value) is { } until) rule._until = until.Value;
                        break;
                    case "BYDAY":
                        foreach (var day in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var name = day.Trim();
                            // 只认两位星期缩写，"2FR" 这类序号写法不支持
                            if (name.Length > 2) name = name[^2..];
                            var dow = DayOfWeekOf(name);
                            if (dow >= 0) rule._weekdays.Add(dow);
                        }

                        break;
                    case "BYMONTHDAY":
                        foreach (var day in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (int.TryParse(day.Trim(), out var n) && n is >= 1 and <= 31) rule._monthDays.Add(n);
                        }

                        break;
                    case "BYSETPOS":
                    case "BYYEARDAY":
                    case "BYWEEKNO":
                        return null;   // 明确不支持，交给调用方降级
                }
            }

            return rule._freq switch
            {
                "DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY" => rule,
                _ => null,
            };
        }

        private static int DayOfWeekOf(string name) => name.ToUpperInvariant() switch
        {
            "MO" => 1,
            "TU" => 2,
            "WE" => 3,
            "TH" => 4,
            "FR" => 5,
            "SA" => 6,
            "SU" => 0,
            _ => -1,
        };

        internal IEnumerable<DateTime> Expand(DateTime first, bool allDay, DateTime windowFrom, DateTime windowTo)
        {
            var produced = 0;

            // DTSTART 不一定落在规则上（很多日历会有偏差），先对齐到第一个真正匹配的日期
            var cursor = AlignFirst(first, allDay);

            // 无 COUNT 时可以直接跳到窗口附近，避免从 2010 年一天天算过来
            if (_count < 0)
            {
                var skip = PeriodsToSkip(cursor, windowFrom);
                cursor = Advance(cursor, skip);
            }

            for (var guard = 0; guard < MaxSeriesIterations; guard++)
            {
                if (cursor >= windowTo) yield break;
                if (_until is { } until && cursor > until) yield break;

                produced++;
                if (_count > 0 && produced > _count) yield break;

                yield return cursor;

                var next = NextAfter(cursor, first, allDay);
                if (next <= cursor) yield break;
                cursor = next;
            }
        }

        /// <summary>把起点对齐到规则真正匹配的第一天（DTSTART 落在规则之外时用）。</summary>
        private DateTime AlignFirst(DateTime first, bool allDay)
        {
            switch (_freq)
            {
                case "WEEKLY" when _weekdays.Count > 0:
                {
                    for (var offset = 0; offset < 8; offset++)
                    {
                        var candidate = first.AddDays(offset);
                        if (_weekdays.Contains((int)candidate.DayOfWeek)) return candidate;
                    }

                    return first;
                }

                case "MONTHLY" when _monthDays.Count > 0 && !_monthDays.Contains(first.Day):
                {
                    var month = new DateTime(first.Year, first.Month, 1);
                    for (var step = 1; step <= 13; step++)
                    {
                        var probe = month.AddMonths(step);
                        foreach (var day in _monthDays.OrderBy(d => d))
                        {
                            if (day > DateTime.DaysInMonth(probe.Year, probe.Month)) continue;

                            var candidate = new DateTime(probe.Year, probe.Month, day);
                            if (!allDay) candidate = candidate + first.TimeOfDay;
                            return candidate;
                        }
                    }

                    return first;
                }

                default:
                    return first;
            }
        }

        private int PeriodsToSkip(DateTime first, DateTime windowFrom)
        {
            if (windowFrom <= first) return 0;

            var days = (windowFrom - first).TotalDays;
            var periods = _freq switch
            {
                "DAILY" => (int)(days / _interval),
                "WEEKLY" => (int)(days / 7 / _interval),
                "MONTHLY" => (int)(days / 28 / _interval),
                "YEARLY" => (int)(days / 365 / _interval),
                _ => 0,
            };

            return Math.Max(0, periods - 1);
        }

        private DateTime Advance(DateTime from, int periods)
        {
            if (periods <= 0) return from;

            return _freq switch
            {
                "DAILY" => from.AddDays(periods * _interval),
                "WEEKLY" => from.AddDays(periods * 7 * _interval),
                "MONTHLY" => from.AddMonths(periods * _interval),
                "YEARLY" => from.AddYears(periods * _interval),
                _ => from,
            };
        }

        private DateTime NextAfter(DateTime current, DateTime first, bool allDay)
        {
            switch (_freq)
            {
                case "DAILY":
                    return current.AddDays(_interval);

                case "WEEKLY":
                {
                    var set = _weekdays.Count > 0 ? _weekdays : new List<int> { (int)first.DayOfWeek };

                    for (var step = 1; step <= 8 * _interval; step++)
                    {
                        var candidate = current.AddDays(step);
                        if (!set.Contains((int)candidate.DayOfWeek)) continue;
                        if (candidate < first) continue;
                        return candidate;
                    }

                    return current.AddDays(7 * _interval);
                }

                case "MONTHLY":
                {
                    var month = new DateTime(current.Year, current.Month, 1).AddMonths(_interval);
                    var days = _monthDays.Count > 0 ? _monthDays : new List<int> { first.Day };

                    var candidates = days
                        .Select(day => new DateTime(month.Year, month.Month,
                            Math.Min(day, DateTime.DaysInMonth(month.Year, month.Month))))
                        .Select(day => allDay ? day.Date : day + current.TimeOfDay)
                        .Where(day => day > current)
                        .OrderBy(day => day)
                        .ToList();

                    return candidates.Count > 0 ? candidates[0] : month.AddMonths(_interval);
                }

                case "YEARLY":
                {
                    var next = current.AddYears(_interval);
                    return _monthDays.Count > 0
                        ? new DateTime(next.Year, next.Month,
                            Math.Min(_monthDays[0], DateTime.DaysInMonth(next.Year, next.Month))) + current.TimeOfDay
                        : next;
                }

                default:
                    return current.AddDays(1);
            }
        }
    }
}
