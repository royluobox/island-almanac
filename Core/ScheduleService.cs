using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IslandAlmanac.Core;

/// <summary>一个订阅源（只读）。Name 为空时用日历自身的 X-WR-CALNAME 或域名兜底。</summary>
internal sealed class Feed
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    [JsonIgnore]
    public DateTime? FetchedAt { get; set; }

    [JsonIgnore]
    public string? Error { get; set; }

    [JsonIgnore]
    public int Count { get; set; }
}

/// <summary>
/// 只读日程：订阅别人的 ICS 链接，解析成具体发生，供小岛与月历显示。
/// **不提供编辑、不回写、也不弹通知** —— 数据源仍是 Outlook / Google / 腾讯日历本身。
/// </summary>
internal sealed class ScheduleService
{
    /// <summary>缓存窗口：往前 7 天（回看月历）到往后 180 天。</summary>
    private const int DaysBack = 7;
    private const int DaysAhead = 180;
    private const int MaxItems = 3000;

    private static readonly HttpClient Http = CreateClient(useProxy: true);

    /// <summary>本机地址不走系统代理：代理收到 127.0.0.1 会回 502，本地调试/自建服务会直接失败。</summary>
    private static readonly HttpClient HttpDirect = CreateClient(useProxy: false);

    private readonly List<Feed> _feeds = new();
    private readonly List<ScheduleItem> _items = new();
    private readonly object _gate = new();

    internal DateTime? UpdatedAt { get; private set; }

    internal bool HasFeeds
    {
        get { lock (_gate) return _feeds.Count > 0; }
    }

    internal IReadOnlyList<Feed> Feeds
    {
        get { lock (_gate) return _feeds.ToList(); }
    }

    internal string StatusLabel
    {
        get
        {
            lock (_gate)
            {
                if (_feeds.Count == 0) return "未订阅";
                if (UpdatedAt is null) return $"{_feeds.Count} 个源 · 尚未拉取";
                return $"{_feeds.Count} 个源 · {_items.Count} 条 · 更新于 {UpdatedAt:MM-dd HH:mm}";
            }
        }
    }

    private static HttpClient CreateClient(bool useProxy)
    {
        var handler = new HttpClientHandler { UseProxy = useProxy };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WinIsland-IslandAlmanac/1.0");
        return client;
    }

    private static HttpClient ClientFor(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback ? HttpDirect : Http;

    // ---------------- 载入 / 保存 ----------------

    internal void Load()
    {
        try
        {
            if (File.Exists(AppPaths.FeedsFile))
            {
                var feeds = JsonSerializer.Deserialize<FeedFile>(File.ReadAllText(AppPaths.FeedsFile), JsonOptions);
                lock (_gate)
                {
                    _feeds.Clear();
                    foreach (var feed in feeds?.Feeds ?? new List<Feed>())
                    {
                        if (!string.IsNullOrWhiteSpace(feed.Url)) _feeds.Add(feed);
                    }
                }
            }

            if (File.Exists(AppPaths.ScheduleCacheFile))
            {
                var cache = JsonSerializer.Deserialize<ScheduleCache>(File.ReadAllText(AppPaths.ScheduleCacheFile), JsonOptions);
                lock (_gate)
                {
                    _items.Clear();
                    _items.AddRange(cache?.Items ?? new List<ScheduleItem>());
                    UpdatedAt = cache?.Updated;
                }
            }

            Diag.Info($"日程订阅：{_feeds.Count} 个源，缓存 {_items.Count} 条");
        }
        catch (Exception ex)
        {
            Diag.Warn($"读取日程订阅失败：{ex.Message}");
        }
    }

    private void SaveFeeds()
    {
        try
        {
            AppPaths.Ensure();
            FeedFile file;
            lock (_gate) file = new FeedFile { Feeds = _feeds.ToList() };

            var tmp = AppPaths.FeedsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(tmp, AppPaths.FeedsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Diag.Warn($"保存订阅列表失败：{ex.Message}");
        }
    }

    private void SaveCache()
    {
        try
        {
            AppPaths.Ensure();
            ScheduleCache cache;
            lock (_gate)
            {
                cache = new ScheduleCache
                {
                    Updated = UpdatedAt,
                    Items = _items.Take(MaxItems).ToList(),
                };
            }

            var tmp = AppPaths.ScheduleCacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache, JsonOptions));
            File.Move(tmp, AppPaths.ScheduleCacheFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Diag.Warn($"保存日程缓存失败：{ex.Message}");
        }
    }

    // ---------------- 订阅管理 ----------------

    internal (bool Ok, string Message) AddFeed(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return (false, "链接不能为空");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (false, "请填 http/https 开头的 ICS 链接");
        }

        lock (_gate)
        {
            if (_feeds.Any(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, "这个链接已经订阅过了");
            }

            _feeds.Add(new Feed { Url = url });
        }

        SaveFeeds();
        Diag.Info($"添加日程订阅：{url}");
        return (true, "已添加，正在拉取…");
    }

    internal bool RemoveFeed(string id)
    {
        bool removed;
        lock (_gate)
        {
            var feed = _feeds.FirstOrDefault(f => f.Id == id);
            removed = feed is not null && _feeds.Remove(feed);
            if (removed && feed is not null)
            {
                _items.RemoveAll(item => item.Feed == FeedLabel(feed));
            }
        }

        if (!removed) return false;

        SaveFeeds();
        SaveCache();
        return true;
    }

    private static string FeedLabel(Feed feed) =>
        string.IsNullOrWhiteSpace(feed.Name) ? HostOf(feed.Url) : feed.Name;

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    // ---------------- 抓取 ----------------

    /// <summary>逐个源抓取并重建缓存。返回一句给人看的结果。</summary>
    internal async Task<string> RefreshAsync(CancellationToken token)
    {
        List<Feed> feeds;
        lock (_gate) feeds = _feeds.ToList();

        if (feeds.Count == 0) return "还没有订阅任何日历";

        var today = DateOnly.FromDateTime(DateTime.Today);
        var from = today.AddDays(-DaysBack);
        var to = today.AddDays(DaysAhead);

        var collected = new List<ScheduleItem>();
        var failed = new List<string>();

        foreach (var feed in feeds)
        {
            try
            {
                using var response = await ClientFor(feed.Url).GetAsync(feed.Url, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                var name = IcsParser.ParseCalendarName(text);
                if (!string.IsNullOrWhiteSpace(name)) feed.Name = name!;

                var items = IcsParser.Parse(text, FeedLabel(feed), from, to);

                lock (_gate)
                {
                    feed.FetchedAt = DateTime.Now;
                    feed.Count = items.Count;
                    feed.Error = null;
                }

                collected.AddRange(items);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    feed.FetchedAt = DateTime.Now;
                    feed.Error = ex.Message;
                }

                failed.Add($"{FeedLabel(feed)}：{ex.Message}");
            }
        }

        if (collected.Count == 0 && failed.Count == feeds.Count)
        {
            SaveFeeds();
            return $"全部源拉取失败（{failed[0]}），继续使用上次缓存";
        }

        lock (_gate)
        {
            _items.Clear();
            _items.AddRange(collected
                .OrderBy(item => item.Start)
                .Take(MaxItems));

            UpdatedAt = DateTime.Now;
        }

        SaveFeeds();
        SaveCache();

        var names = string.Join("、", feeds.Select(FeedLabel).Distinct());
        var message = $"已更新 {names}：{collected.Count} 条日程";
        if (failed.Count > 0) message += $"（{failed.Count} 个源失败）";

        Diag.Info(message + $"，窗口 {from:MM-dd}–{to:MM-dd}");
        return message;
    }

    /// <summary>缓存是否过期（超过 6 小时）或需要首次拉取。</summary>
    internal bool NeedsRefresh()
    {
        if (!HasFeeds) return false;
        if (UpdatedAt is null) return true;
        return (DateTime.Now - UpdatedAt.Value).TotalHours >= 6;
    }

    // ---------------- 查询 ----------------

    internal IReadOnlyList<ScheduleItem> OnDay(DateOnly day)
    {
        lock (_gate)
        {
            return _items.Where(item => item.CoversDay(day))
                .OrderBy(item => item.AllDay ? 0 : item.Start.TimeOfDay.Ticks)
                .ThenBy(item => item.Title, StringComparer.CurrentCulture)
                .ToList();
        }
    }

    /// <summary>下一次发生（跨天事件用开始时间判断）。</summary>
    internal (ScheduleItem Item, DateTime When)? NextAfter(DateTime now)
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(now);

            ScheduleItem? best = null;
            var bestWhen = DateTime.MaxValue;

            foreach (var item in _items)
            {
                // 全天/跨天事件：今天一整天都算「进行中」，优先报今天
                var startsToday = item.Start.Date == now.Date;
                var when = startsToday ? now : item.Start;
                if (!startsToday && item.End <= now) continue;

                if (when < bestWhen)
                {
                    bestWhen = when;
                    best = item;
                }
            }

            return best is null ? null : (best, bestWhen);
        }
    }

    /// <summary>今天剩余（含正在进行的）日程。</summary>
    internal IReadOnlyList<ScheduleItem> LaterToday(DateTime now)
    {
        lock (_gate)
        {
            return _items
                .Where(item => item.End > now && item.Start.Date == now.Date)
                .OrderBy(item => item.AllDay ? 0 : item.Start.TimeOfDay.Ticks)
                .ToList();
        }
    }

    // ---------------- 存储 DTO ----------------

    private sealed class FeedFile
    {
        public List<Feed> Feeds { get; set; } = new();
    }

    private sealed class ScheduleCache
    {
        public DateTime? Updated { get; set; }
        public List<ScheduleItem> Items { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
