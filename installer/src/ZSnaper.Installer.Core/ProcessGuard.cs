using System.Diagnostics;

namespace ZSnaper.Installer.Core;

public static class ProcessGuard
{
    public static void EnsureClosed(string installDirectory, TimeSpan? timeout = null)
    {
        string expectedExecutable = InstallerPaths.GetProductExecutablePath(installDirectory);
        string normalizedDirectory = InstallerPaths.Normalize(installDirectory);
        string[] legacyExecutables =
        [
            Path.Combine(normalizedDirectory, "app", InstallerPaths.ProductExecutableName),
            Path.Combine(normalizedDirectory, "runtime", InstallerPaths.ProductExecutableName)
        ];
        TimeSpan waitTimeout = timeout ?? TimeSpan.FromSeconds(8);

        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstallerPaths.ProductExecutableName)))
        {
            using (process)
            {
                string? path = TryGetProcessPath(process);
                if (!string.Equals(path, expectedExecutable, StringComparison.OrdinalIgnoreCase) &&
                    !legacyExecutables.Any(candidate => string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (process.HasExited)
                {
                    continue;
                }

                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    process.CloseMainWindow();
                }

                if (!process.WaitForExit((int)waitTimeout.TotalMilliseconds))
                {
                    throw new InvalidOperationException(
                        "ZSnaper 仍在运行，请先从托盘菜单退出后再继续安装或更新");
                }
            }
        }
    }

    public static void WaitForProcessExit(int processId, TimeSpan timeout)
    {
        if (processId <= 0 || processId == Environment.ProcessId)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch
        {
            // The process may already have exited.
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
