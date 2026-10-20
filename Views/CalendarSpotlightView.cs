using IslandAlmanac.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace IslandAlmanac.Views;

/// <summary>
/// 展开后的完整日历（聚光卡）：左边月历（格子底部有小圆点表示当天有提醒），
/// 右边是提醒清单 + 一个编辑器。卡片尺寸、飞入动画、遮罩全由宿主负责。
/// </summary>
internal sealed class CalendarSpotlightView : UserControl
{
    private const int CellCount = 42;

    private static readonly (string Label, int Minutes)[] LeadOptions =
    {
        ("准时", 0),
        ("提前 5 分钟", 5),
        ("提前 15 分钟", 15),
        ("提前 30 分钟", 30),
        ("提前 1 小时", 60),
        ("提前 1 天", 1440),
    };

    private readonly IAlmanacService _service;
    private readonly IIslandTheme _theme;

    // 主题画刷（共享，改 Color 即全树生效）
    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _accentBrush = new();
    private readonly SolidColorBrush _dangerBrush = new();
    private readonly SolidColorBrush _okBrush = new();
    private readonly SolidColorBrush _cardBrush = new();
    private readonly SolidColorBrush _selectionBrush = new();
    private readonly SolidColorBrush _borderBrush = new();
    private readonly SolidColorBrush _offBandBrush = new();
    private readonly SolidColorBrush _chipBrush = new();
    private readonly SolidColorBrush _chipPastBrush = new();
    private readonly SolidColorBrush _eventBrush = new();

    private readonly TextBlock _monthTitle;
    private readonly TextBlock _monthSub;
    private readonly TextBlock _footerText;
    private readonly TextBlock _statusText;
    private readonly StackPanel _reminderList;
    private readonly TextBlock _reminderCount;
    private readonly Border _todayStrip;
    private readonly TextBlock _todayText;

    private readonly TextBlock _editorTitle;
    private readonly TextBox _titleBox;
    private readonly TextBox _dateBox;
    private readonly TextBox _timeBox;
    private readonly TextBox _dayOfMonthBox;
    private readonly StackPanel _weekdayRow;
    private readonly StackPanel _dayOfMonthRow;
    private readonly ComboBox _leadBox;
    private readonly Button _primaryButton;
    private readonly Button _cancelButton;
    private readonly StackPanel _actionRow;
    private readonly Button _doneButton;
    private readonly Dictionary<ReminderRepeat, ToggleButton> _repeatButtons = new();
    private readonly List<ToggleButton> _weekdayButtons = new();

    private readonly DayCell[] _cells = new DayCell[CellCount];

    private DateOnly _cursor;
    private DateOnly _selected;
    private Reminder? _editing;
    private int _leadMinutes = 15;
    private bool _loading = true;

    internal CalendarSpotlightView(IAlmanacService service, IIslandTheme theme)
    {
        _service = service;
        _theme = theme;

        _selected = DateOnly.FromDateTime(DateTime.Today);
        _cursor = new DateOnly(_selected.Year, _selected.Month, 1);

        // ---------------- 头部 ----------------
        _monthTitle = new TextBlock
        {
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _monthSub = new TextBlock
        {
            FontSize = 12,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 2, 0, 0),
        };

        var prevButton = NavButton("‹");
        prevButton.Click += (_, _) => ShiftMonth(-1);

        var nextButton = NavButton("›");
        nextButton.Click += (_, _) => ShiftMonth(1);

        var todayButton = SmallButton("今天");
        todayButton.Click += (_, _) =>
        {
            _selected = DateOnly.FromDateTime(DateTime.Today);
            _cursor = new DateOnly(_selected.Year, _selected.Month, 1);
            if (_editing is null && _dateBox is not null) _dateBox.Text = _selected.ToString("yyyy-MM-dd");
            RenderAll();
        };

        var lunarToggle = new ToggleButton
        {
            Content = "农历",
            FontSize = 12,
            IsChecked = _service.ShowLunar,
            Padding = new Thickness(10, 4, 10, 4),
        };
        lunarToggle.Click += (_, _) =>
        {
            _service.ShowLunar = lunarToggle.IsChecked == true;
            RenderAll();
        };

        var termToggle = new ToggleButton
        {
            Content = "节气",
            FontSize = 12,
            IsChecked = _service.ShowTerm,
            Padding = new Thickness(10, 4, 10, 4),
        };
        termToggle.Click += (_, _) =>
        {
            _service.ShowTerm = termToggle.IsChecked == true;
            RenderAll();
            _service.RequestRefresh();
        };

        var headerLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerLeft.Children.Add(prevButton);
        headerLeft.Children.Add(_monthTitle);
        headerLeft.Children.Add(nextButton);
        headerLeft.Children.Add(_monthSub);
        headerLeft.Children.Add(todayButton);

        var headerRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerRight.Children.Add(lunarToggle);
        headerRight.Children.Add(termToggle);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(headerLeft, 0);
        Grid.SetColumn(headerRight, 1);
        header.Children.Add(headerLeft);
        header.Children.Add(headerRight);

        // ---------------- 月历 ----------------
        var weekHeader = new Grid { Margin = new Thickness(0, 10, 0, 4) };
        for (var i = 0; i < 7; i++)
        {
            weekHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock
            {
                Text = i == 6 ? "日" : DateText.WeekChar(i + 1),
                FontSize = 11.5,
                Foreground = _faintBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Grid.SetColumn(label, i);
            weekHeader.Children.Add(label);
        }

        var grid = new Grid { RowSpacing = 3, ColumnSpacing = 3 };
        for (var i = 0; i < 7; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var r = 0; r < 6; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        for (var i = 0; i < CellCount; i++)
        {
            var cell = BuildCell();
            _cells[i] = cell;
            Grid.SetRow(cell.Host, i / 7);
            Grid.SetColumn(cell.Host, i % 7);
            grid.Children.Add(cell.Host);
        }

        var calendarArea = new Grid();
        calendarArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        calendarArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(weekHeader, 0);
        Grid.SetRow(grid, 1);
        calendarArea.Children.Add(weekHeader);
        calendarArea.Children.Add(grid);

        // ---------------- 右侧：清单 ----------------
        _reminderCount = new TextBlock
        {
            FontSize = 11.5,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        _reminderList = new StackPanel { Spacing = 6 };

        var addButton = new Button
        {
            Content = "＋",
            FontSize = 13,
            Padding = new Thickness(0, 0, 0, 2),
            Width = 30,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        addButton.Click += (_, _) => BeginNew();

        var listTitle = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        listTitle.Children.Add(new TextBlock
        {
            Text = "提醒",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
        });
        listTitle.Children.Add(_reminderCount);

        var listHeader = new Grid();
        listHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        listHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(listTitle, 0);
        Grid.SetColumn(addButton, 1);
        listHeader.Children.Add(listTitle);
        listHeader.Children.Add(addButton);

        var listScroller = new ScrollViewer
        {
            Content = _reminderList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 4, 4, 4),
        };

        // 今日日程：只读一行，来自订阅的日历；今天没有日程时整条隐藏
        _todayText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = _mutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };

        _todayStrip = new Border
        {
            Background = _cardBrush,
            BorderBrush = _borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 5, 9, 5),
            Visibility = Visibility.Collapsed,
            Child = _todayText,
        };

        // ---------------- 右侧：编辑器 ----------------
        _editorTitle = new TextBlock
        {
            Text = "新建提醒",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _cancelButton = new Button
        {
            Content = "取消",
            FontSize = 11.5,
            Padding = new Thickness(8, 3, 8, 3),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Visibility = Visibility.Collapsed,
        };
        _cancelButton.Click += (_, _) => BeginNew();

        var editorHeader = new Grid();
        editorHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editorHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_editorTitle, 0);
        Grid.SetColumn(_cancelButton, 1);
        editorHeader.Children.Add(_editorTitle);
        editorHeader.Children.Add(_cancelButton);

        _titleBox = new TextBox { PlaceholderText = "例如：交周报", FontSize = 12.5 };
        _titleBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) Commit();
        };

        _dateBox = new TextBox { Text = _selected.ToString("yyyy-MM-dd"), FontSize = 12.5, Width = 108 };
        _timeBox = new TextBox { Text = "09:00", FontSize = 12.5, Width = 64 };
        _dayOfMonthBox = new TextBox { Text = _selected.Day.ToString(), FontSize = 12.5, Width = 56 };

        _weekdayRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };
        for (var i = 1; i <= 7; i++)
        {
            var dow = i % 7;
            var button = new ToggleButton
            {
                Content = DateText.WeekChar(dow),
                FontSize = 11.5,
                MinWidth = 29,
                Padding = new Thickness(2, 2, 2, 2),
            };
            button.Tag = dow;
            _weekdayButtons.Add(button);
            _weekdayRow.Children.Add(button);
        }

        _dayOfMonthRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Visibility = Visibility.Collapsed };
        _dayOfMonthRow.Children.Add(new TextBlock
        {
            Text = "每月",
            FontSize = 12,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _dayOfMonthRow.Children.Add(_dayOfMonthBox);
        _dayOfMonthRow.Children.Add(new TextBlock
        {
            Text = "日",
            FontSize = 12,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var repeatRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (kind, label) in new[]
                 {
                     (ReminderRepeat.Once, "单次"),
                     (ReminderRepeat.Daily, "每天"),
                     (ReminderRepeat.Weekly, "每周"),
                     (ReminderRepeat.Monthly, "每月"),
                     (ReminderRepeat.Yearly, "每年"),
                 })
        {
            var button = new ToggleButton
            {
                Content = label,
                FontSize = 11.5,
                Padding = new Thickness(8, 2, 8, 2),
            };
            button.Click += (_, _) => SelectRepeat(kind);
            _repeatButtons[kind] = button;
            repeatRow.Children.Add(button);
        }

        _leadBox = new ComboBox { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var option in LeadOptions) _leadBox.Items.Add(option.Label);
        _leadBox.SelectedIndex = 2;
        _leadBox.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var index = _leadBox.SelectedIndex;
            if (index >= 0 && index < LeadOptions.Length) _leadMinutes = LeadOptions[index].Minutes;
        };

        _primaryButton = new Button
        {
            Content = "添加",
            FontSize = 12,
            Background = _accentBrush,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            Padding = new Thickness(20, 5, 20, 5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _primaryButton.Click += (_, _) => Commit();

        _statusText = new TextBlock { FontSize = 11, Foreground = _dangerBrush, TextWrapping = TextWrapping.Wrap };

        // 编辑已有提醒时才出现的快捷操作：稍后 / 完成（单次）或跳过本次（重复）
        var snooze10 = QuietButton("稍后 10 分");
        snooze10.Click += (_, _) => SnoozeEditing(TimeSpan.FromMinutes(10));

        var snooze60 = QuietButton("稍后 1 小时");
        snooze60.Click += (_, _) => SnoozeEditing(TimeSpan.FromHours(1));

        _doneButton = QuietButton("完成");
        _doneButton.Click += (_, _) => CompleteEditing();

        _actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Visibility = Visibility.Collapsed,
        };
        _actionRow.Children.Add(snooze10);
        _actionRow.Children.Add(snooze60);
        _actionRow.Children.Add(_doneButton);

        var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        dateRow.Children.Add(_dateBox);
        dateRow.Children.Add(_timeBox);

        var editor = new StackPanel { Spacing = 5 };
        editor.Children.Add(editorHeader);
        editor.Children.Add(_titleBox);
        editor.Children.Add(FieldLabel("重复"));
        editor.Children.Add(repeatRow);
        editor.Children.Add(_weekdayRow);
        editor.Children.Add(_dayOfMonthRow);
        editor.Children.Add(FieldLabel("日期 · 时间"));
        editor.Children.Add(dateRow);
        editor.Children.Add(FieldLabel("提醒时机"));
        editor.Children.Add(_leadBox);
        editor.Children.Add(_primaryButton);
        editor.Children.Add(_actionRow);
        editor.Children.Add(_statusText);

        var editorHost = new Border
        {
            Background = _cardBrush,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 8, 10, 10),
            Child = editor,
        };

        var side = new Grid { RowSpacing = 8 };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_todayStrip, 0);
        Grid.SetRow(listHeader, 1);
        Grid.SetRow(listScroller, 2);
        Grid.SetRow(editorHost, 3);
        side.Children.Add(_todayStrip);
        side.Children.Add(listHeader);
        side.Children.Add(listScroller);
        side.Children.Add(editorHost);

        // ---------------- 组合 ----------------
        var body = new Grid { ColumnSpacing = 14, Margin = new Thickness(0, 8, 0, 6) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
        Grid.SetColumn(calendarArea, 0);
        Grid.SetColumn(side, 1);
        body.Children.Add(calendarArea);
        body.Children.Add(side);

        _footerText = new TextBlock { FontSize = 12, Foreground = _mutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };

        var root = new Grid { Padding = new Thickness(20, 16, 20, 12), RowSpacing = 4 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(_footerText, 2);
        root.Children.Add(header);
        root.Children.Add(body);
        root.Children.Add(_footerText);

        Content = root;

        ApplyTheme();
        _theme.Changed += ApplyTheme;

        Loaded += (_, _) =>
        {
            _service.DataChanged -= OnDataChanged;
            _service.DataChanged += OnDataChanged;
            BeginNew();
            RenderAll();
        };

        Unloaded += (_, _) => _service.DataChanged -= OnDataChanged;

        _loading = false;
    }

    /// <summary>宿主收起卡片时调用。</summary>
    internal void OnHostClosed() => _service.DataChanged -= OnDataChanged;

    private void OnDataChanged(object? sender, EventArgs e) => RenderAll();

    // ---------------- 小工具 ----------------

    private static Button NavButton(string glyph) => new()
    {
        Content = glyph,
        FontSize = 14,
        Padding = new Thickness(10, 2, 10, 4),
        MinWidth = 32,
    };

    private static Button SmallButton(string text) => new()
    {
        Content = text,
        FontSize = 12,
        Padding = new Thickness(10, 4, 10, 4),
        Margin = new Thickness(6, 0, 0, 0),
    };

    private static Button QuietButton(string text) => new()
    {
        Content = text,
        FontSize = 11.5,
        Padding = new Thickness(9, 3, 9, 3),
        MinWidth = 0,
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
        BorderThickness = new Thickness(1),
    };

    private TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        Foreground = _faintBrush,
        Margin = new Thickness(0, 2, 0, 0),
    };

    private sealed class DayCell
    {
        internal required Border Host { get; init; }
        internal required TextBlock Day { get; init; }
        internal required TextBlock Sub { get; init; }
        internal required TextBlock Badge { get; init; }
        internal required StackPanel Chips { get; init; }
        internal DateOnly Date { get; set; }
    }

    private DayCell BuildCell()
    {
        var day = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var sub = new TextBlock
        {
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var badge = new TextBlock
        {
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 5, 0),
            Visibility = Visibility.Collapsed,
        };

        var chips = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(0, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var stack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        stack.Children.Add(day);
        stack.Children.Add(sub);
        stack.Children.Add(chips);

        var layer = new Grid();
        layer.Children.Add(stack);
        layer.Children.Add(badge);

        var host = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(3, 4, 3, 4),
            Child = layer,
        };

        var cell = new DayCell { Host = host, Day = day, Sub = sub, Badge = badge, Chips = chips };
        host.Tapped += (_, _) => SelectDate(cell.Date);
        return cell;
    }

    // ---------------- 交互 ----------------

    private void SelectDate(DateOnly date)
    {
        _selected = date;
        _dateBox.Text = date.ToString("yyyy-MM-dd");
        if (_editing is { Repeat: ReminderRepeat.Monthly }) _dayOfMonthBox.Text = date.Day.ToString();
        RenderAll();
    }

    private void ShiftMonth(int delta)
    {
        _cursor = _cursor.AddMonths(delta);
        RenderAll();
    }

    private void SelectRepeat(ReminderRepeat kind)
    {
        foreach (var (key, button) in _repeatButtons) button.IsChecked = key == kind;
        _weekdayRow.Visibility = kind == ReminderRepeat.Weekly ? Visibility.Visible : Visibility.Collapsed;
        _dayOfMonthRow.Visibility = kind == ReminderRepeat.Monthly ? Visibility.Visible : Visibility.Collapsed;
    }

    private ReminderRepeat CurrentRepeat()
    {
        foreach (var (key, button) in _repeatButtons)
        {
            if (button.IsChecked == true) return key;
        }

        return ReminderRepeat.Once;
    }

    /// <summary>回到「新建」模式：清单里没有任何一项处于选中态。</summary>
    private void BeginNew()
    {
        _editing = null;
        _editorTitle.Text = "新建提醒";
        _primaryButton.Content = "添加";
        _cancelButton.Visibility = Visibility.Collapsed;
        _actionRow.Visibility = Visibility.Collapsed;

        _titleBox.Text = string.Empty;
        _dateBox.Text = _selected.ToString("yyyy-MM-dd");
        _timeBox.Text = "09:00";
        _dayOfMonthBox.Text = _selected.Day.ToString();
        foreach (var button in _weekdayButtons) button.IsChecked = false;
        SelectRepeat(ReminderRepeat.Once);
        SetLead(15);
        _statusText.Text = string.Empty;

        RenderAll();
    }

    private void LoadEditor(Reminder reminder)
    {
        _editing = reminder;
        _editorTitle.Text = "编辑提醒";
        _primaryButton.Content = "保存修改";
        _cancelButton.Visibility = Visibility.Visible;
        _actionRow.Visibility = Visibility.Visible;
        _doneButton.Content = reminder.Repeat == ReminderRepeat.Once ? "完成" : "跳过本次";

        _titleBox.Text = reminder.Title;
        _dateBox.Text = reminder.StartDate.ToString("yyyy-MM-dd");
        _timeBox.Text = reminder.Time.ToString("HH:mm");
        _dayOfMonthBox.Text = reminder.DayOfMonth.ToString();
        foreach (var button in _weekdayButtons) button.IsChecked = reminder.Weekdays.Contains((int)button.Tag!);
        SelectRepeat(reminder.Repeat);
        SetLead(reminder.LeadMinutes);
        _statusText.Text = string.Empty;

        RenderAll();
    }

    private void SetLead(int minutes)
    {
        _leadMinutes = minutes;
        var index = Array.FindIndex(LeadOptions, o => o.Minutes == minutes);
        _leadBox.SelectedIndex = index >= 0 ? index : 0;
    }

    private void Commit()
    {
        var title = (_titleBox.Text ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            _statusText.Foreground = _dangerBrush;
            _statusText.Text = "先写一句提醒内容";
            return;
        }

        if (!DateOnly.TryParse(_dateBox.Text, out var date))
        {
            _statusText.Foreground = _dangerBrush;
            _statusText.Text = "日期格式应为 2026-10-19";
            return;
        }

        if (!TimeOnly.TryParse(_timeBox.Text, out var time))
        {
            _statusText.Foreground = _dangerBrush;
            _statusText.Text = "时间格式应为 09:00";
            return;
        }

        var repeat = CurrentRepeat();
        var weekdays = _weekdayButtons
            .Where(b => b.IsChecked == true)
            .Select(b => (int)b.Tag!)
            .OrderBy(v => v)
            .ToList();

        if (repeat == ReminderRepeat.Weekly && weekdays.Count == 0)
        {
            _statusText.Foreground = _dangerBrush;
            _statusText.Text = "每周重复至少要选一天";
            return;
        }

        int.TryParse(_dayOfMonthBox.Text, out var dayOfMonth);
        if (repeat == ReminderRepeat.Monthly && (dayOfMonth < 1 || dayOfMonth > 31))
        {
            _statusText.Foreground = _dangerBrush;
            _statusText.Text = "「每月几号」要在 1-31 之间";
            return;
        }

        var isNew = _editing is null;
        var reminder = _editing ?? new Reminder();
        reminder.Title = title;
        reminder.Repeat = repeat;
        reminder.StartDate = date;
        reminder.Time = time;
        reminder.Weekdays = weekdays;
        reminder.DayOfMonth = dayOfMonth is >= 1 and <= 31 ? dayOfMonth : date.Day;
        reminder.LeadMinutes = _leadMinutes;
        reminder.Enabled = true;

        _service.SaveReminder(reminder);

        _editing = reminder;
        _editorTitle.Text = "编辑提醒";
        _primaryButton.Content = "保存修改";
        _cancelButton.Visibility = Visibility.Visible;
        _actionRow.Visibility = Visibility.Visible;
        _doneButton.Content = repeat == ReminderRepeat.Once ? "完成" : "跳过本次";

        _statusText.Foreground = _okBrush;
        _statusText.Text = isNew ? "已添加" : "已保存";

        RenderAll();
    }

    private void DeleteReminder(Reminder reminder)
    {
        _service.DeleteReminder(reminder.Id);
        if (_editing?.Id == reminder.Id) BeginNew();
        else RenderAll();
    }

    private void SnoozeEditing(TimeSpan span)
    {
        if (_editing is not { } reminder) return;

        _service.SnoozeReminder(reminder.Id, span);
        _statusText.Foreground = _okBrush;
        _statusText.Text = $"已稍后到 {DateTime.Now + span:HH:mm}";
        RenderAll();
    }

    private void CompleteEditing()
    {
        if (_editing is not { } reminder) return;

        var label = reminder.Repeat == ReminderRepeat.Once ? "已完成" : "已跳过本次";
        _service.CompleteReminder(reminder.Id);
        _statusText.Foreground = _okBrush;
        _statusText.Text = label;
        RenderAll();
    }

    // ---------------- 渲染 ----------------

    private void RenderAll()
    {
        try
        {
            RenderCalendar();
            RenderTodayStrip();
            RenderReminderList();
            RenderFooter();
        }
        catch (Exception ex)
        {
            Diag.Warn($"日历渲染失败：{ex.Message}");
        }
    }

    private void RenderCalendar()
    {
        _monthTitle.Text = $"{_cursor.Year} 年 {_cursor.Month} 月";
        _monthSub.Text = LunarInfo.GanzhiYear(_cursor);

        var lunarOn = _service.ShowLunar;
        var termOn = _service.ShowTerm;

        var first = _cursor;
        var offset = ((int)first.DayOfWeek + 6) % 7;   // 周一为一周之始
        var start = first.AddDays(-offset);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var reminders = _service.Reminders;

        for (var i = 0; i < CellCount; i++)
        {
            var cell = _cells[i];
            var date = start.AddDays(i);
            cell.Date = date;

            cell.Day.Text = date.Day.ToString();
            cell.Sub.Text = LunarInfo.CellLabel(date, lunarOn, termOn);

            var isCurrentMonth = date.Month == _cursor.Month;
            var isToday = date == today;
            var isSelected = date == _selected;

            var holiday = _service.Holidays.Get(date);
            var isOff = holiday?.IsOff == true;
            var isMakeup = holiday is { IsOff: false };

            cell.Badge.Visibility = isOff || isMakeup ? Visibility.Visible : Visibility.Collapsed;
            cell.Badge.Text = isOff ? "休" : "班";
            cell.Badge.Foreground = isOff ? _dangerBrush : _okBrush;

            // 格子里写当天的条目：提醒（蓝，可编辑）+ 日程（紫，只读），按时间混排，最多两行
            cell.Chips.Children.Clear();
            if (isCurrentMonth)
            {
                var entries = new List<(TimeSpan Order, string Text, bool IsEvent, bool Past)>();

                foreach (var reminder in reminders.Where(r => !r.IsDone && ReminderStore.OccursOn(r, date)))
                {
                    entries.Add((reminder.Time.ToTimeSpan(), reminder.Title, false,
                        date == today && reminder.Time.ToTimeSpan() < DateTime.Now.TimeOfDay));
                }

                foreach (var item in _service.Schedule.OnDay(date))
                {
                    entries.Add((item.AllDay ? TimeSpan.Zero : item.Start.TimeOfDay, item.Chip, true, false));
                }

                var ordered = entries.OrderBy(e => e.Order).ThenBy(e => e.Text, StringComparer.CurrentCulture).ToList();

                for (var k = 0; k < Math.Min(2, ordered.Count); k++)
                {
                    var entry = ordered[k];
                    cell.Chips.Children.Add(new TextBlock
                    {
                        Text = entry.Text,
                        FontSize = 9.5,
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextAlignment = TextAlignment.Center,
                        Foreground = entry.IsEvent ? _eventBrush : entry.Past ? _chipPastBrush : _chipBrush,
                        Opacity = entry.Past ? 0.75 : 1,
                    });
                }

                if (ordered.Count > 2)
                {
                    cell.Chips.Children.Add(new TextBlock
                    {
                        Text = $"+{ordered.Count - 2}",
                        FontSize = 8.5,
                        TextAlignment = TextAlignment.Center,
                        Foreground = _faintBrush,
                    });
                }
            }

            var dayColor = isOff
                ? _dangerBrush.Color
                : isMakeup
                    ? _okBrush.Color
                    : _textBrush.Color;

            cell.Day.Foreground = new SolidColorBrush(dayColor);
            cell.Day.Opacity = isCurrentMonth ? 1 : 0.38;
            cell.Day.FontWeight = isToday || isSelected ? FontWeights.Bold : FontWeights.SemiBold;

            cell.Sub.Foreground = isOff ? _dangerBrush : isToday ? _accentBrush : _mutedBrush;
            cell.Sub.Opacity = isCurrentMonth ? 1 : 0.35;

            // 放假整段铺一层淡红底色（调休日只保留「班」小标记，不铺色）
            cell.Host.Background = isSelected
                ? _selectionBrush
                : isOff && isCurrentMonth
                    ? _offBandBrush
                    : null;
            cell.Host.BorderBrush = isToday ? _accentBrush : isSelected ? _borderBrush : null;
        }
    }

    /// <summary>右侧顶上的一行「今天还有什么」：只读，来自订阅日历；没有就整条隐藏。</summary>
    private void RenderTodayStrip()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var items = _service.Schedule.OnDay(today);

        if (items.Count == 0)
        {
            _todayStrip.Visibility = Visibility.Collapsed;
            return;
        }

        var now = DateTime.Now;
        var head = $"今天 {items.Count} 件";
        var preview = string.Join(" · ", items.Take(2).Select(item => item.Chip));

        _todayText.Text = $"📅 {head}：{preview}" + (items.Count > 2 ? $" · +{items.Count - 2}" : string.Empty);
        _todayText.Foreground = items.Any(item => !item.AllDay && item.Start >= now) ? _mutedBrush : _faintBrush;
        _todayStrip.Visibility = Visibility.Visible;
    }

    private void RenderReminderList()
    {
        _reminderList.Children.Clear();

        var now = DateTime.Now;
        var items = _service.Reminders
            .Select(r => (Reminder: r, Next: ReminderStore.Next(r, now)))
            .OrderBy(t => t.Reminder.IsDone)                       // 已完成的沉到底部
            .ThenBy(t => t.Next ?? DateTime.MaxValue)
            .ThenBy(t => t.Reminder.Title, StringComparer.CurrentCulture)
            .ToList();

        var active = items.Count(t => !t.Reminder.IsDone);
        _reminderCount.Text = items.Count == 0
            ? "还没有"
            : active == items.Count ? $"{active} 条" : $"{active} 条 · {items.Count - active} 已完成";

        if (items.Count == 0)
        {
            _reminderList.Children.Add(new TextBlock
            {
                Text = "还没有提醒。在下面写一条，点「添加」即可。",
                FontSize = 12,
                Foreground = _faintBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 6, 2, 0),
            });
            return;
        }

        foreach (var (reminder, next) in items)
        {
            _reminderList.Children.Add(BuildReminderRow(reminder, next, now));
        }
    }

    private UIElement BuildReminderRow(Reminder reminder, DateTime? next, DateTime now)
    {
        var isEditing = _editing?.Id == reminder.Id;

        var time = new TextBlock
        {
            Text = reminder.Time.ToString("HH:mm"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = isEditing ? _accentBrush : _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 42,
        };

        var title = new TextBlock
        {
            Text = reminder.Title,
            FontSize = 12.5,
            Foreground = reminder.IsDone ? _faintBrush : _textBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = isEditing ? FontWeights.SemiBold : FontWeights.Normal,
            Opacity = reminder.IsDone ? 0.55 : 1,
        };

        var detail = new TextBlock
        {
            Text = reminder.IsDone
                ? $"已完成 · {reminder.RepeatText}"
                : reminder.SnoozeUntil is { } snooze
                    ? $"稍后 {snooze:HH:mm} 提醒 · {reminder.RepeatText}"
                    : next is null
                        ? $"{reminder.RepeatText} · 已过期"
                        : $"{reminder.RepeatText} · {DateText.Friendly(next.Value, now)}",
            FontSize = 10.5,
            Foreground = reminder.SnoozeUntil is not null && !reminder.IsDone ? _accentBrush : _mutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        texts.Children.Add(detail);

        var delete = new Button
        {
            Content = "✕",
            FontSize = 9.5,
            Padding = new Thickness(6, 1, 6, 1),
            MinWidth = 0,
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
        };
        delete.Click += (_, _) => DeleteReminder(reminder);

        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(time, 0);
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(delete, 2);
        row.Children.Add(time);
        row.Children.Add(texts);
        row.Children.Add(delete);

        var host = new Border
        {
            Background = isEditing ? _selectionBrush : _cardBrush,
            BorderBrush = isEditing ? _accentBrush : _borderBrush,
            BorderThickness = new Thickness(isEditing ? 1.5 : 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 5, 4, 5),
            Child = row,
        };

        host.Tapped += (_, _) => LoadEditor(reminder);
        return host;
    }

    private void RenderFooter()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var outlook = HolidaySummary.Next(_service.Holidays, today);

        if (outlook is null)
        {
            _footerText.Text = "暂无节假日数据（会在后台自动获取，也可断网使用内置数据）。";
            return;
        }

        var when = outlook.Ongoing
            ? $"正在放假 · 第 {today.DayNumber - outlook.Date.DayNumber + 1} 天 / 共 {outlook.OffDays} 天"
            : $"还有 {outlook.DaysUntil} 天 · " + (outlook.Provisional ? "放假安排待官方公布" : $"放假 {outlook.OffDays} 天");

        var makeup = _service.Holidays.NextMakeupWorkday(today);
        var makeupText = makeup is { } m && m.DayNumber - today.DayNumber <= 45
            ? $" · 最近补班 {m.Month}月{m.Day}日"
            : string.Empty;

        _footerText.Text =
            $"🎊 {outlook.Name} {outlook.Date.Month}月{outlook.Date.Day}日 {DateText.Week(outlook.Date)}" +
            (outlook.End != outlook.Date ? $" — {outlook.End.Month}月{outlook.End.Day}日" : string.Empty) +
            $" · {when}{makeupText}　|　数据：{_service.Holidays.SourceLabel}";
    }

    // ---------------- 主题 ----------------

    private void ApplyTheme()
    {
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(190);
        _faintBrush.Color = Neutral(130);
        _borderBrush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(26, 0, 0, 0)
            : Windows.UI.Color.FromArgb(32, 255, 255, 255);
        _cardBrush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(12, 0, 0, 0)
            : Windows.UI.Color.FromArgb(16, 255, 255, 255);
        _selectionBrush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(28, 0x0F, 0x6C, 0xBD)
            : Windows.UI.Color.FromArgb(46, 0x6C, 0xC4, 0xFF);
        _accentBrush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 0x0F, 0x6C, 0xBD)
            : Windows.UI.Color.FromArgb(255, 0x6C, 0xC4, 0xFF);
        _dangerBrush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C)
            : Windows.UI.Color.FromArgb(255, 0xFF, 0x8A, 0x7A);
        _okBrush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 0x0B, 0x6B, 0x62)
            : Windows.UI.Color.FromArgb(255, 0x4A, 0xD1, 0xC0);

        // 放假块底色：整段连成一片淡红；提醒标题用强调色，今天已过点的用灰色
        _offBandBrush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(20, 0xC4, 0x2B, 0x1C)
            : Windows.UI.Color.FromArgb(30, 0xFF, 0x8A, 0x7A);
        _chipBrush.Color = _accentBrush.Color;
        _eventBrush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 0x6A, 0x3D, 0xC4)
            : Windows.UI.Color.FromArgb(255, 0xB4, 0x9B, 0xFF);
        _chipPastBrush.Color = _theme.IsLight
            ? Windows.UI.Color.FromArgb(255, 0x7A, 0x7A, 0x7A)
            : Windows.UI.Color.FromArgb(255, 0xA0, 0xA0, 0xA0);

        RenderAll();
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);
}
