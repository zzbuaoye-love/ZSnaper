using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace ZSnaper.Update;

public sealed record PreparedAppUpdate(string UpdaterPath, string PackagePath, string LogPath);

public sealed class AppUpdateService
{
    private static readonly HttpClient Client = CreateClient();

    internal static bool SupportsDifferentialUpdates =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "ZSnaper.dll"));

    public static GitHubReleaseAsset SelectPackageAsset(GitHubRelease release) =>
        SelectSingleAsset(release, name => name.EndsWith("-Update.zup", StringComparison.OrdinalIgnoreCase), ".zup update package");

    public static GitHubReleaseAsset SelectUpdaterAsset(GitHubRelease release) =>
        SelectSingleAsset(release, name => name.EndsWith("-Update.exe", StringComparison.OrdinalIgnoreCase), "update executable");

    public static GitHubReleaseAsset SelectChecksumAsset(GitHubRelease release) =>
        SelectSingleAsset(release, name => string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase), "checksum list");

    public async Task<PreparedAppUpdate> PrepareAsync(
        GitHubRelease release,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        GitHubReleaseAsset packageAsset = SelectPackageAsset(release);
        GitHubReleaseAsset updaterAsset = SelectUpdaterAsset(release);
        GitHubReleaseAsset checksumAsset = SelectChecksumAsset(release);
        ValidateDownloadUri(packageAsset.DownloadUrl);
        ValidateDownloadUri(updaterAsset.DownloadUrl);
        ValidateDownloadUri(checksumAsset.DownloadUrl);

        string version = SanitizePathPart(release.TagName);
        string updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZSnaper", "Updates", version);
        Directory.CreateDirectory(updateDirectory);

        string checksumPath = Path.Combine(updateDirectory, checksumAsset.Name);
        await DownloadAtomicAsync(checksumAsset.DownloadUrl, checksumPath, null, cancellationToken);
        IReadOnlyDictionary<string, string> hashes = ParseChecksums(await File.ReadAllTextAsync(checksumPath, cancellationToken));

        string packagePath = Path.Combine(updateDirectory, packageAsset.Name);
        string updaterPath = Path.Combine(updateDirectory, updaterAsset.Name);
        await DownloadAtomicAsync(packageAsset.DownloadUrl, packagePath, new Progress<long>(bytes =>
        {
            if (packageAsset.Size > 0) progress?.Report(Math.Clamp((int)(bytes * 80L / packageAsset.Size), 0, 80));
        }), cancellationToken);
        VerifyChecksum(packagePath, GetExpectedHash(hashes, packageAsset.Name));

        await DownloadAtomicAsync(updaterAsset.DownloadUrl, updaterPath, new Progress<long>(bytes =>
        {
            if (updaterAsset.Size > 0) progress?.Report(80 + Math.Clamp((int)(bytes * 20L / updaterAsset.Size), 0, 20));
        }), cancellationToken);
        VerifyChecksum(updaterPath, GetExpectedHash(hashes, updaterAsset.Name));
        progress?.Report(100);

        return new PreparedAppUpdate(
            updaterPath,
            packagePath,
            Path.Combine(updateDirectory, "update.log"));
    }

    public static void Launch(PreparedAppUpdate update)
    {
        string applicationDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string directoryName = Path.GetFileName(applicationDirectory);
        string installDirectory = string.Equals(directoryName, "runtime", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(directoryName, "app", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(applicationDirectory)?.FullName ?? applicationDirectory
            : applicationDirectory;
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = update.UpdaterPath,
            Arguments = string.Join(' ',
                "--silent",
                "--package", Quote(update.PackagePath),
                "--wait-for-pid", Environment.ProcessId,
                "--install-directory", Quote(installDirectory),
                "--installed-version", Quote(Helpers.AppVersionInfo.DisplayVersion),
                "--restart",
                "--log", Quote(update.LogPath)),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("Unable to start the update process.");
    }

    public static IReadOnlyDictionary<string, string> ParseChecksums(string content)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = line.IndexOfAny([' ', '\t']);
            if (separator <= 0) continue;
            string hash = line[..separator].Trim();
            string name = line[separator..].Trim().TrimStart('*');
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit) && !string.IsNullOrWhiteSpace(name))
            {
                result[name] = hash;
            }
        }
        return result;
    }

    private static GitHubReleaseAsset SelectSingleAsset(GitHubRelease release, Func<string, bool> predicate, string description)
    {
        GitHubReleaseAsset[] matches = release.Assets.Where(asset => predicate(asset.Name)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException($"Release {release.TagName} must contain exactly one {description}.");
    }

    private static string GetExpectedHash(IReadOnlyDictionary<string, string> hashes, string name) =>
        hashes.TryGetValue(name, out string? hash)
            ? hash
            : throw new InvalidDataException($"SHA256SUMS.txt does not contain {name}.");

    private static async Task DownloadAtomicAsync(
        string url,
        string destination,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        string temporary = destination + ".download";
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (FileStream target = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    total += read;
                    progress?.Report(total);
                }
                await target.FlushAsync(cancellationToken);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void VerifyChecksum(string path, string expectedHash)
    {
        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Checksum verification failed for {Path.GetFileName(path)}.");
        }
    }

    private static void ValidateDownloadUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Update assets must use HTTPS.");
        }
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZSnaper-App", Helpers.AppVersionInfo.Version));
        return client;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string SanitizePathPart(string value) =>
        string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
