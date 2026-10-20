using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IslandAlmanac.Core;

/// <summary>插件自己的数据目录：%LocalAppData%\WinIsland\island-almanac。</summary>
internal static class AppPaths
{
    internal static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinIsland", "island-almanac");

    internal static string HolidaysCache => Path.Combine(Root, "holidays-cache.json");
    internal static string RemindersFile => Path.Combine(Root, "reminders.json");

    internal static string StateFile => Path.Combine(Root, "state.json");

    internal static string FeedsFile => Path.Combine(Root, "feeds.json");

    internal static string ScheduleCacheFile => Path.Combine(Root, "schedule-cache.json");

    internal static void Ensure() => Directory.CreateDirectory(Root);
}

internal sealed record HolidayDay(DateOnly Date, string Name, bool IsOff);

internal sealed record HolidayBlock(string Name, DateOnly Start, DateOnly End, int OffDays, int WorkDays)
{
    /// <summary>今天是否正处在这个假期里。</summary>
    public bool Contains(DateOnly today) => today >= Start && today <= End;

    public int DaysUntil(DateOnly today) => Math.Max(0, Start.DayNumber - today.DayNumber);
}

/// <summary>
/// 法定节假日 / 调休数据。三层来源，按优先级：在线缓存 → 内置数据。
/// 在线源是 holiday-cn（据国务院办公厅公告自动生成），运行期可选刷新；断网时完全靠内置数据。
/// </summary>
internal sealed class HolidayService
{
    private const string CdnTemplate = "https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{0}.json";
    private const string RawTemplate = "https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{0}.json";

    private static readonly HttpClient Http = CreateClient();

    private readonly Dictionary<DateOnly, HolidayDay> _days = new();
    private readonly object _gate = new();

    internal DateTime? UpdatedAt { get; private set; }
    internal string SourceLabel { get; private set; } = "内置数据";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WinIsland-IslandAlmanac/1.0");
        return client;
    }

    // ---------------- 加载 ----------------

    internal void LoadEmbedded()
    {
        try
        {
            var asm = typeof(HolidayService).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("holidays.json", StringComparison.OrdinalIgnoreCase));
            if (name is null) return;

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) return;

            var file = JsonSerializer.Deserialize<HolidayFile>(stream, JsonOptions);
            Apply(file);
        }
        catch (Exception ex)
        {
            Diag.Warn($"读取内置节假日数据失败：{ex.Message}");
        }
    }

    internal void LoadCache()
    {
        try
        {
            if (!File.Exists(AppPaths.HolidaysCache)) return;

            var json = File.ReadAllText(AppPaths.HolidaysCache);
            var file = JsonSerializer.Deserialize<HolidayFile>(json, JsonOptions);
            if (file is null) return;

            Apply(file);
            UpdatedAt = file.updated;
            if (UpdatedAt is not null) SourceLabel = $"在线数据 {UpdatedAt:MM-dd}";
        }
        catch (Exception ex)
        {
            Diag.Warn($"读取节假日缓存失败：{ex.Message}");
        }
    }

    private void Apply(HolidayFile? file)
    {
        if (file is null) return;

        lock (_gate)
        {
            Merge(file.days);
            if (file.years is not null)
            {
                foreach (var year in file.years) Merge(year.days);
            }
        }
    }

    private void Merge(IEnumerable<HolidayEntry>? entries)
    {
        if (entries is null) return;

        foreach (var e in entries)
        {
            if (e is null || string.IsNullOrWhiteSpace(e.date)) continue;
            if (!DateOnly.TryParse(e.date, out var date)) continue;
            if (string.IsNullOrWhiteSpace(e.name)) continue;

            _days[date] = new HolidayDay(date, e.name.Trim(), e.isOffDay);
        }
    }

    internal bool HasYear(int year)
    {
        lock (_gate) return _days.Keys.Any(d => d.Year == year);
    }

    // ---------------- 查询 ----------------

    internal HolidayDay? Get(DateOnly date)
    {
        lock (_gate) return _days.TryGetValue(date, out var day) ? day : null;
    }

    internal bool IsOffDay(DateOnly date) => Get(date)?.IsOff == true;

    /// <summary>调休上班日（周末/工作日都可能，数据里 isOffDay = false 且带节日名）。</summary>
    internal bool IsMakeupWorkday(DateOnly date) => Get(date) is { IsOff: false };

    /// <summary>
    /// 下一个（或正在进行的）放假块。today 在假期内时返回今天所在的这一块。
    /// </summary>
    internal HolidayBlock? NextBlock(DateOnly today, int maxDays = 420)
    {
        DateOnly? first = null;

        for (var i = 0; i <= maxDays; i++)
        {
            var d = today.AddDays(i);
            if (IsOffDay(d))
            {
                first = d;
                break;
            }
        }

        if (first is not { } firstOff) return null;

        var day0 = Get(firstOff)!;

        // 今天正处在假期中时，把起点往前推到假期第一天（否则「第几天」会算错）
        var start = firstOff;
        for (var i = 0; i < 20; i++)
        {
            var prev = start.AddDays(-1);
            if (IsOffDay(prev) && Get(prev)?.Name == day0.Name) start = prev;
            else break;
        }

        var end = firstOff;
        while (IsOffDay(end.AddDays(1)) && Get(end.AddDays(1))?.Name == day0.Name && (end.DayNumber - start.DayNumber) < 20)
        {
            end = end.AddDays(1);
        }

        var offDays = 0;
        var workDays = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (IsOffDay(d)) offDays++;
            else workDays++;
        }

        return new HolidayBlock(day0.Name, start, end, offDays, workDays);
    }

    /// <summary>下一个调休补班日。</summary>
    internal DateOnly? NextMakeupWorkday(DateOnly today, int maxDays = 420)
    {
        for (var i = 0; i <= maxDays; i++)
        {
            var d = today.AddDays(i);
            if (IsMakeupWorkday(d)) return d;
        }

        return null;
    }

    // ---------------- 在线刷新 ----------------

    /// <summary>抓取指定年份（含前后各一年），成功则写缓存。返回给用户看的一句话。</summary>
    internal async Task<string> RefreshAsync(int centerYear, CancellationToken token)
    {
        var years = new[] { centerYear - 1, centerYear, centerYear + 1 };
        var fetched = new List<HolidayEntry>();
        var gotYears = new List<int>();

        foreach (var year in years)
        {
            var (entries, error) = await FetchYearAsync(year, token).ConfigureAwait(false);
            if (entries.Count > 0)
            {
                fetched.AddRange(entries);
                gotYears.Add(year);
            }
            else if (error is not null)
            {
                // 「官方还没公布」是常态，不该按失败刷警告；只有真正的网络/格式错误才 Warn
                if (error.Contains("返回空数据", StringComparison.Ordinal))
                {
                    Diag.Info($"{year} 年放假安排尚未公布，继续用兜底数据");
                }
                else
                {
                    Diag.Warn($"抓取 {year} 年节假日失败：{error}");
                }
            }
        }

        if (fetched.Count == 0)
        {
            return "在线获取失败（网络或 CDN 不可达），继续使用本地数据";
        }

        lock (_gate) Merge(fetched);

        UpdatedAt = DateTime.Now;
        SourceLabel = $"在线数据 {UpdatedAt:MM-dd}";

        try
        {
            AppPaths.Ensure();
            var file = new HolidayFile
            {
                updated = UpdatedAt,
                days = fetched
                    .GroupBy(e => e.date)
                    .Select(g => g.First())
                    .OrderBy(e => e.date)
                    .ToList(),
            };

            await File.WriteAllTextAsync(AppPaths.HolidaysCache, JsonSerializer.Serialize(file, JsonOptions), token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diag.Warn($"写入节假日缓存失败：{ex.Message}");
        }

        return $"已更新 {string.Join("、", gotYears)} 年节假日数据（共 {fetched.Count} 天）";
    }

    private async Task<(List<HolidayEntry> Entries, string? Error)> FetchYearAsync(int year, CancellationToken token)
    {
        string? lastError = null;

        foreach (var template in new[] { CdnTemplate, RawTemplate })
        {
            var url = string.Format(template, year);
            try
            {
                using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                var file = JsonSerializer.Deserialize<HolidayFile>(json, JsonOptions);
                var entries = file?.days ?? file?.years?.SelectMany(y => y.days ?? new()).ToList() ?? new List<HolidayEntry>();

                if (entries.Count > 0) return (entries, null);

                lastError = $"{url} 返回空数据（官方可能尚未公布）";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        return (new List<HolidayEntry>(), lastError);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    // ---------------- DTO ----------------

    private sealed class HolidayFile
    {
        public DateTime? updated { get; set; }
        public List<HolidayEntry>? days { get; set; }
        public List<HolidayYear>? years { get; set; }
    }

    private sealed class HolidayYear
    {
        public int year { get; set; }
        public List<HolidayEntry>? days { get; set; }
    }

    private sealed class HolidayEntry
    {
        public string? name { get; set; }
        public string? date { get; set; }
        public bool isOffDay { get; set; }
    }
}
