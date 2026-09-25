using Serilog;
using Serilog.Events;
using Serilog.Core;

namespace ZSnaper.Services;

internal static class AppDiagnostics
{
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZSnaper",
        "Logs");
    private static int _initialized;
    private static int _threadExceptionNoticeShown;
    private static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Information);

    public static void Initialize(string? logDirectoryOverride = null)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        try
        {
            string logDirectory = logDirectoryOverride ?? LogDirectory;
            Directory.CreateDirectory(logDirectory);
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(LevelSwitch)
                .WriteTo.File(
                    Path.Combine(logDirectory, "ZSnaper-.log"),
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    retainedFileCountLimit: 14,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"ZSnaper file logging initialization failed: {exception}");
            // Logging must never prevent the application from starting.
        }

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

        SetMinimumLevel(ConfigService.Current.LogLevel);
        Log.Information("ZSnaper 启动 | 版本: {Version} | 配置目录: {ConfigDirectory}",
            Helpers.AppVersionInfo.DisplayVersion, ConfigService.ActiveConfigDirectory);
    }

    public static void SetMinimumLevel(AppLogLevel level)
    {
        LevelSwitch.MinimumLevel = level switch
        {
            AppLogLevel.Debug => LogEventLevel.Debug,
            AppLogLevel.Warning => LogEventLevel.Warning,
            AppLogLevel.Error => LogEventLevel.Error,
            _ => LogEventLevel.Information
        };
    }

    public static void LogException(string source, Exception exception, LogEventLevel level = LogEventLevel.Error)
    {
        Log.Write(level, exception, "{Source} failed", source);
    }

    public static void LogMessage(string source, string message, LogEventLevel level = LogEventLevel.Information)
    {
        Log.Write(level, "{Source}: {Message}", source, message);
    }

    public static void Shutdown()
    {
        Log.Information("ZSnaper 退出");
        Log.CloseAndFlush();
    }
}
