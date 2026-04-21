using Android.Util;

namespace BaluMediaServer;

/// <summary>
/// Log severity level.
/// </summary>
public enum BaluLogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

/// <summary>
/// A single log entry emitted by the BaluMediaServer library.
/// </summary>
public sealed class BaluLogEventArgs : EventArgs
{
    public BaluLogLevel Level { get; }
    public string Tag { get; }
    public string Message { get; }
    public DateTime Timestamp { get; }

    internal BaluLogEventArgs(BaluLogLevel level, string tag, string message)
    {
        Level = level;
        Tag = tag;
        Message = message;
        Timestamp = DateTime.Now;
    }
}

/// <summary>
/// Drop-in replacement for <c>Android.Util.Log</c> that writes to logcat AND fires
/// <see cref="OnLog"/> so consuming apps can subscribe to internal library diagnostics
/// without ADB (useful for in-app log views, remote telemetry, etc.).
/// <para>
/// Subscribe before creating a <see cref="Services.Server"/> instance to capture
/// startup and encoder-selection messages.
/// </para>
/// </summary>
public static class BaluLogger
{
    /// <summary>
    /// Raised on every internal library log call.
    /// The event is fired synchronously on the thread that produced the log entry,
    /// so handlers must return quickly — defer heavy work to a background queue.
    /// </summary>
    public static event EventHandler<BaluLogEventArgs>? OnLog;

    public static void Debug(string tag, string message)
    {
        Log.Debug(tag, message);
        Fire(BaluLogLevel.Debug, tag, message);
    }

    public static void Info(string tag, string message)
    {
        Log.Info(tag, message);
        Fire(BaluLogLevel.Info, tag, message);
    }

    public static void Warn(string tag, string message)
    {
        Log.Warn(tag, message);
        Fire(BaluLogLevel.Warn, tag, message);
    }

    public static void Error(string tag, string message)
    {
        Log.Error(tag, message);
        Fire(BaluLogLevel.Error, tag, message);
    }

    private static void Fire(BaluLogLevel level, string tag, string message)
    {
        try { OnLog?.Invoke(null, new BaluLogEventArgs(level, tag, message)); }
        catch { /* never let a subscriber crash the calling thread */ }
    }
}
