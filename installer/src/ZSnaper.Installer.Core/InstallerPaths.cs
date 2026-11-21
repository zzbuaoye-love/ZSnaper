using Microsoft.Win32;

namespace ZSnaper.Installer.Core;

public static class InstallerPaths
{
    public const string ProductName = "ZSnaper";
    public const string ProductExecutableName = "ZSnaper.exe";
    public const string SetupExecutableName = "ZSnaper-Setup.exe";
    public const string UpdateExecutableName = "Update.exe";
    public const string SupportDirectoryName = "update";
    public const string InstallerRegistryPath = @"Software\ZSnaper\Installer";
    public const string UninstallRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ZSnaper";
    public const string StartupRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupValueName = ProductName;

    public static string DefaultInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        ProductName);

    public static string StartMenuDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        "Programs",
        ProductName);

    public static string DesktopDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string GetProductExecutablePath(string installDirectory) =>
        Path.Combine(Normalize(installDirectory), ProductExecutableName);

    public static string GetSupportDirectory(string installDirectory) =>
        Path.Combine(Normalize(installDirectory), SupportDirectoryName);

    public static string GetSetupExecutablePath(string installDirectory) =>
        Path.Combine(GetSupportDirectory(installDirectory), SetupExecutableName);

    public static string GetUpdateExecutablePath(string installDirectory) =>
        Path.Combine(GetSupportDirectory(installDirectory), UpdateExecutableName);

    public static string Normalize(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));

    public static bool IsUsableInstallDirectory(string path, out string error)
    {
        error = string.Empty;
        try
        {
            string fullPath = Normalize(path);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root) ||
                string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                error = "安装目录不能是磁盘根目录";
                return false;
            }

            if (fullPath.Length > 240)
            {
                error = "安装目录路径过长";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "安装目录无效";
            return false;
        }
    }

    public static bool IsOwnedPath(string candidate, string installDirectory)
    {
        string normalizedCandidate = Normalize(candidate).TrimEnd(Path.DirectorySeparatorChar);
        string normalizedInstall = Normalize(installDirectory).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(normalizedCandidate, normalizedInstall, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(normalizedInstall + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static RegistryKey? OpenInstallerKey(bool writable) =>
        Registry.CurrentUser.OpenSubKey(InstallerRegistryPath, writable);
}
