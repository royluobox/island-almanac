using IslandAlmanac.Core;
using IslandAlmanac.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace IslandAlmanac;

/// <summary>
/// 岛历（island-almanac）：小岛显示今天 + 下一个节假日/提醒倒数，展开看农历与提醒，点击打开完整月历。
/// </summary>
public sealed class IslandAlmanacPlugin : IslandPluginBase, IAlmanacService
{
    private static readonly Windows.Foundation.Size CompactSize = new(182, 38);
    private static readonly Windows.Foundation.Size ExpandedSize = new(482, 186);
    private static readonly Windows.Foundation.Size SpotlightSize = new(820, 600);

    private readonly HolidayService _holidays = new();
    private readonly ReminderStore _reminders = new();
    private readonly ScheduleService _schedule = new();
    private PluginState _state = new();

    private IslandView _view = null!;
    private IslandLiveContent _content = null!;
    private CalendarSpotlightView? _spotlight;
    private DateOnly _lastDay;
    private DateTime _nextHolidayRefresh = DateTime.MinValue;

    /// <summary>实时提醒窗口：到点后这么多分钟内仍算「准时」。</summary>
    private static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(6);

    /// <summary>补提醒最多回溯这么久，避免翻出陈年旧账。</summary>
    private static readonly TimeSpan MaxCatchUp = TimeSpan.FromHours(24);

    /// <summary>心跳写盘节流。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    private DateTime _lastTickAt;
    private DateTime _lastHeartbeat = DateTime.MinValue;
    private DateTime _nextScheduleRefresh = DateTime.MinValue;
    private bool _stopped;

    private event EventHandler? DataChangedEvent;

    // ---------------- 生命周期 ----------------

    protected override Task OnInitializeAsync()
    {
        Diag.InfoSink = message => Log.Info(message);
        Diag.WarnSink = message => Log.Warn(message);
        Diag.ErrorSink = (message, ex) => Log.Error(message, ex!);

        _stopped = false;
        _lastDay = DateOnly.FromDateTime(DateTime.Today);

        // 节假日：先读缓存（更新），再用内置数据补齐缺失年份
        _holidays.LoadEmbedded();
        _holidays.LoadCache();
        _reminders.Load();
        _schedule.Load();

        Log.Info($"启动：{Manifest.Id} {Manifest.Version}；节假日来源 {_holidays.SourceLabel}；提醒 {_reminders.Items.Count} 条");

        _view = new IslandView(Manifest, Theme);
        _view.Render(BuildSnapshot());

        _content = new IslandLiveContent
        {
            Priority = 45,
            OwnerLabel = Manifest.Name,
            OwnerGlyph = Manifest.IconGlyph,
            OwnerAccent = Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C),
            MorphView = _view,
            CompactSize = CompactSize,
            ExpandedSize = ExpandedSize,
            OnTap = OpenSpotlight,
        };

        SetContent(Settings.Get("enabled", true) ? _content : null);

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE787", BuildSettingsPage, order: 60));

        foreach (var key in new[] { "enabled", "showLunar", "showTerm", "showHoliday", "showReminder", "showSchedule" })
        {
            Context.Register(Context.OnSettingsChanged(key, OnSettingsChanged));
        }

        Context.CreateTimer(TimeSpan.FromSeconds(15), repeat: true, OnTick);

        // 上一次退出到现在这段时间里到点却没响的提醒，启动时补报一次
        _state = PluginState.Load();
        var bootAt = DateTime.Now;
        if (_state.LastAlive is { } awayFrom)
        {
            CatchUpMissed(awayFrom, bootAt, "启动");
        }

        _lastTickAt = bootAt;
        SaveHeartbeat(bootAt, force: true);

        // 宿主可能活不到第一个 15 秒 tick（本机会被全屏窗口抢置顶后崩），
        // 所以启动时立刻检查一次到点提醒与稍后提醒。
        CheckReminderDue();

        // 节假日数据：启动时按需自动更新一次，之后定时器每 6 小时检查一次
        TryAutoRefreshHolidays("启动");
        TryAutoRefreshSchedule("启动");

        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        _stopped = true;
        SaveHeartbeat(DateTime.Now, force: true);
        SetContent(null);
        Log.Info("已停用");
        return Task.CompletedTask;
    }

    /// <summary>记录心跳，供下次启动时计算「宿主缺席了多久」。</summary>
    private void SaveHeartbeat(DateTime now, bool force = false)
    {
        if (!force && now - _lastHeartbeat < HeartbeatInterval) return;
        _lastHeartbeat = now;
        _state.Save(now);
    }

    /// <summary>缓存超过 24 小时（或根本没有）才去联网。</summary>
    private bool NeedsHolidayRefresh()
    {
        if (_holidays.UpdatedAt is null) return true;
        return (DateTime.Now - _holidays.UpdatedAt.Value).TotalHours >= 24;
    }

    private void ScheduleNextHolidayRefresh() => _nextHolidayRefresh = DateTime.Now.AddHours(6);

    /// <summary>
    /// 自动更新节假日。只写 holidays-cache.json，绝不碰 reminders.json —— 日程提醒不会被覆盖。
    /// 每 6 小时最多尝试一次；失败（例如官方还没公布明年安排）不会重试到刷屏。
    /// </summary>
    private void TryAutoRefreshHolidays(string reason)
    {
        if (DateTime.Now < _nextHolidayRefresh) return;
        ScheduleNextHolidayRefresh();

        if (!NeedsHolidayRefresh()) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var message = await _holidays.RefreshAsync(DateTime.Today.Year, CancellationToken.None);
                Log.Info($"自动更新节假日（{reason}）：{message}；提醒数据未改动（{_reminders.Items.Count} 条）");
                RunOnUI(() =>
                {
                    RaiseDataChanged();
                    RefreshIsland("节假日数据更新");
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"自动更新节假日失败（{reason}）：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 只读日程的空闲刷新：每 6 小时最多一次，缓存超过 6 小时才真的联网。
    /// 失败的源保留上次缓存，不会把已有日程清空。
    /// </summary>
    private void TryAutoRefreshSchedule(string reason)
    {
        if (DateTime.Now < _nextScheduleRefresh) return;
        _nextScheduleRefresh = DateTime.Now.AddHours(6);

        if (!_schedule.NeedsRefresh()) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var message = await _schedule.RefreshAsync(CancellationToken.None);
                Log.Info($"刷新日程（{reason}）：{message}");
                RunOnUI(() =>
                {
                    RaiseDataChanged();
                    RefreshIsland("日程数据更新");
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"刷新日程失败（{reason}）：{ex.Message}");
            }
        });
    }

    // ---------------- 定时 ----------------
    private void OnTick()
    {
        if (_stopped) return;

        try
        {
            var now = DateTime.Now;

            // 定时器两次 tick 之间隔了超过 3 分钟 → 线程被挂起过（睡眠/休眠/宿主卡住），
            // 这段时间的提醒不会自己补，主动扫一遍。
            if (_lastTickAt != default && now - _lastTickAt > TimeSpan.FromMinutes(3))
            {
                CatchUpMissed(_lastTickAt, now, "唤醒");
            }

            _lastTickAt = now;
            SaveHeartbeat(now);

            var today = DateOnly.FromDateTime(now);
            if (today != _lastDay)
            {
                _lastDay = today;
                Log.Info("跨日，重算小岛内容");
            }

            TryAutoRefreshHolidays("定时检查");
            TryAutoRefreshSchedule("定时检查");
            CheckReminderDue();
            RefreshIsland("定时刷新");
        }
        catch (Exception ex)
        {
            Diag.Warn($"定时器异常：{ex.Message}");
        }
    }

    private void CheckReminderDue()
    {
        var now = DateTime.Now;
        var fired = false;

        // 1) 稍后提醒到点：优先于正常周期，响一次后清空
        foreach (var reminder in _reminders.Items)
        {
            if (reminder.SnoozeUntil is not { } until || until > now) continue;

            reminder.SnoozeUntil = null;
            _reminders.Save(reminder);
            fired = true;
            Log.Info($"稍后提醒到点：{reminder.Title}（原定 {until:HH:mm}）");
            Toast("稍后提醒", $"{Shorten(reminder.Title, 12)} · {now:HH:mm}", "\uEA8F");
        }

        // 2) 正常周期：只认「到点后 LiveWindow 内」，再早的交给补提
        foreach (var due in CollectDue(now - LiveWindow, now))
        {
            fired = true;
            var lead = due.Lead > 0 ? $"（提前 {due.Lead} 分钟）" : string.Empty;
            Toast("提醒", $"{Shorten(due.Title, 12)} · {due.When:HH:mm}{lead}", "\uEA8F");
        }

        if (fired)
        {
            RaiseDataChanged();
            RefreshIsland("提醒触发");
        }
    }

    /// <summary>
    /// 把 [from, to] 里到点却没响过的提醒一次性补报出来。
    /// 用于启动、系统休眠唤醒、宿主被顶掉后恢复 —— 这些时段定时器没走，正常触发会漏。
    /// 每条补报后立刻写 LastFired，所以同一次不会被补两遍。
    /// </summary>
    private void CatchUpMissed(DateTime from, DateTime to, string reason)
    {
        if (to <= from) return;
        if (to - from > MaxCatchUp) from = to - MaxCatchUp;

        var missed = CollectDue(from, to);
        if (missed.Count == 0) return;

        var first = missed[0];
        var text = missed.Count == 1
            ? $"{first.When:MM-dd HH:mm} {Shorten(first.Title, 12)}"
            : $"{missed.Count} 条，最近：{first.When:MM-dd HH:mm} {Shorten(first.Title, 12)}";

        Toast(missed.Count == 1 ? "错过提醒" : $"错过 {missed.Count} 条提醒", text, "\uE7BA");
        Log.Info($"补提醒（{reason}）{missed.Count} 条：" +
                 string.Join("；", missed.Take(6).Select(m => $"{m.When:MM-dd HH:mm} {m.Title}")));

        RaiseDataChanged();
        RefreshIsland("补提醒");
    }

    /// <summary>取 [from, to] 内所有该响而未响的发生时刻，并立刻标记为已提醒。</summary>
    private List<DueItem> CollectDue(DateTime from, DateTime to)
    {
        var result = new List<DueItem>();

        foreach (var reminder in _reminders.Items)
        {
            var span = ReminderStore.OccurrencesBetween(
                reminder,
                DateOnly.FromDateTime(from).AddDays(-2),
                DateOnly.FromDateTime(to).AddDays(2));

            foreach (var occurrence in span)
            {
                if (reminder.LastFired == occurrence) continue;

                var fireAt = occurrence.AddMinutes(-reminder.LeadMinutes);
                if (fireAt > to || fireAt < from) continue;

                reminder.LastFired = occurrence;
                reminder.SnoozeUntil = null;
                _reminders.Save(reminder);
                result.Add(new DueItem(reminder.Title, occurrence, reminder.LeadMinutes));
            }
        }

        return result.OrderBy(item => item.When).ToList();
    }

    private readonly record struct DueItem(string Title, DateTime When, int Lead);

    // ---------------- 小岛内容 ----------------

    private void RefreshIsland(string reason)
    {
        if (_stopped) return;

        var snapshot = BuildSnapshot();
        _view.Render(snapshot);
    }

    private void OnSettingsChanged()
    {
        SetContent(Settings.Get("enabled", true) ? _content : null);
        RefreshIsland("设置变更");
        RaiseDataChanged();
    }

    private IslandSnapshot BuildSnapshot()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        var showLunar = Settings.Get("showLunar", true);
        var showTerm = Settings.Get("showTerm", true);
        var showHoliday = Settings.Get("showHoliday", true);
        var showReminder = Settings.Get("showReminder", true);
        var showSchedule = Settings.Get("showSchedule", true);

        // 紧凑态：只有日期，绝不溢出
        var dateLine = $"{today.Month}月{today.Day}日 {DateText.Week(today)}";

        // 右上块：今天过节就报今天，否则报下一个
        var outlook = HolidaySummary.Next(_holidays, today);
        var festival = LunarInfo.Festival(today) ?? LunarInfo.SolarFestival(today);
        string topTitle;
        string topDetail;

        if (!showHoliday)
        {
            var lunar = LunarInfo.Describe(today);
            topTitle = lunar.Length > 0 ? $"🌙 农历{lunar}" : LunarInfo.FestivalEmoji(festival);
            topDetail = LunarInfo.GanzhiYear(today);
        }
        else if (festival is not null)
        {
            topTitle = $"{LunarInfo.FestivalEmoji(festival)} 今天 {festival}";
            var extra = outlook is not null && outlook.Ongoing
                ? $" · 放假第 {today.DayNumber - outlook.Date.DayNumber + 1} 天 / 共 {outlook.OffDays} 天"
                : string.Empty;
            topDetail = $"农历{LunarInfo.Describe(today)}{extra}";
        }
        else if (outlook is not null)
        {
            var provisional = outlook.Provisional ? " · 放假安排待公布" : $" · 放假 {outlook.OffDays} 天";
            topTitle = $"{LunarInfo.FestivalEmoji(outlook.Name)} 下一个节日 {outlook.Name}";
            topDetail = $"{outlook.Date.Month}月{outlook.Date.Day}日 {DateText.Week(outlook.Date)} · 还有 {outlook.DaysUntil} 天{provisional}";
        }
        else
        {
            topTitle = "🎊 暂无节假日数据";
            topDetail = "设置页可手动更新";
        }

        // 右下块：日程与提醒里更近的那一件；都没有才回落到节气
        var nearest = _reminders.Nearest(now);
        var nextEvent = showSchedule ? _schedule.NextAfter(now) : null;

        var useReminder = showReminder && nearest.HasValue &&
                          (nextEvent is null || nearest.Value.When <= nextEvent.Value.When);
        var useEvent = nextEvent is not null && !useReminder;

        string bottomTitle;
        string bottomDetail;

        if (useReminder)
        {
            var next = nearest!.Value;
            bottomTitle = $"⏰ {Shorten(next.Reminder.Title, 10)}";
            bottomDetail = $"{DateText.Friendly(next.When, now)} · {DateText.Span(next.When - now)}后 · {next.Reminder.RepeatText}";
        }
        else if (useEvent)
        {
            var evt = nextEvent!.Value;
            var item = evt.Item;
            bottomTitle = $"📅 {Shorten(item.Title, 10)}";
            bottomDetail = DescribeEvent(item, now);
        }
        else if (showTerm && SolarTerms.TryGetCurrent(today, out var termStart, out var termEnd, out var termIndex))
        {
            bottomTitle = $"{SolarTerms.EmojiOf(termIndex)} {SolarTerms.Names[termIndex]} {termStart.Month}.{termStart.Day}–{termEnd.Month}.{termEnd.Day}";
            bottomDetail = SolarTerms.Intro[termIndex];
        }
        else
        {
            var lunar = LunarInfo.Describe(today);
            bottomTitle = lunar.Length > 0 ? $"🌙 农历{lunar}" : "🌙 农历";
            bottomDetail = LunarInfo.GanzhiYear(today);
        }

        return new IslandSnapshot(
            dateLine,
            $"{today.Year}年{today.Month}月",
            BuildMonth(today),
            topTitle, topDetail,
            bottomTitle, bottomDetail);
    }

    /// <summary>当月迷你月历：周一起，42 格（含上下月补位）。</summary>
    private IReadOnlyList<MiniDay> BuildMonth(DateOnly today)
    {
        var first = new DateOnly(today.Year, today.Month, 1);
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var start = first.AddDays(-offset);
        var cells = new List<MiniDay>(42);

        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            var holiday = _holidays.Get(date);

            cells.Add(new MiniDay(
                date.Day.ToString(),
                date.Month == today.Month,
                date == today,
                holiday?.IsOff == true,
                holiday is { IsOff: false }));
        }

        return cells;
    }

    /// <summary>日程的一句话描述：全天 / 进行中 / 还有多久。</summary>
    private static string DescribeEvent(ScheduleItem item, DateTime now)
    {
        var timing = item.AllDay
            ? item.Start.Date == now.Date ? "全天" : $"{DateText.Friendly(item.Start, now)} 全天"
            : item.Start <= now
                ? $"进行中 · {item.End:HH:mm} 结束"
                : $"{DateText.Friendly(item.Start, now)} · {DateText.Span(item.Start - now)}后";

        return $"{timing} · {item.Feed}";
    }

    private static string Shorten(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "提醒";
        text = text.Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private void OpenSpotlight()
    {
        if (_stopped) return;

        _spotlight ??= new CalendarSpotlightView(this, Theme);

        Context.Island.OpenSpotlight(new IslandSpotlight
        {
            Content = _spotlight,
            Size = SpotlightSize,
            OnClosed = () => _spotlight?.OnHostClosed(),
        });
    }

    // ---------------- IAlmanacService ----------------

    PluginManifest IAlmanacService.Manifest => Manifest;

    IIslandTheme IAlmanacService.Theme => Theme;

    HolidayService IAlmanacService.Holidays => _holidays;

    ScheduleService IAlmanacService.Schedule => _schedule;

    IReadOnlyList<Reminder> IAlmanacService.Reminders => _reminders.Items;

    bool IAlmanacService.ShowLunar
    {
        get => Settings.Get("showLunar", true);
        set => Settings.Set("showLunar", value);
    }

    bool IAlmanacService.ShowTerm
    {
        get => Settings.Get("showTerm", true);
        set => Settings.Set("showTerm", value);
    }

    Reminder? IAlmanacService.FindReminder(string id) => _reminders.Find(id);

    void IAlmanacService.SaveReminder(Reminder reminder)
    {
        _reminders.Save(reminder);
        Log.Info($"保存提醒：{reminder.Title}（{reminder.RepeatText} {reminder.Time:HH:mm}）");
        Toast("已保存", $"{reminder.Title} · {reminder.RepeatText}", "\uE73E");
        RaiseDataChanged();
        RefreshIsland("提醒变更");
    }

    void IAlmanacService.DeleteReminder(string id)
    {
        if (_reminders.Delete(id))
        {
            Log.Info($"删除提醒：{id}");
            Toast("已删除", "这条提醒不会再触发", "\uE74D");
            RaiseDataChanged();
            RefreshIsland("提醒变更");
        }
    }

    void IAlmanacService.SnoozeReminder(string id, TimeSpan duration)
    {
        var reminder = _reminders.Find(id);
        if (reminder is null) return;

        reminder.SnoozeUntil = DateTime.Now + duration;
        _reminders.Save(reminder);
        Log.Info($"稍后提醒：{reminder.Title} → {reminder.SnoozeUntil:HH:mm}");
        Toast("已稍后", $"{Shorten(reminder.Title, 10)} · {reminder.SnoozeUntil:HH:mm} 再提醒", "\uE823");
        RaiseDataChanged();
        RefreshIsland("稍后提醒");
    }

    void IAlmanacService.CompleteReminder(string id)
    {
        var reminder = _reminders.Find(id);
        if (reminder is null) return;

        if (reminder.Repeat == ReminderRepeat.Once)
        {
            reminder.CompletedAt = DateTime.Now;
            reminder.SnoozeUntil = null;
            _reminders.Save(reminder);
            Log.Info($"完成提醒：{reminder.Title}");
            Toast("已完成", Shorten(reminder.Title, 12), "\uE73E");
        }
        else
        {
            // 重复提醒：只跳过本次，把即将到来的那次标成已提醒
            var upcoming = ReminderStore.NextOccurrence(reminder, DateTime.Now);
            reminder.LastFired = upcoming;
            reminder.SnoozeUntil = null;
            _reminders.Save(reminder);
            Log.Info($"跳过本次：{reminder.Title}（跳过 {upcoming:MM-dd HH:mm}）");
            Toast("已跳过本次", $"{Shorten(reminder.Title, 10)} · 下个周期照常", "\uE7BA");
        }

        RaiseDataChanged();
        RefreshIsland("提醒完成");
    }

    event EventHandler? IAlmanacService.DataChanged
    {
        add => DataChangedEvent += value;
        remove => DataChangedEvent -= value;
    }

    void IAlmanacService.Toast(string title, string text, string glyph) => Toast(title, text, glyph);

    void IAlmanacService.CloseSpotlight() => Context.Island.CloseSpotlight();

    void IAlmanacService.RequestRefresh() => RefreshIsland("视图请求");

    private void RaiseDataChanged()
    {
        try
        {
            DataChangedEvent?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Diag.Warn($"通知视图失败：{ex.Message}");
        }
    }

    private void Toast(string title, string text, string glyph)
    {
        try
        {
            Context.Island.ShowMessage(new IslandMessage
            {
                Title = title,
                Text = text,
                Glyph = glyph,
                AccentColor = Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C),
                Duration = TimeSpan.FromSeconds(4),
            });
        }
        catch (Exception ex)
        {
            Diag.Warn($"弹消息失败：{ex.Message}");
        }
    }

    // ---------------- 设置页 ----------------

    private UIElement BuildSettingsPage()
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = Manifest.Name,
            Style = ResourceStyle("TitleTextBlockStyle"),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "小岛常驻只显示今天的日期；鼠标悬停展开迷你月历、下一个节日与节气（有提醒时换成提醒）；点一下岛体打开完整月历，可增删提醒。",
            Style = ResourceStyle("BodyTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
        });

        var enabled = new ToggleSwitch { Header = "在灵动岛上显示", IsOn = Settings.Get("enabled", true) };
        enabled.Toggled += (_, _) => Settings.Set("enabled", enabled.IsOn);

        var showLunar = new ToggleSwitch { Header = "展开态/月历显示农历", IsOn = Settings.Get("showLunar", true) };
        showLunar.Toggled += (_, _) => Settings.Set("showLunar", showLunar.IsOn);

        var showTerm = new ToggleSwitch { Header = "展开态/月历显示节气（立冬、霜降…）", IsOn = Settings.Get("showTerm", true) };
        showTerm.Toggled += (_, _) => Settings.Set("showTerm", showTerm.IsOn);

        var showHoliday = new ToggleSwitch { Header = "展开态显示下一个节日", IsOn = Settings.Get("showHoliday", true) };
        showHoliday.Toggled += (_, _) => Settings.Set("showHoliday", showHoliday.IsOn);

        var showReminder = new ToggleSwitch { Header = "展开态显示最近提醒", IsOn = Settings.Get("showReminder", true) };
        showReminder.Toggled += (_, _) => Settings.Set("showReminder", showReminder.IsOn);

        var showSchedule = new ToggleSwitch { Header = "展开态显示下一条日程", IsOn = Settings.Get("showSchedule", true) };
        showSchedule.Toggled += (_, _) => Settings.Set("showSchedule", showSchedule.IsOn);

        panel.Children.Add(Card(new StackPanel
        {
            Spacing = 2,
            Children = { enabled, showLunar, showTerm, showHoliday, showReminder, showSchedule },
        }));

        // 数据状态（只读：更新全自动，不提供手动按钮）
        var sourceText = new TextBlock
        {
            Text = $"节假日数据：{_holidays.SourceLabel}",
            Style = ResourceStyle("BodyTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
        };

        var autoText = new TextBlock
        {
            Text = "更新方式：启动时检查，之后每 6 小时一次；缓存超过 24 小时才联网。断网或官方未公布时自动沿用内置数据，日程提醒独立存放，永不被覆盖。",
            Style = ResourceStyle("CaptionTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
        };

        var calendar = new Button { Content = "打开日历" };
        calendar.Click += (_, _) => OpenSpotlight();

        panel.Children.Add(Card(new StackPanel { Spacing = 6, Children = { sourceText, autoText, calendar } }));

        // ---------------- 日程订阅（只读桥） ----------------
        var feedRows = new StackPanel { Spacing = 4 };

        var feedStatus = new TextBlock
        {
            Style = ResourceStyle("CaptionTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
            Text = _schedule.StatusLabel,
        };

        void RenderFeeds()
        {
            feedRows.Children.Clear();
            var feeds = _schedule.Feeds;

            if (feeds.Count == 0)
            {
                feedRows.Children.Add(new TextBlock
                {
                    Text = "还没有订阅。把 Outlook / Google / 腾讯日历的 ICS 链接粘到下面即可。",
                    Style = ResourceStyle("CaptionTextBlockStyle"),
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            foreach (var feed in feeds)
            {
                var name = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(feed.Name) ? feed.Url : feed.Name,
                    FontSize = 12.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };

                var detail = new TextBlock
                {
                    Text = feed.Error is { } error
                        ? $"拉取失败：{error}"
                        : feed.FetchedAt is { } at
                            ? $"{feed.Count} 条 · {at:MM-dd HH:mm} 拉取"
                            : "尚未拉取",
                    Style = ResourceStyle("CaptionTextBlockStyle"),
                    TextWrapping = TextWrapping.Wrap,
                };

                var texts = new StackPanel { Spacing = 0 };
                texts.Children.Add(name);
                texts.Children.Add(detail);

                var remove = new Button
                {
                    Content = "✕",
                    FontSize = 10,
                    Padding = new Thickness(8, 2, 8, 2),
                    MinWidth = 0,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                remove.Click += (_, _) =>
                {
                    _schedule.RemoveFeed(feed.Id);
                    feedStatus.Text = "已取消订阅";
                    RenderFeeds();
                    RaiseDataChanged();
                    RefreshIsland("取消订阅");
                };

                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(texts, 0);
                Grid.SetColumn(remove, 1);
                row.Children.Add(texts);
                row.Children.Add(remove);
                feedRows.Children.Add(row);
            }
        }

        RenderFeeds();

        var feedBox = new TextBox
        {
            PlaceholderText = "粘贴 ICS 链接（只读，不会改动原日历）",
            FontSize = 12.5,
        };

        var addFeed = new Button { Content = "添加" };
        addFeed.Click += async (_, _) =>
        {
            var (ok, message) = _schedule.AddFeed(feedBox.Text);
            feedStatus.Text = message;
            if (!ok) return;

            feedBox.Text = string.Empty;
            RenderFeeds();
            addFeed.IsEnabled = false;
            feedStatus.Text = "正在拉取…";
            feedStatus.Text = await _schedule.RefreshAsync(CancellationToken.None);
            addFeed.IsEnabled = true;
            RenderFeeds();
            RaiseDataChanged();
            RefreshIsland("订阅日程");
        };

        var refreshFeeds = new Button
        {
            Content = "立即刷新",
            IsEnabled = _schedule.HasFeeds,
        };
        refreshFeeds.Click += async (_, _) =>
        {
            refreshFeeds.IsEnabled = false;
            feedStatus.Text = "正在刷新…";
            feedStatus.Text = await _schedule.RefreshAsync(CancellationToken.None);
            refreshFeeds.IsEnabled = true;
            RenderFeeds();
            RaiseDataChanged();
            RefreshIsland("刷新日程");
        };

        var feedActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        feedActions.Children.Add(addFeed);
        feedActions.Children.Add(refreshFeeds);

        panel.Children.Add(Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "日程订阅（只读）",
                    FontSize = 14,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                },
                new TextBlock
                {
                    Text = "把 Windows 日历／Outlook／Google 日历发布的 ICS 链接放进来，岛上就能显示「下一条日程」。只读：这里不会修改原日历，也不会为订阅事件弹提醒（避免和 Outlook 通知撞车）。",
                    Style = ResourceStyle("CaptionTextBlockStyle"),
                    TextWrapping = TextWrapping.Wrap,
                },
                feedRows,
                feedBox,
                feedActions,
                feedStatus,
            },
        }));

        panel.Children.Add(new TextBlock
        {
            Text = "开源数据源：holiday-cn（据国务院办公厅放假公告生成）。断网时使用插件内置的数据，仍可正常显示。",
            Style = ResourceStyle("CaptionTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
        });

        return panel;
    }

    private static Style? ResourceStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    private static Border Card(UIElement child) => new()
    {
        Style = ResourceStyle("SettingsCardStyle"),
        Child = child,
        Padding = new Thickness(14, 10, 14, 10),
    };
}
