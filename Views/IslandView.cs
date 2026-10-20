using IslandAlmanac.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace IslandAlmanac.Views;

/// <summary>
/// 岛上的视图：一棵树同时承载小岛（紧凑态）与大岛（展开态）。
/// 紧凑态只有「图标 + 日期」，绝不溢出；
/// 展开态左边是当月迷你月历，右边上下两块信息（节日 / 节气或提醒）。
/// 形态动画逐帧直接赋值（不用 Storyboard —— 插件程序集里属性路径动画会抛 0x800F1001）。
/// </summary>
internal sealed class IslandView : UserControl, IMorphView
{
    private const double BackAmplitude = 0.45;
    private const double CompactIconSize = 15;
    private const double ExpandedIconSize = 18;
    private const double CompactDateSize = 14.5;
    private const double ExpandedDateSize = 15.5;
    private const double ExpandedDetailHeight = 140;
    private const int CellCount = 42;

    private readonly IIslandTheme _theme;
    private readonly FontIcon _icon;
    private readonly TextBlock _date;
    private readonly TextBlock _monthLabel;
    private readonly Grid _body;
    private readonly RowDefinition _detailRow;

    private readonly TextBlock _topTitle;
    private readonly TextBlock _topDetail;
    private readonly TextBlock _bottomTitle;
    private readonly TextBlock _bottomDetail;
    private readonly MiniSlot[] _cells = new MiniSlot[CellCount];

    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _accentBrush = new();
    private readonly SolidColorBrush _todayBrush = new();
    private readonly SolidColorBrush _dangerBrush = new();
    private readonly SolidColorBrush _okBrush = new();
    private readonly SolidColorBrush _cardBrush = new();

    private readonly DispatcherQueueTimer? _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;
    private double _progress;

    internal IslandView(PluginManifest manifest, IIslandTheme theme)
    {
        _theme = theme;
        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        _icon = new FontIcon
        {
            Glyph = manifest.IconGlyph ?? "\uE787",
            FontSize = CompactIconSize,
            Foreground = _accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _date = new TextBlock
        {
            Text = "1月1日 周一",
            FontSize = CompactDateSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        _monthLabel = new TextBlock
        {
            Text = "2026年10月",
            FontSize = 10,
            Foreground = _faintBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(1, 0, 0, 2),
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(_icon);
        header.Children.Add(_date);

        // ---------- 左：迷你月历 ----------
        var weekRow = new Grid { Height = 14, ColumnSpacing = 2 };
        for (var i = 0; i < 7; i++)
        {
            weekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock
            {
                Text = i == 6 ? "日" : DateText.WeekChar(i + 1),
                FontSize = 9,
                Foreground = _faintBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Grid.SetColumn(label, i);
            weekRow.Children.Add(label);
        }

        var monthGrid = new Grid { RowSpacing = 1, ColumnSpacing = 2 };
        for (var i = 0; i < 7; i++)
        {
            monthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var r = 0; r < 6; r++)
        {
            monthGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        for (var i = 0; i < CellCount; i++)
        {
            var slot = new MiniSlot();
            _cells[i] = slot;
            Grid.SetRow(slot.Host, i / 7);
            Grid.SetColumn(slot.Host, i % 7);
            monthGrid.Children.Add(slot.Host);
        }

        var calendar = new Grid();
        calendar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        calendar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        calendar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_monthLabel, 0);
        Grid.SetRow(weekRow, 1);
        Grid.SetRow(monthGrid, 2);
        calendar.Children.Add(_monthLabel);
        calendar.Children.Add(weekRow);
        calendar.Children.Add(monthGrid);

        // ---------- 右：两块信息 ----------
        _topTitle = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _topDetail = new TextBlock
        {
            FontSize = 10.5,
            Foreground = _mutedBrush,
            TextWrapping = TextWrapping.Wrap,
        };

        _bottomTitle = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _bottomDetail = new TextBlock
        {
            FontSize = 10.5,
            Foreground = _mutedBrush,
            TextWrapping = TextWrapping.Wrap,
        };

        var right = new Grid { RowSpacing = 6 };
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var topBlock = Block(_topTitle, _topDetail);
        var bottomBlock = Block(_bottomTitle, _bottomDetail);
        Grid.SetRow(topBlock, 0);
        Grid.SetRow(bottomBlock, 1);
        right.Children.Add(topBlock);
        right.Children.Add(bottomBlock);

        _body = new Grid { ColumnSpacing = 10 };
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(calendar, 0);
        Grid.SetColumn(right, 1);
        _body.Children.Add(calendar);
        _body.Children.Add(right);
        _body.Opacity = 0;

        _detailRow = new RowDefinition { Height = new GridLength(0) };

        var root = new Grid { Padding = new Thickness(14, 6, 14, 6), RowSpacing = 5 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(_detailRow);
        Grid.SetRow(header, 0);
        Grid.SetRow(_body, 1);
        root.Children.Add(header);
        root.Children.Add(_body);

        Content = root;

        _morphTimer = DispatcherQueue?.CreateTimer();
        if (_morphTimer is not null)
        {
            _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        Unloaded += (_, _) => _morphTimer?.Stop();
    }

    public UIElement View => this;

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(1, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(0, duration);

    private Border Block(TextBlock title, TextBlock detail) => new()
    {
        Background = _cardBrush,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(9, 6, 9, 6),
        Child = new StackPanel { Spacing = 2, Children = { title, detail } },
    };

    // ---------------- 内容 ----------------

    internal void Render(IslandSnapshot snapshot)
    {
        _date.Text = snapshot.DateLine;
        _monthLabel.Text = snapshot.MonthLabel;

        for (var i = 0; i < CellCount && i < snapshot.Month.Count; i++)
        {
            var slot = _cells[i];
            var cell = snapshot.Month[i];

            slot.Day.Text = cell.Day;
            slot.Host.Background = cell.IsToday ? _todayBrush : null;
            slot.Day.Opacity = cell.InMonth ? 1 : 0.32;
            slot.Day.Foreground = cell.IsOff ? _dangerBrush : cell.IsMakeup ? _okBrush : _textBrush;
            slot.Day.FontWeight = cell.IsToday || cell.IsOff ? FontWeights.Bold : FontWeights.Normal;
        }

        _topTitle.Text = snapshot.TopTitle;
        _topDetail.Text = snapshot.TopDetail;
        _bottomTitle.Text = snapshot.BottomTitle;
        _bottomDetail.Text = snapshot.BottomDetail;
    }

    private sealed class MiniSlot
    {
        internal Border Host { get; }
        internal TextBlock Day { get; }

        internal MiniSlot()
        {
            Day = new TextBlock
            {
                FontSize = 9.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Host = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(0, 1, 0, 1),
                Child = Day,
            };
        }
    }

    // ---------------- 主题 ----------------

    private void ApplyThemeColors()
    {
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(195);
        _faintBrush.Color = Neutral(135);
        _accentBrush.Color = _theme.IsLight ? Argb(255, 0x0F, 0x6C, 0xBD) : Argb(255, 0x6C, 0xC4, 0xFF);
        _todayBrush.Color = _theme.IsLight ? Argb(30, 0x0F, 0x6C, 0xBD) : Argb(44, 0x6C, 0xC4, 0xFF);
        _dangerBrush.Color = _theme.IsLight ? Argb(255, 0xC4, 0x2B, 0x1C) : Argb(255, 0xFF, 0x8A, 0x7A);
        _okBrush.Color = _theme.IsLight ? Argb(255, 0x0B, 0x6B, 0x62) : Argb(255, 0x4A, 0xD1, 0xC0);
        _cardBrush.Color = _theme.IsLight ? Argb(14, 0, 0, 0) : Argb(18, 255, 255, 255);
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Argb(alpha, 0, 0, 0)
        : Argb(alpha, 255, 255, 255);

    private static Windows.UI.Color Argb(byte a, byte r, byte g, byte b) => Windows.UI.Color.FromArgb(a, r, g, b);

    // ---------------- 变形 ----------------

    private void StartMorph(double target, TimeSpan duration)
    {
        _morphFrom = _progress;
        _morphTarget = target;
        _morphDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
        _morphStart = DateTimeOffset.UtcNow;

        if (_morphTimer is null)
        {
            ApplyMorph(target);
            return;
        }

        _morphTimer.Start();
    }

    private void OnMorphTick()
    {
        try
        {
            var elapsed = (DateTimeOffset.UtcNow - _morphStart).TotalMilliseconds;
            var t = Math.Clamp(elapsed / Math.Max(1, _morphDuration.TotalMilliseconds), 0, 1);

            ApplyMorph(_morphFrom + (_morphTarget - _morphFrom) * BackEaseOut(t));

            if (t >= 1)
            {
                _morphTimer?.Stop();
                ApplyMorph(_morphTarget);
            }
        }
        catch (Exception ex)
        {
            _morphTimer?.Stop();
            Diag.Warn($"形态动画异常：{ex.Message}");
        }
    }

    /// <summary>BackEase(EaseOut, A) = 1 + (A+1)(t-1)³ + A(t-1)²，与宿主曲线一致。</summary>
    private static double BackEaseOut(double t)
    {
        var d = t - 1;
        return 1 + (BackAmplitude + 1) * d * d * d + BackAmplitude * d * d;
    }

    private void ApplyMorph(double progress)
    {
        _progress = progress;

        var p = Math.Clamp(progress, 0, 1);
        _detailRow.Height = new GridLength(Math.Max(0, ExpandedDetailHeight * p));
        _body.Opacity = p;

        _icon.FontSize = CompactIconSize + (ExpandedIconSize - CompactIconSize) * p;
        _date.FontSize = CompactDateSize + (ExpandedDateSize - CompactDateSize) * p;
    }
}
