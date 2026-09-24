using System.Text;

namespace ZSnaper.Services;

internal static class AppDiagnostics
{
    private static readonly object SyncRoot = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZSnaper",
        "Logs");
    private static int _initialized;
    private static int _threadExceptionNoticeShown;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) =>
        {
            LogException("WinForms.ThreadException", args.Exception);
            if (Interlocked.Exchange(ref _threadExceptionNoticeShown, 1) == 0)
            {
                try
                {
                    MessageBox.Show(
                        "ZSnaper 遇到异常，但已尽量保持运行。错误详情已写入本地日志；如果功能状态异常，建议重新启动应用。",
                        "ZSnaper 稳定性保护",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                catch
                {
                    // Never let diagnostic UI cause another failure.
                }
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Exception exception = args.ExceptionObject as Exception
                ?? new InvalidOperationException(args.ExceptionObject?.ToString() ?? "Unknown fatal error");
            LogException(args.IsTerminating ? "AppDomain.Terminating" : "AppDomain.UnhandledException", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        PruneOldLogs();
    }

    public static void LogException(string source, Exception exception)
    {
        WriteEntry(source, exception.ToString());
    }

    public static void LogMessage(string source, string message)
    {
        WriteEntry(source, message);
    }

    private static void WriteEntry(string source, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                string path = Path.Combine(LogDirectory, $"ZSnaper-{DateTime.Now:yyyyMMdd}.log");
                var entry = new StringBuilder()
                    .Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] ")
                    .AppendLine(source)
                    .AppendLine(message)
                    .AppendLine(new string('-', 72))
                    .ToString();
                File.AppendAllText(path, entry, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Diagnostics are best-effort and must never destabilize the app.
        }
    }

    private static void PruneOldLogs()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;
            DateTime cutoff = DateTime.UtcNow.AddDays(-14);
            foreach (string path in Directory.EnumerateFiles(LogDirectory, "ZSnaper-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                }
                catch
                {
                    // One locked log must not block cleanup of the rest.
                }
            }
        }
        catch
        {
            // Ignore unavailable log directories.
        }
    }
}
