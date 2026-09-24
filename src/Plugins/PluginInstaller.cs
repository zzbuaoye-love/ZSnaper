using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ZSnaper.Helpers;

namespace ZSnaper.Plugins;

public sealed record InstalledPlugin(PluginManifest Manifest, string Directory, bool Enabled, string Sha256)
{
    public bool Compatible { get; init; } = true;
}

/// <summary>Installs inspected packages without executing their assemblies.</summary>
public sealed class PluginInstaller
{
    private const long MaxExtractedBytes = 512L * 1024 * 1024;
    private readonly string _root;
    private string InstalledPath => Path.Combine(_root, "installed");
    private string StagingPath => Path.Combine(_root, "staging");
    private string StatePath => Path.Combine(_root, "state.json");

    public PluginInstaller(string? root = null) => _root = Path.GetFullPath(root ?? PluginStorage.RootDirectory);

    public IReadOnlyList<InstalledPlugin> List()
    {
        if (!Directory.Exists(InstalledPath)) return [];
        Dictionary<string, bool> states = ReadStates();
        var plugins = new List<InstalledPlugin>();
        foreach (string directory in Directory.EnumerateDirectories(InstalledPath))
        {
            string manifestPath = Path.Combine(directory, PluginContract.ManifestFileName);
            if (!File.Exists(manifestPath)) continue;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                PluginManifest manifest = PluginManifestService.Load(manifestPath);
                if (!string.Equals(manifest.Id, Path.GetFileName(directory), StringComparison.OrdinalIgnoreCase)) continue;
                plugins.Add(new InstalledPlugin(manifest, directory,
                    states.GetValueOrDefault(manifest.Id), ReadHash(directory))
                {
                    Compatible = PluginManifestService.Validate(manifest, AppVersionInfo.Version).Count == 0
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A broken installation must not hide other plugins.
            }
        }
        return plugins.OrderBy(plugin => plugin.Manifest.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public InstalledPlugin Install(string packagePath)
    {
        PluginPackageInspection inspection = PluginPackageService.Inspect(packagePath, AppVersionInfo.Version);
        if (!inspection.IsValid || inspection.Manifest is null)
            throw new InvalidDataException(string.Join("; ", inspection.Errors));

        PluginManifest manifest = inspection.Manifest;
        string target = Path.Combine(InstalledPath, manifest.Id);
        if (Directory.Exists(target)) throw new IOException($"插件 {manifest.Id} 已安装。");

        Directory.CreateDirectory(InstalledPath);
        Directory.CreateDirectory(StagingPath);
        string staging = Path.Combine(StagingPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            long extracted = 0;
            using ZipArchive archive = ZipFile.OpenRead(inspection.PackagePath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                string relative = entry.FullName.Replace('\\', '/');
                if (!PluginPackageService.IsSafeEntryName(relative))
                    throw new InvalidDataException($"不安全的插件文件路径：{entry.FullName}");
                string output = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!output.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("插件文件超出安装目录。");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using Stream input = entry.Open();
                using FileStream destination = new(output, FileMode.CreateNew, FileAccess.Write);
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    if (read > MaxExtractedBytes - extracted)
                        throw new InvalidDataException("插件解压大小超过限制。");
                    extracted += read;
                    destination.Write(buffer, 0, read);
                }
                if (destination.Length != entry.Length)
                    throw new InvalidDataException($"插件文件大小不匹配：{relative}");
            }

            File.WriteAllText(Path.Combine(staging, ".zsnaper-sha256"), GetSha256(inspection.PackagePath));
            Directory.Move(staging, target);
            return new InstalledPlugin(manifest, target, false, ReadHash(target));
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public void SetEnabled(string pluginId, bool enabled)
    {
        InstalledPlugin? plugin = List().FirstOrDefault(item =>
            string.Equals(item.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (plugin is null || enabled && !plugin.Compatible)
            throw new InvalidOperationException($"插件 {pluginId} 未安装或不兼容。");
        Dictionary<string, bool> states = ReadStates();
        states[pluginId] = enabled;
        WriteStates(states);
    }

    public void Remove(string pluginId)
    {
        InstalledPlugin plugin = List().FirstOrDefault(item =>
            string.Equals(item.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"插件 {pluginId} 未安装。");
        string installedRoot = Path.GetFullPath(InstalledPath) + Path.DirectorySeparatorChar;
        if (!plugin.Directory.StartsWith(installedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("插件目录无效。");
        if ((File.GetAttributes(plugin.Directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("插件目录是符号链接或联接点。");
        Directory.Delete(plugin.Directory, recursive: true);
        Dictionary<string, bool> states = ReadStates();
        states.Remove(pluginId);
        WriteStates(states);
    }

    private Dictionary<string, bool> ReadStates()
    {
        if (!File.Exists(StatePath)) return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(StatePath));
            return data is null
                ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WriteStates(Dictionary<string, bool> states)
    {
        Directory.CreateDirectory(_root);
        string temp = Path.Combine(_root, "state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(states));
            File.Move(temp, StatePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string GetSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ReadHash(string directory)
    {
        string path = Path.Combine(directory, ".zsnaper-sha256");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
    }
}
