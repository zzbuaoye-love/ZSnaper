using System.Reflection;
using ZSnaper.Services;

namespace ZSnaper.Helpers;

public static class AppVersionInfo
{
    public const string Version = "0.0.6";

    // User preference: which update channel should be checked.
    public static string Channel => ConfigService.Current.UpdateChannel;

    // Actual channel encoded in this build. It must not depend on the update preference.
    public static string BuildChannel => ResolveBuildChannel();

    public static bool IsReleaseBuild =>
        string.Equals(BuildChannel, "Release", StringComparison.OrdinalIgnoreCase);

    public static string? WelcomeChannelLabel => IsReleaseBuild ? null : "BETA";

    public const string BuildNumber = "20260925.1";
    public const string BuildDate = "2026-09-25";
    public const int BuildCount = 1;

    public static bool ShowChannel => !IsReleaseBuild;

    public static string DisplayVersion =>
        typeof(AppVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?.Split('+', 2)[0]
        ?? (IsReleaseBuild ? Version : $"{Version}-beta");

    private static string ResolveBuildChannel()
    {
        Assembly assembly = typeof(AppVersionInfo).Assembly;
        string informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? Version;

        string versionWithoutMetadata = informationalVersion.Split('+', 2)[0];
        int separatorIndex = versionWithoutMetadata.IndexOf('-');
        if (separatorIndex < 0)
        {
            return "Release";
        }

        return "Beta";
    }
}
