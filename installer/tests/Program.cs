using ZSnaper.Installer.Core;

namespace ZSnaper.Installer.Smoke;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: ZSnaper.Installer.Smoke <setup.exe> <update.zup>");
            return 2;
        }

        string setupPath = Path.GetFullPath(args[0]);
        string updatePath = Path.GetFullPath(args[1]);
        if (!PayloadArchive.TryReadPayloadRange(setupPath, out long offset, out long length) || length <= 0)
        {
            throw new InvalidDataException("The setup payload footer could not be read.");
        }

        string extracted = PayloadArchive.ExtractEmbeddedPayload(setupPath);
        try
        {
            if (!File.Exists(InstallerPaths.GetProductExecutablePath(extracted)))
            {
                throw new InvalidDataException("The embedded payload does not contain ZSnaper.exe.");
            }

            if (!File.Exists(InstallerPaths.GetUpdateExecutablePath(extracted)))
            {
                throw new InvalidDataException("The embedded payload does not contain update\\Update.exe.");
            }

            if (File.Exists(InstallerPaths.GetSetupExecutablePath(extracted)))
            {
                throw new InvalidDataException("The embedded payload contains an unnecessary full setup executable.");
            }

            if (!Directory.Exists(Path.Combine(extracted, "langs", "zh-Hans")) ||
                Directory.Exists(Path.Combine(extracted, "zh-Hans")))
            {
                throw new InvalidDataException("The embedded language packs are not organized under langs.");
            }
        }
        finally
        {
            PayloadArchive.TryDeleteDirectory(extracted);
        }

        UpdateManifest manifest = new UpdatePackageService().ReadManifest(updatePath);
        if (!string.Equals(manifest.Format, "zsnaper-update-1", StringComparison.Ordinal) ||
            !string.Equals(manifest.From, "0.0.5-beta", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.To, "0.0.6-beta", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update manifest did not pass the smoke test.");
        }
        if (!manifest.Delete.Contains("update/ZSnaper-Setup.exe", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update does not remove the legacy full setup executable.");
        }

        Console.WriteLine($"Smoke test passed. Payload offset={offset}, length={length}, changed={manifest.Files.Count}, deleted={manifest.Delete.Count}.");
        return 0;
    }
}
