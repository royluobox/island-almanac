using WinIsland.Core;

namespace IslandAlmanac.Core;

/// <summary>展开态迷你月历里的一格。</summary>
internal sealed record MiniDay(
    string Day,
    bool InMonth,
    bool IsToday,
    bool IsOff,
    bool IsMakeup);

/// <summary>小岛一次渲染所需的全部文案，由插件算好后交给视图。</summary>
internal sealed record IslandSnapshot(
    string DateLine,
    string MonthLabel,
    IReadOnlyList<MiniDay> Month,
    string TopTitle,
    string TopDetail,
    string BottomTitle,
    string BottomDetail);

internal enum CountdownKind
{
    None = 0,
    Holiday = 1,
    Reminder = 2,
}

/// <summary>
/// 视图与插件之间的契约。视图只做展示与编辑，数据读写全部回给插件，
/// 保证"编辑完立刻反映到小岛"这条链路只有一个出口。
/// </summary>
internal interface IAlmanacService
{
    PluginManifest Manifest { get; }

    IIslandTheme Theme { get; }

    HolidayService Holidays { get; }

    /// <summary>只读日程（来自订阅的 ICS），视图只读不写。</summary>
    ScheduleService Schedule { get; }

    IReadOnlyList<Reminder> Reminders { get; }

    /// <summary>日历里是否显示农历小字（设置项）。</summary>
    bool ShowLunar { get; set; }

    /// <summary>日历里是否显示节气（设置项）。</summary>
    bool ShowTerm { get; set; }

    Reminder? FindReminder(string id);

    void SaveReminder(Reminder reminder);

    void DeleteReminder(string id);

    /// <summary>稍后提醒：到点优先触发一次，之后回到原周期。</summary>
    void SnoozeReminder(string id, TimeSpan duration);

    /// <summary>
    /// 完成/跳过：单次提醒标记完成（不再触发）；重复提醒只跳过本次，下个周期照常。
    /// </summary>
    void CompleteReminder(string id);

    event EventHandler? DataChanged;

    void Toast(string title, string text, string glyph);

    void CloseSpotlight();

    /// <summary>日历/提醒改动后，让小岛立刻重算一次。</summary>
    void RequestRefresh();
}
