using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ZSnaper.Plugins;

public sealed record PluginPackageInspection(
    string PackagePath,
    PluginManifest? Manifest,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Errors,
    long UncompressedSize)
{
    public bool IsValid => Manifest is not null && Errors.Count == 0;
}

/// <summary>
/// Reads and validates a .zsp package without extracting, loading, or
/// executing plugin code. Installation and execution are separate steps.
/// </summary>
public static class PluginPackageService
{
    private const int MaxFileCount = 4096;
    private const long MaxUncompressedSize = 512L * 1024 * 1024;
    private const long MaxManifestSize = 1024 * 1024;

    public static PluginPackageInspection Inspect(
        string packagePath,
        string appVersion,
        string apiVersion = PluginContract.ApiVersion)
    {
        string normalizedPath = Path.GetFullPath(packagePath);
        var files = new List<string>();
        var errors = new List<string>();
        PluginManifest? manifest = null;
        long uncompressedSize = 0;

        if (!File.Exists(normalizedPath))
        {
            errors.Add("Plugin package was not found.");
            return new PluginPackageInspection(normalizedPath, null, files, errors, 0);
        }

        if (!string.Equals(Path.GetExtension(normalizedPath), PluginContract.PackageExtension, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Plugin package must use {PluginContract.PackageExtension}.");
            return new PluginPackageInspection(normalizedPath, null, files, errors, 0);
        }

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(normalizedPath);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry? manifestEntry = null;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (IsDirectoryEntry(entry))
                {
                    continue;
                }

                string normalizedEntryName = NormalizeEntryName(entry.FullName);
                if (!IsSafeEntryName(normalizedEntryName))
                {
                    errors.Add($"Package path escapes the package root: {entry.FullName}");
                    continue;
                }

                if (!names.Add(normalizedEntryName))
                {
                    errors.Add($"Package contains a duplicate file: {normalizedEntryName}");
                    continue;
                }

                files.Add(normalizedEntryName);
                if (entry.Length < 0 || entry.Length > MaxUncompressedSize - uncompressedSize)
                {
                    errors.Add("Package uncompressed size exceeds the safety limit.");
                    break;
                }

                uncompressedSize += entry.Length;
                if (files.Count > MaxFileCount)
                {
                    errors.Add("Package contains too many files.");
                    break;
                }

                if (string.Equals(normalizedEntryName, PluginContract.ManifestFileName, StringComparison.OrdinalIgnoreCase))
                {
                    manifestEntry = entry;
                }
            }

            if (manifestEntry is null)
            {
                errors.Add($"Package is missing root {PluginContract.ManifestFileName}.");
            }
            else
            {
                if (manifestEntry.Length > MaxManifestSize)
                {
                    errors.Add("Plugin manifest is too large.");
                    return new PluginPackageInspection(normalizedPath, null, files, errors, uncompressedSize);
                }
                using Stream stream = manifestEntry.Open();
                using MemoryStream content = new();
                byte[] buffer = new byte[16 * 1024];
                int read;
                while ((read = stream.Read(buffer)) != 0)
                {
                    if (read > MaxManifestSize - content.Length)
                        throw new InvalidDataException("Plugin manifest is too large.");
                    content.Write(buffer, 0, read);
                }
                manifest = PluginManifestJson.Deserialize(
                    Encoding.UTF8.GetString(content.ToArray()).TrimStart('\uFEFF'));
                errors.AddRange(PluginManifestService.Validate(manifest, appVersion, apiVersion));
                if (manifest.Entry is not null)
                {
                    ValidateEntryAssembly(manifest, files, errors);
                }
            }
        }
        catch (InvalidDataException exception)
        {
            errors.Add($"Invalid plugin package: {exception.Message}");
        }
        catch (IOException exception)
        {
            errors.Add($"Unable to read plugin package: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            errors.Add($"Unable to access plugin package: {exception.Message}");
        }
        catch (System.Text.Json.JsonException exception)
        {
            errors.Add($"Plugin manifest is invalid: {exception.Message}");
        }

        return new PluginPackageInspection(normalizedPath, manifest, files, errors, uncompressedSize);
    }

    public static bool VerifySha256(string packagePath, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64 || !File.Exists(packagePath))
        {
            return false;
        }

        using FileStream stream = File.OpenRead(packagePath);
        string actualSha256 = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateEntryAssembly(PluginManifest manifest, IReadOnlyCollection<string> files, ICollection<string> errors)
    {
        string assemblyPath = NormalizeEntryName(manifest.Entry.Assembly);
        if (!IsSafeEntryName(assemblyPath) || !assemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("entry.assembly must be a safe relative .dll path.");
            return;
        }

        if (!files.Contains(assemblyPath, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"entry.assembly is not present in the package: {manifest.Entry.Assembly}");
        }
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
        entry.FullName.EndsWith("\\", StringComparison.Ordinal);

    private static string NormalizeEntryName(string entryName) =>
        entryName.Replace('\\', '/');

    internal static bool IsSafeEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) ||
            entryName.Contains('\0') ||
            entryName.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(entryName.Replace('/', Path.DirectorySeparatorChar)))
        {
            return false;
        }

        string[] parts = entryName.Split('/');
        return parts.Length > 0 &&
               parts.All(part =>
                   !string.IsNullOrWhiteSpace(part) &&
                   part is not "." and not ".." &&
                   !part.EndsWith(' ') && !part.EndsWith('.') &&
                   part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                   !IsReservedWindowsName(part));
    }

    private static bool IsReservedWindowsName(string part)
    {
        string stem = part.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               stem.Length == 4 && stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && stem[3] is >= '1' and <= '9' ||
               stem.Length == 4 && stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase) && stem[3] is >= '1' and <= '9';
    }
}
