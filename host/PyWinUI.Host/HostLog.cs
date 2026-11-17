using System.Text;

namespace PyWinUI.Host;

/// <summary>
/// 宿主日志。
///
/// 存在的理由：WinUI 3 的失败经常是 stowed exception，进程直接以 0xC000027B 退出，
/// 托管侧的 try/catch 抓不到，也没有任何输出。唯一能用的手段是往文件里逐行打点，
/// 事后看最后一行停在哪。所以关键日志（异常、生命周期、告警）写完立即 Flush。
///
/// 性能约束：这里曾是**每条双向消息**都会经过的热路径。旧实现对每条入站写一次
/// "&lt;&lt;"、每条出站写一次 "&gt;&gt;"，每次 File.AppendAllText 都要打开/写/关文件句柄，
/// 而且全挤在一把跨 UI 线程与读线程的静态锁上——实时 UI 下约 200 次同步磁盘
/// IO/秒，后台读线程的 IO 延迟会直接变成 UI 卡顿。
///
/// 现在分两级：Write 只给关键日志，长驻 StreamWriter（不再反复开关文件）；
/// Trace 给逐条报文，默认完全不落盘，PYWINUI3_TRACE=1 抓包时才启用。
/// </summary>
internal static class HostLog
{
    private static readonly object Gate = new();

    /// <summary>
    /// 日志落盘位置。放在 TEMP 下，避免污染程序目录。
    ///
    /// 文件名带 PID：同时开两个 App / 两个宿主时各写各的，不会互相清空。
    /// 这个日志的唯一用途是「进程静默崩溃后看最后一行」，共用同一个文件
    /// 恰好把用途毁掉。要排查某个进程，按 PID 找对应文件即可。
    /// </summary>
    public static string FilePath { get; } =
        Path.Combine(Path.GetTempPath(), $"pywinui3-host-{Environment.ProcessId}.log");

    private static StreamWriter? _writer;

    /// <summary>
    /// 报文级逐条日志开关。默认关闭；设 PYWINUI3_TRACE=1 才打开，
    /// 用于抓管道内容排查协议问题。
    /// </summary>
    public static bool TraceEnabled { get; } =
        Environment.GetEnvironmentVariable("PYWINUI3_TRACE") == "1";

    private static void EnsureWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        try
        {
            File.WriteAllText(FilePath, "", Encoding.UTF8);
            var stream = new FileStream(
                FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(false))
            {
                AutoFlush = false,
            };
        }
        catch
        {
            _writer = null;
        }
    }

    /// <summary>清空日志，用于每次启动时重新开始。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // 日志本身绝不能成为故障源
            }

            _writer = null;
            EnsureWriter();
        }
    }

    /// <summary>关键日志：异常、生命周期、告警。写完立即 Flush。</summary>
    public static void Write(string message)
    {
        lock (Gate)
        {
            EnsureWriter();
            try
            {
                _writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
                _writer?.Flush();
            }
            catch
            {
                // 日志失败不能影响主流程
            }
        }
    }

    /// <summary>
    /// 逐条报文日志（&lt;&lt; / &gt;&gt;）。默认零 IO；开 TRACE 时才落盘。
    /// 这是"日志分级"而不是"删日志"——诊断能力靠环境变量随时能拿回来。
    /// </summary>
    public static void Trace(string message)
    {
        if (TraceEnabled)
        {
            Write(message);
        }
    }

    public static void Write(Exception ex, string context)
    {
        Write($"{context}: {ex.GetType().Name}: {ex.Message}");
        if (ex.StackTrace is { Length: > 0 })
        {
            Write("    " + ex.StackTrace.Replace(Environment.NewLine, Environment.NewLine + "    "));
        }
    }
}
