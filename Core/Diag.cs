namespace IslandAlmanac.Core;

/// <summary>
/// 极简日志转发：核心逻辑类不继承 IslandPluginBase，拿不到宿主的 Log 实例，
/// 由插件在初始化时把宿主的日志方法挂到这里。
/// </summary>
internal static class Diag
{
    internal static Action<string>? InfoSink;
    internal static Action<string>? WarnSink;
    internal static Action<string, Exception?>? ErrorSink;

    internal static void Info(string message) => Safe(InfoSink, message);

    internal static void Warn(string message) => Safe(WarnSink, message);

    internal static void Error(string message, Exception? ex = null)
    {
        try { ErrorSink?.Invoke(message, ex); }
        catch { /* 日志本身不能把插件带崩 */ }
    }

    private static void Safe(Action<string>? sink, string message)
    {
        try { sink?.Invoke(message); }
        catch { /* ignore */ }
    }
}
