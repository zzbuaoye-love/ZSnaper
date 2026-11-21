using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ZSnaper.Installer.Core;

public sealed class UpdatePackageService
{
    private const string ManifestName = "update.manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly InstallerService _installerService;

    public UpdatePackageService(InstallerService? installerService = null)
    {
        _installerService = installerService ?? new InstallerService();
    }

    public UpdateManifest ReadManifest(string packagePath)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("Update package was not found.", packagePath);
        }

        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry entry = archive.GetEntry(ManifestName)
            ?? throw new InvalidDataException("The update package has no update.manifest.json.");
        using Stream stream = entry.Open();
        UpdateManifest? manifest = JsonSerializer.Deserialize<UpdateManifest>(stream, JsonOptions);
        if (manifest is null)
        {
            throw new InvalidDataException("The update manifest is empty.");
        }

        ValidateManifest(manifest);
        return manifest;
    }

    public void Apply(
        string packagePath,
        InstallationInfo installation,
        IProgress<InstallProgress>? progress = null,
        bool updateInstalledVersion = true)
    {
        UpdateManifest manifest = ReadManifest(packagePath);
        if (!string.IsNullOrWhiteSpace(manifest.From) &&
            !string.Equals(manifest.From, installation.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"This package targets {manifest.From}, but the installed version is {installation.Version}. Use the full installer.");
        }

        if (!File.Exists(Path.ChangeExtension(installation.ExecutablePath, ".dll")))
        {
            throw new InvalidOperationException("差分更新仅适用于完整安装目录，请下载新版便携包或完整安装包");
        }

        string extractionDirectory = PayloadArchive.CreateUpdatePayloadTempDirectory();
        string backupDirectory = extractionDirectory + "-backup";
        Dictionary<string, string?> backups = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            Directory.CreateDirectory(extractionDirectory);
            ZipFile.ExtractToDirectory(packagePath, extractionDirectory, overwriteFiles: true);
            ProcessGuard.EnsureClosed(installation.InstallDirectory);

            int total = manifest.Files.Count + manifest.Delete.Count;
            int completed = 0;
            foreach (UpdateFileEntry file in manifest.Files)
            {
                string sourcePath = GetSafePath(extractionDirectory, file.Path);
                string targetPath = GetSafePath(installation.InstallDirectory, file.Path);
                if (!File.Exists(sourcePath))
                {
                    throw new InvalidDataException($"The update package is missing {file.Path}.");
                }

                VerifyFile(sourcePath, file.Sha256, file.Size, file.Path);
                BackupOnce(targetPath, installation.InstallDirectory, backupDirectory, backups);
                string? targetDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                string temporaryPath = targetPath + ".updating-" + Guid.NewGuid().ToString("N");
                File.Copy(sourcePath, temporaryPath, overwrite: true);
                File.Move(temporaryPath, targetPath, overwrite: true);
                completed++;
                progress?.Report(new InstallProgress("Replacing update files", completed, total));
            }

            foreach (string relativePath in manifest.Delete)
            {
                string targetPath = GetSafePath(installation.InstallDirectory, relativePath);
                BackupOnce(targetPath, installation.InstallDirectory, backupDirectory, backups);
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                completed++;
                progress?.Report(new InstallProgress("Removing obsolete files", completed, total));
            }

            VerifyInstalledFiles(installation.InstallDirectory, manifest);
            _installerService.OrganizeInstallation(installation.InstallDirectory, updateInstalledVersion);
            if (updateInstalledVersion && InstallerService.IsRegisteredInstall(installation.InstallDirectory))
            {
                _installerService.UpdateInstalledVersion(manifest.To);
            }
        }
        catch
        {
            Rollback(backupDirectory, backups);
            throw;
        }
        finally
        {
            PayloadArchive.TryDeleteDirectory(extractionDirectory);
            PayloadArchive.TryDeleteDirectory(backupDirectory);
        }
    }

    private static void ValidateManifest(UpdateManifest manifest)
    {
        if (!string.Equals(manifest.Format, "zsnaper-update-1", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unsupported update package format.");
        }

        if (string.IsNullOrWhiteSpace(manifest.To))
        {
            throw new InvalidDataException("The update manifest has no target version.");
        }

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (UpdateFileEntry file in manifest.Files)
        {
            ValidateRelativePath(file.Path);
            if (!paths.Add(file.Path))
            {
                throw new InvalidDataException($"The update manifest contains a duplicate path: {file.Path}.");
            }

            if (file.Size < 0 || string.IsNullOrWhiteSpace(file.Sha256) || file.Sha256.Length != 64)
            {
                throw new InvalidDataException($"Invalid file metadata for {file.Path}.");
            }
        }

        foreach (string relativePath in manifest.Delete)
        {
            ValidateRelativePath(relativePath);
            if (!paths.Add(relativePath))
            {
                throw new InvalidDataException($"The update manifest processes a path twice: {relativePath}.");
            }
        }
    }

    private static void VerifyInstalledFiles(string installDirectory, UpdateManifest manifest)
    {
        foreach (UpdateFileEntry file in manifest.Files)
        {
            string targetPath = GetSafePath(installDirectory, file.Path);
            if (!File.Exists(targetPath))
            {
                throw new IOException($"The updated installation is missing {file.Path}.");
            }

            VerifyFile(targetPath, file.Sha256, file.Size, file.Path);
        }
    }

    private static void VerifyFile(string path, string expectedHash, long expectedSize, string relativePath)
    {
        if (new FileInfo(path).Length != expectedSize)
        {
            throw new InvalidDataException($"File size verification failed for {relativePath}.");
        }

        using FileStream stream = File.OpenRead(path);
        string actualHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Checksum verification failed for {relativePath}.");
        }
    }

    private static void BackupOnce(
        string targetPath,
        string installDirectory,
        string backupDirectory,
        IDictionary<string, string?> backups)
    {
        if (backups.ContainsKey(targetPath))
        {
            return;
        }

        if (!File.Exists(targetPath))
        {
            backups[targetPath] = null;
            return;
        }

        string relativePath = Path.GetRelativePath(installDirectory, targetPath);
        string backupPath = GetSafePath(backupDirectory, relativePath);
        string? backupParent = Path.GetDirectoryName(backupPath);
        if (!string.IsNullOrWhiteSpace(backupParent))
        {
            Directory.CreateDirectory(backupParent);
        }

        File.Copy(targetPath, backupPath, overwrite: true);
        backups[targetPath] = backupPath;
    }

    private static void Rollback(
        string backupDirectory,
        IReadOnlyDictionary<string, string?> backups)
    {
        foreach ((string targetPath, string? backupPath) in backups)
        {
            try
            {
                if (backupPath is null)
                {
                    if (File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                    }
                }
                else if (File.Exists(backupPath))
                {
                    string? targetDirectory = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrWhiteSpace(targetDirectory))
                    {
                        Directory.CreateDirectory(targetDirectory);
                    }

                    File.Copy(backupPath, targetPath, overwrite: true);
                }
            }
            catch
            {
                // Preserve the original exception; cleanup follows in finally.
            }
        }
    }

    private static string GetSafePath(string root, string relativePath)
    {
        ValidateRelativePath(relativePath);
        string normalizedRoot = InstallerPaths.Normalize(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The update path escapes its root: {relativePath}.");
        }

        return fullPath;
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Path.IsPathRooted(path) ||
            path.Contains('\0') ||
            path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part is "." or ".."))
        {
            throw new InvalidDataException($"Invalid update path: {path}.");
        }
    }
}
