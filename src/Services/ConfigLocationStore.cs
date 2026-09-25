namespace ZSnaper.Services;

internal static class ConfigLocationStore
{
    public static ConfigStorageLocation Resolve(string applicationModeMarker) =>
        File.Exists(applicationModeMarker)
            ? ConfigStorageLocation.ApplicationDirectory
            : ConfigStorageLocation.UserData;

    public static void Switch(
        ConfigStorageLocation location,
        string destinationPath,
        string applicationModeMarker,
        string contents)
    {
        ConfigFileStore.WriteAtomic(destinationPath, contents, destinationPath + ".bak", backupExisting: true);
        if (location == ConfigStorageLocation.ApplicationDirectory)
        {
            ConfigFileStore.WriteAtomic(applicationModeMarker, "app-directory", null, backupExisting: false);
        }
        else if (File.Exists(applicationModeMarker))
        {
            File.Delete(applicationModeMarker);
        }
    }
}
