using System.Text.Json;

namespace IslandAlmanac.Core;

/// <summary>
/// 插件的运行状态（与提醒数据分开存放）。
/// 目前只记「最后一次活着的时间戳」：下次启动时用它算出宿主缺席的时间段，
/// 把那段时间里到点却没响的提醒补报出来，而不是静默丢掉。
/// </summary>
internal sealed class PluginState
{
    /// <summary>最后一次心跳时刻。</summary>
    public DateTime? LastAlive { get; set; }

    internal static PluginState Load()
    {
        try
        {
            if (!File.Exists(AppPaths.StateFile)) return new PluginState();

            var json = File.ReadAllText(AppPaths.StateFile);
            return JsonSerializer.Deserialize<PluginState>(json) ?? new PluginState();
        }
        catch (Exception ex)
        {
            Diag.Warn($"读取运行状态失败：{ex.Message}");
            return new PluginState();
        }
    }

    internal void Save(DateTime aliveAt)
    {
        try
        {
            AppPaths.Ensure();
            LastAlive = aliveAt;

            var tmp = AppPaths.StateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, AppPaths.StateFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Diag.Warn($"写入运行状态失败：{ex.Message}");
        }
    }
}
