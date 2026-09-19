using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using ZSnaper.Installer.Core;

namespace ZSnaper.FullInstaller;

internal static class Program
{
    public const string Version = "0.0.5-beta";
    internal static string? PayloadDirectory { get; private set; }
    internal static string InstallerExecutable { get; private set; } = string.Empty;

    [STAThread]
    private static int Main(string[] args)
    {
        if (HasFlag(args, "--self-test-shortcuts"))
        {
            return TestShortcutInterop();
        }

        if (HasFlag(args, "--uninstall-apply"))
        {
            return ApplyUninstall(args);
        }

        if (HasFlag(args, "--uninstall"))
        {
            return StartUninstall(args);
        }

        try
        {
            InstallerExecutable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The installer process path is unavailable.");
            try
            {
                PayloadDirectory = PayloadArchive.ExtractEmbeddedPayload(InstallerExecutable);
            }
            catch (InvalidDataException)
            {
                // A normal project build has no appended payload. The UI explains how to create one.
            }

            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            ShowNativeError(exception.Message, "ZSnaper Installer");
            return 1;
        }
        finally
        {
            if (PayloadDirectory is not null)
            {
                PayloadArchive.TryDeleteDirectory(PayloadDirectory);
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static int StartUninstall(string[] args)
    {
        try
        {
            InstallerService service = new();
            InstallationInfo installation = service.GetInstalled()
                ?? throw new InvalidOperationException("ZSnaper is not registered as installed.");
            string currentExecutable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The installer process path is unavailable.");
            string helperDirectory = Path.Combine(Path.GetTempPath(), InstallerPaths.ProductName, "uninstall");
            Directory.CreateDirectory(helperDirectory);
            string helperPath = Path.Combine(helperDirectory, "uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(currentExecutable, helperPath, overwrite: true);

            Process.Start(new ProcessStartInfo
            {
                FileName = helperPath,
                Arguments = $"--uninstall-apply \"{installation.InstallDirectory}\" --wait-for-pid {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = helperDirectory
            });
            return 0;
        }
        catch (Exception exception)
        {
            ShowNativeError(exception.Message, "ZSnaper Uninstall");
            return 1;
        }
    }

    private static int ApplyUninstall(string[] args)
    {
        try
        {
            string installDirectory = GetValue(args, "--uninstall-apply")
                ?? throw new ArgumentException("Missing uninstall directory.");
            if (int.TryParse(GetValue(args, "--wait-for-pid"), out int processId))
            {
                ProcessGuard.WaitForProcessExit(processId, TimeSpan.FromSeconds(15));
            }

            new InstallerService().Uninstall(installDirectory);
            ScheduleSelfDelete(Environment.ProcessPath);
            return 0;
        }
        catch (Exception exception)
        {
            ShowNativeError(exception.Message, "ZSnaper Uninstall");
            return 1;
        }
    }

    private static void ScheduleSelfDelete(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        string commandShell = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        string escapedPath = executablePath.Replace("\"", "\"\"");
        Process.Start(new ProcessStartInfo
        {
            FileName = commandShell,
            Arguments = $"/d /c \"timeout /t 2 /nobreak >nul & del /f /q \"\"{escapedPath}\"\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        });
    }

    private static int TestShortcutInterop()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            InstallerPaths.ProductName,
            "shortcut-self-test-" + Guid.NewGuid().ToString("N"));
        string shortcutPath = Path.Combine(testDirectory, "ZSnaper Shortcut Test.lnk");
        string targetPath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "ZSnaper.FullInstaller.exe");

        try
        {
            ShortcutService.Create(
                shortcutPath,
                targetPath,
                description: "ZSnaper installer shortcut self-test");
            if (!File.Exists(shortcutPath))
            {
                throw new IOException("The shortcut file was not created.");
            }

            ShortcutService.DeleteIfOwned(shortcutPath, targetPath);
            if (File.Exists(shortcutPath))
            {
                throw new IOException("The shortcut file was not removed.");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            PayloadArchive.TryDeleteDirectory(testDirectory);
        }
    }

    private static bool HasFlag(IEnumerable<string> args, string flag) =>
        args.Any(value => string.Equals(value, flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetValue(IReadOnlyList<string> args, string flag)
    {
        for (int index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    internal static void ShowNativeError(string message, string title)
    {
        const uint MbOk = 0x00000000;
        const uint MbIconError = 0x00000010;
        _ = MessageBox(IntPtr.Zero, message, title, MbOk | MbIconError);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr windowHandle, string text, string caption, uint type);
}
