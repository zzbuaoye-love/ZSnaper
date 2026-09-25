namespace ZSnaper.Installer.Core;

public sealed record InstallOptions(
    string InstallDirectory,
    string Version,
    bool CreateDesktopShortcut,
    bool CreateStartMenuShortcut,
    bool EnableAutoStart,
    bool ApplyOptionalSettings = true);

public sealed record InstallationInfo(
    string InstallDirectory,
    string Version,
    string ExecutablePath,
    string UninstallExecutablePath);

public sealed record InstallProgress(string Stage, int Completed, int Total);

public sealed record UpdateFileEntry(string Path, string Sha256, long Size);

public sealed class UpdateManifest
{
    public string Format { get; set; } = "zsnaper-update-1";
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public List<UpdateFileEntry> Files { get; set; } = [];
    public List<string> Delete { get; set; } = [];
}
