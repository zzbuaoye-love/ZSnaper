using System.Diagnostics;
using ZSnaper.Installer.Core;

namespace ZSnaper.UpdateInstaller;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (HasFlag(args, "--uninstall-apply"))
        {
            return ApplyUninstall(args);
        }

        if (HasFlag(args, "--uninstall"))
        {
            return StartUninstall();
        }

        if (HasFlag(args, "--silent"))
        {
            return RunSilent(args);
        }

        ApplicationConfiguration.Initialize();
        using UpdateForm form = new(GetValue(args, "--package"));
        Application.Run(form);
        return 0;
    }

    private static int RunSilent(IReadOnlyList<string> args)
    {
        string logPath = GetValue(args, "--log") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZSnaper", "Updates", "update.log");
        try
        {
            string packagePath = GetValue(args, "--package")
                ?? throw new ArgumentException("--package is required in silent mode.");
            WaitForProcess(GetIntValue(args, "--wait-for-pid"));

            InstallerService installerService = new();
            bool noRegister = HasFlag(args, "--no-register");
            string? installDirectory = GetValue(args, "--install-directory");
            InstallationInfo installation = installDirectory is null
                ? installerService.GetInstalled() ?? throw new InvalidOperationException("ZSnaper is not installed.")
                : new InstallationInfo(
                    InstallerPaths.Normalize(installDirectory),
                    GetValue(args, "--installed-version") ?? string.Empty,
                    InstallerPaths.GetProductExecutablePath(installDirectory),
                    InstallerPaths.GetUpdateExecutablePath(installDirectory));

            new UpdatePackageService(installerService).Apply(packagePath, installation, updateInstalledVersion: !noRegister);
            AppendLog(logPath, $"Updated {installation.InstallDirectory} successfully.");
            if (HasFlag(args, "--restart"))
            {
                Restart(installation.ExecutablePath);
            }

            return 0;
        }
        catch (Exception exception)
        {
            AppendLog(logPath, exception.ToString());
            string? installDirectory = GetValue(args, "--install-directory");
            if (HasFlag(args, "--restart") && installDirectory is not null)
            {
                Restart(GetRestartExecutable(installDirectory));
            }

            return 1;
        }
    }

    private static int StartUninstall()
    {
        try
        {
            InstallationInfo installation = new InstallerService().GetInstalled()
                ?? throw new InvalidOperationException("ZSnaper is not registered as installed.");
            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The updater process path is unavailable.");
            string helperDirectory = Path.Combine(Path.GetTempPath(), InstallerPaths.ProductName, "uninstall");
            Directory.CreateDirectory(helperDirectory);
            string helperPath = Path.Combine(helperDirectory, "uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(executable, helperPath);
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = helperPath,
                Arguments = $"--uninstall-apply {Quote(installation.InstallDirectory)} --wait-for-pid {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = helperDirectory
            }) ?? throw new InvalidOperationException("Unable to start the uninstall helper.");
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "ZSnaper Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int ApplyUninstall(IReadOnlyList<string> args)
    {
        try
        {
            string installDirectory = GetValue(args, "--uninstall-apply")
                ?? throw new ArgumentException("Missing uninstall directory.");
            WaitForProcess(GetIntValue(args, "--wait-for-pid"));
            new InstallerService().Uninstall(installDirectory);
            ScheduleSelfDelete(Environment.ProcessPath);
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "ZSnaper Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void ScheduleSelfDelete(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return;
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

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string GetRestartExecutable(string installDirectory)
    {
        string executablePath = InstallerPaths.GetProductExecutablePath(installDirectory);
        return File.Exists(executablePath)
            ? executablePath
            : Path.Combine(InstallerPaths.Normalize(installDirectory), InstallerPaths.ProductExecutableName);
    }

    private static void WaitForProcess(int? processId)
    {
        if (processId is null || processId <= 0) return;
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId.Value);
            process.WaitForExit(30_000);
            if (!process.HasExited)
            {
                throw new TimeoutException("ZSnaper did not exit within 30 seconds.");
            }
        }
        catch (ArgumentException)
        {
            // The application already exited.
        }
    }

    private static void Restart(string executablePath)
    {
        if (!File.Exists(executablePath)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "--startup",
            UseShellExecute = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        });
    }

    private static void AppendLog(string path, string message)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }

    private static bool HasFlag(IEnumerable<string> args, string flag) =>
        args.Any(value => string.Equals(value, flag, StringComparison.OrdinalIgnoreCase));

    private static int? GetIntValue(IReadOnlyList<string> args, string flag) =>
        int.TryParse(GetValue(args, flag), out int value) ? value : null;

    private static string? GetValue(IReadOnlyList<string> args, string flag)
    {
        for (int index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return args.FirstOrDefault(value => value.EndsWith(".zup", StringComparison.OrdinalIgnoreCase));
    }
}
