using System.Globalization;

namespace IslandAlmanac.Core;

/// <summary>
/// 二十四节气（日期粒度）。数据来自离线表 <see cref="SolarTermsData"/>，1901-2100 年全覆盖，运行期零网络。
/// </summary>
internal static class SolarTerms
{
    internal static readonly string[] Names =
    {
        "小寒", "大寒", "立春", "雨水", "惊蛰", "春分", "清明", "谷雨",
        "立夏", "小满", "芒种", "夏至", "小暑", "大暑", "立秋", "处暑",
        "白露", "秋分", "寒露", "霜降", "立冬", "小雪", "大雪", "冬至",
    };

    /// <summary>每个节气配一个 Emoji 小图标（用 Segoe UI Emoji 渲染，深浅色主题下都清晰）。</summary>
    internal static readonly string[] Emoji =
    {
        "🥶", "❄️", "🌱", "🌧️", "🐛", "🌸", "🌿", "🌦️",
        "☀️", "🌾", "🌾", "🌞", "🥵", "🔥", "🍂", "🌤️",
        "💧", "🌕", "🍁", "🌫️", "🧣", "🌨️", "⛄", "🥟",
    };

    /// <summary>每个节气一句短介绍（展开态用一行说明）。</summary>
    internal static readonly string[] Intro =
    {
        "天渐寒，尚未大冷",
        "一年最冷，寒气之逆极",
        "东风解冻，万物起始",
        "冰雪融化，降水渐多",
        "春雷始鸣，蛰虫惊醒",
        "昼夜均分，燕子归来",
        "气清景明，草木繁茂",
        "雨生百谷，播种时节",
        "万物生长，暑气渐起",
        "麦粒渐满，未至大满",
        "有芒之谷可种，最忙时节",
        "日最长，阴气始生",
        "暑气渐盛，尚未极热",
        "一年最热，湿热交蒸",
        "凉风至，暑去秋来",
        "暑气至此而止",
        "露凝而白，秋意渐浓",
        "昼夜再均分，秋色平分",
        "露气寒冷，将凝结也",
        "初霜出现，秋末冬始",
        "水始冰，万物收藏",
        "寒未深，雪未大",
        "雪盛之时，天寒地冻",
        "日最短，一阳始生",
    };

    private const int TermCount = 24;

    /// <summary>今天是不是某个节气；是则给出名字与序号。</summary>
    internal static bool TryGet(DateOnly date, out string name, out int index)
    {
        name = string.Empty;
        index = -1;

        var rows = SolarTermsData.Rows;
        var i = date.Year - SolarTermsData.FirstYear;
        if (i < 0 || i >= rows.Length) return false;

        var row = rows[i];
        for (var k = 0; k < TermCount; k++)
        {
            var m = (row[k * 4] - '0') * 10 + (row[k * 4 + 1] - '0');
            var d = (row[k * 4 + 2] - '0') * 10 + (row[k * 4 + 3] - '0');
            if (m == date.Month && d == date.Day)
            {
                name = Names[k];
                index = k;
                return true;
            }
        }

        return false;
    }

    internal static string EmojiOf(int index) =>
        index >= 0 && index < Emoji.Length ? Emoji[index] : "📅";

    /// <summary>今天或之后的第一个节气（含今天）。</summary>
    internal static DateOnly? Next(DateOnly from, out string name, out int index)
    {
        for (var i = 1; i <= 32; i++)
        {
            var d = from.AddDays(i);
            if (TryGet(d, out name, out index)) return d;
        }

        if (TryGet(from, out name, out index)) return from;
        name = string.Empty;
        index = -1;
        return null;
    }

    /// <summary>
    /// 今天所处的节气区间：start = 节气当天，end = 下一个节气的前一天。
    /// </summary>
    internal static bool TryGetCurrent(DateOnly date, out DateOnly start, out DateOnly end, out int index)
    {
        start = date;
        end = date;
        index = -1;

        for (var back = 0; back <= 40; back++)
        {
            var d = date.AddDays(-back);
            if (!TryGet(d, out _, out var idx)) continue;

            start = d;
            index = idx;

            for (var forward = 1; forward <= 20; forward++)
            {
                var n = d.AddDays(forward);
                if (TryGet(n, out _, out _))
                {
                    end = n.AddDays(-1);
                    return true;
                }
            }

            end = d.AddDays(15);
            return true;
        }

        return false;
    }
}

/// <summary>农历（依赖 .NET 内置的 <see cref="ChineseLunisolarCalendar"/>，1901-2100 年）。</summary>
internal static class LunarInfo
{
    private static readonly ChineseLunisolarCalendar Cal = new();

    private static readonly string[] MonthNames =
    {
        "正月", "二月", "三月", "四月", "五月", "六月",
        "七月", "八月", "九月", "十月", "冬月", "腊月",
    };

    private static readonly string[] DayNames =
    {
        "",
        "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十",
    };

    private static readonly string[] Stems = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };
    private static readonly string[] Branches = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };
    private static readonly string[] Zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

    /// <summary>农历节日（月, 日）。除夕单独判定。</summary>
    private static readonly Dictionary<(int Month, int Day), string> Festivals = new()
    {
        [(1, 1)] = "春节",
        [(1, 15)] = "元宵",
        [(2, 2)] = "龙抬头",
        [(5, 5)] = "端午",
        [(7, 7)] = "七夕",
        [(7, 15)] = "中元",
        [(8, 15)] = "中秋",
        [(9, 9)] = "重阳",
        [(12, 8)] = "腊八",
        [(12, 23)] = "小年",
    };

    /// <summary>公历节日（不含法定放假日，那些由节假日数据提供）。</summary>
    private static readonly Dictionary<(int Month, int Day), string> SolarFestivals = new()
    {
        [(1, 1)] = "元旦",
        [(2, 14)] = "情人节",
        [(3, 8)] = "妇女节",
        [(3, 12)] = "植树节",
        [(5, 1)] = "劳动节",
        [(5, 4)] = "青年节",
        [(6, 1)] = "儿童节",
        [(7, 1)] = "建党节",
        [(8, 1)] = "建军节",
        [(9, 10)] = "教师节",
        [(10, 1)] = "国庆节",
        [(12, 24)] = "平安夜",
        [(12, 25)] = "圣诞节",
    };

    internal static bool TryGetLunar(DateOnly date, out int month, out int day, out bool isLeap, out int ganzhiYear)
    {
        month = day = ganzhiYear = 0;
        isLeap = false;

        try
        {
            var dt = date.ToDateTime(TimeOnly.MinValue);
            if (dt < Cal.MinSupportedDateTime || dt > Cal.MaxSupportedDateTime) return false;

            var lunarYear = Cal.GetYear(dt);
            var rawMonth = Cal.GetMonth(dt);
            var leapMonth = Cal.GetLeapMonth(lunarYear);

            isLeap = leapMonth > 0 && rawMonth == leapMonth;
            month = leapMonth > 0 && rawMonth >= leapMonth ? rawMonth - 1 : rawMonth;
            day = Cal.GetDayOfMonth(dt);
            ganzhiYear = Cal.GetSexagenaryYear(dt);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>「九月初九」「闰六月初一」这种纯农历日。</summary>
    internal static string Describe(DateOnly date)
    {
        if (!TryGetLunar(date, out var m, out var d, out var leap, out _)) return string.Empty;
        if (m < 1 || m > 12 || d < 1 || d > 30) return string.Empty;
        return (leap ? "闰" : string.Empty) + MonthNames[m - 1] + DayNames[d];
    }

    /// <summary>农历节日名（含除夕）。</summary>
    internal static string? Festival(DateOnly date)
    {
        if (!TryGetLunar(date, out var m, out var d, out var leap, out _)) return null;
        if (!leap && Festivals.TryGetValue((m, d), out var name)) return name;

        // 除夕 = 明天是正月初一
        if (TryGetLunar(date.AddDays(1), out var nm, out var nd, out _, out _) && nm == 1 && nd == 1)
        {
            return "除夕";
        }

        return null;
    }

    internal static string? SolarFestival(DateOnly date) =>
        SolarFestivals.TryGetValue((date.Month, date.Day), out var name) ? name : null;

    /// <summary>「丙午马年」。</summary>
    internal static string GanzhiYear(DateOnly date)
    {
        if (!TryGetLunar(date, out _, out _, out _, out var gz)) return string.Empty;
        var stem = Cal.GetCelestialStem(gz) - 1;
        var branch = Cal.GetTerrestrialBranch(gz) - 1;
        if (stem < 0 || stem > 9 || branch < 0 || branch > 11) return string.Empty;
        return Stems[stem] + Branches[branch] + Zodiacs[branch] + "年";
    }

    /// <summary>
    /// 日历格子里那一行小字：节气 &gt; 农历节日 &gt; 公历节日 &gt; 农历日。
    /// </summary>
    internal static string CellLabel(DateOnly date, bool showLunar, bool showTerm)
    {
        if (showTerm && SolarTerms.TryGet(date, out var term, out _)) return term;

        var festival = Festival(date);
        if (festival is not null) return festival;

        var solar = SolarFestival(date);
        if (solar is not null) return solar;

        return showLunar ? Describe(date) : string.Empty;
    }

    /// <summary>节日 Emoji。</summary>
    internal static string FestivalEmoji(string? name) => name switch
    {
        "春节" => "🧧",
        "元宵" => "🏮",
        "端午" => "🍙",
        "七夕" => "🌉",
        "中元" => "🕯️",
        "中秋" => "🥮",
        "重阳" => "🌼",
        "腊八" => "🥣",
        "除夕" => "🧨",
        "小年" => "🍬",
        "龙抬头" => "🐉",
        "元旦" => "🎊",
        "劳动节" => "🛠️",
        "国庆节" => "🎉",
        "儿童节" => "🎈",
        "情人节" => "💐",
        "圣诞节" => "🎄",
        "平安夜" => "🍎",
        _ => "📌",
    };
}
