namespace IslandAlmanac.Core;

/// <summary>
/// 法定节日的「日历日期」推算（元旦/春节/清明/劳动节/端午/中秋/国庆），用于官方放假安排尚未公布时的兜底。
/// 只用来算"哪一天过节"，不臆造调休。
/// </summary>
internal static class StatutoryFestivals
{
    private static readonly Dictionary<int, List<(string Name, DateOnly Date)>> Cache = new();
    private static readonly object Gate = new();

    private static readonly (int Month, int Day, string Name)[] LunarFixed =
    {
        (1, 1, "春节"),
        (5, 5, "端午"),
        (8, 15, "中秋"),
    };

    private static List<(string Name, DateOnly Date)> Build(int year)
    {
        var list = new List<(string, DateOnly)>();

        for (var d = new DateOnly(year, 1, 1); d.Year == year; d = d.AddDays(1))
        {
            if (d.Month == 1 && d.Day == 1) list.Add(("元旦", d));
            if (d.Month == 5 && d.Day == 1) list.Add(("劳动节", d));
            if (d.Month == 10 && d.Day == 1) list.Add(("国庆节", d));

            if (SolarTerms.TryGet(d, out var term, out _) && term == "清明") list.Add(("清明节", d));

            if (!LunarInfo.TryGetLunar(d, out var month, out var day, out var leap, out _) || leap) continue;

            foreach (var (m, dd, name) in LunarFixed)
            {
                if (month == m && day == dd) list.Add((name, d));
            }
        }

        return list.OrderBy(t => t.Item2).ToList();
    }

    private static List<(string Name, DateOnly Date)> For(int year)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(year, out var cached)) return cached;

            var built = Build(year);
            Cache[year] = built;
            return built;
        }
    }

    /// <summary>今天或之后的下一个法定节日（跨年）。</summary>
    internal static (string Name, DateOnly Date)? Next(DateOnly today)
    {
        foreach (var year in new[] { today.Year, today.Year + 1 })
        {
            foreach (var item in For(year))
            {
                if (item.Date >= today) return item;
            }
        }

        return null;
    }

    internal static (string Name, DateOnly Date)? On(DateOnly date)
    {
        foreach (var item in For(date.Year))
        {
            if (item.Date == date) return item;
        }

        return null;
    }
}

/// <summary>把「官方放假块」和「节日日期兜底」统一成一份可展示的结论。</summary>
internal sealed record HolidayOutlook(
    string Name,
    DateOnly Date,
    DateOnly End,
    int DaysUntil,
    int OffDays,
    bool Ongoing,
    bool Provisional);

internal static class HolidaySummary
{
    internal static HolidayOutlook? Next(HolidayService holidays, DateOnly today)
    {
        var block = holidays.NextBlock(today);
        if (block is not null)
        {
            var ongoing = block.Contains(today);
            return new HolidayOutlook(block.Name, block.Start, block.End, block.DaysUntil(today), block.OffDays, ongoing, false);
        }

        var festival = StatutoryFestivals.Next(today);
        if (festival is null) return null;

        var days = Math.Max(0, festival.Value.Date.DayNumber - today.DayNumber);
        return new HolidayOutlook(festival.Value.Name, festival.Value.Date, festival.Value.Date, days, 1, false, true);
    }
}
