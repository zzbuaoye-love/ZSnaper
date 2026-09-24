namespace ZSnaper.Plugins;

/// <summary>
/// Plugin storage locations. Directories are created only when installing a
/// package or opening the installed-plugin directory.
/// </summary>
public static class PluginStorage
{
    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ZSnaper",
        "Plugins");

    public static string InstalledDirectory => Path.Combine(RootDirectory, "installed");

    public static string StagingDirectory => Path.Combine(RootDirectory, "staging");

    public static string QuarantineDirectory => Path.Combine(RootDirectory, "quarantine");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(InstalledDirectory);
        Directory.CreateDirectory(StagingDirectory);
        Directory.CreateDirectory(QuarantineDirectory);
    }
}
