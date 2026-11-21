using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ZSnaper.Helpers;

namespace ZSnaper.Update;

/// <summary>
/// GitHub Release 资产文件信息
/// </summary>
public class GitHubReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("download_count")]
    public int DownloadCount { get; set; }

    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = string.Empty;
}

/// <summary>
/// GitHub Release 发布版本信息
/// </summary>
public class GitHubRelease
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("prerelease")]
    public bool IsPrerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool IsDraft { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAsset> Assets { get; set; } = [];

    /// <summary>
    /// 解析提取出的纯语义化版本字符串（如 "0.0.1"）
    /// </summary>
    [JsonIgnore]
    public string CleanVersion => VersionGet.ExtractVersionString(TagName);
}

/// <summary>
/// 检查更新结果模型
/// </summary>
public class UpdateCheckResult
{
    /// <summary>
    /// 是否成功从 GitHub 获取到信息
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// 是否检测到新版本
    /// </summary>
    public bool HasUpdate { get; set; }

    /// <summary>
    /// 当前本地版本
    /// </summary>
    public string CurrentVersion { get; set; } = string.Empty;

    /// <summary>
    /// 当前发布通道
    /// </summary>
    public string CurrentChannel { get; set; } = string.Empty;

    /// <summary>
    /// 远端最新版本信息
    /// </summary>
    public GitHubRelease? LatestRelease { get; set; }

    /// <summary>
    /// 错误信息（若请求失败）
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// 云端 GitHub 版本获取与更新检测服务
/// </summary>
public static class VersionGet
{
    private const string RepoOwner = "zzbuaoye-love";
    private const string RepoName = "ZSnaper";
    private const string ApiBaseUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        // GitHub API 严格要求必须携带 User-Agent 请求头，否则会返回 403 Forbidden
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZSnaper-App", AppVersionInfo.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        return client;
    }

    /// <summary>
    /// 检查软件是否有新版本（自动根据当前渠道判断是否包含 Beta 预发布测试版本）
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>检查结果</returns>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        string currentVer = AppVersionInfo.Version;
        string channel = AppVersionInfo.Channel;

        // 若当前处于 Beta 等测试发布通道，则检索包括 Pre-release 在内的最新版本
        bool includePrerelease = !string.Equals(channel, "Release", StringComparison.OrdinalIgnoreCase);

        try
        {
            var latestRelease = await GetLatestReleaseAsync(includePrerelease, cancellationToken);
            if (latestRelease is null)
            {
                return new UpdateCheckResult
                {
                    IsSuccess = true,
                    HasUpdate = false,
                    CurrentVersion = currentVer,
                    CurrentChannel = channel,
                    ErrorMessage = "未在 GitHub 上找到可用的发布版本"
                };
            }

            bool hasUpdate = IsNewerAppRelease(latestRelease, AppVersionInfo.DisplayVersion);

            return new UpdateCheckResult
            {
                IsSuccess = true,
                HasUpdate = hasUpdate,
                CurrentVersion = currentVer,
                CurrentChannel = channel,
                LatestRelease = latestRelease
            };
        }
        catch (HttpRequestException ex)
        {
            return new UpdateCheckResult
            {
                IsSuccess = false,
                HasUpdate = false,
                CurrentVersion = currentVer,
                CurrentChannel = channel,
                ErrorMessage = $"网络请求失败: {ex.Message}"
            };
        }
        catch (TaskCanceledException)
        {
            return new UpdateCheckResult
            {
                IsSuccess = false,
                HasUpdate = false,
                CurrentVersion = currentVer,
                CurrentChannel = channel,
                ErrorMessage = "检查更新超时，请检查网络连接"
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                IsSuccess = false,
                HasUpdate = false,
                CurrentVersion = currentVer,
                CurrentChannel = channel,
                ErrorMessage = $"获取版本信息异常: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// 获取最新的 GitHub Release
    /// </summary>
    /// <param name="includePrerelease">是否包含测试预发布版本（Beta）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task<GitHubRelease?> GetLatestReleaseAsync(bool includePrerelease = true, CancellationToken cancellationToken = default)
    {
        if (!includePrerelease)
        {
            // 直接请求官方 latest 节点（只返回正式发布版本）
            string url = $"{ApiBaseUrl}/releases/latest";
            using var response = await HttpClient.GetAsync(url, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            GitHubRelease? release = JsonSerializer.Deserialize<GitHubRelease>(json);
            return release is null ? null : SelectAppRelease([release]);
        }

        // 获取全部 Releases 列表，查找最新的有效非草稿版本
        var allReleases = await GetAllReleasesAsync(cancellationToken);
        return SelectAppRelease(allReleases);
    }

    internal static GitHubRelease? SelectAppRelease(IEnumerable<GitHubRelease> releases) =>
        releases.FirstOrDefault(release => !release.IsDraft && Regex.IsMatch(
            release.TagName, @"^v?\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.IgnoreCase));

    internal static bool IsNewerAppRelease(GitHubRelease release, string installedVersion)
    {
        int comparison = CompareVersions(release.CleanVersion, installedVersion);
        return comparison > 0 ||
               (comparison == 0 && !release.IsPrerelease && installedVersion.Contains('-'));
    }

    /// <summary>
    /// 获取 GitHub 所有 Releases 列表
    /// </summary>
    public static async Task<List<GitHubRelease>> GetAllReleasesAsync(CancellationToken cancellationToken = default)
    {
        string url = $"{ApiBaseUrl}/releases?per_page=15";
        using var response = await HttpClient.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<GitHubRelease>>(json) ?? [];
    }

    /// <summary>
    /// 从 Tag 或版本字符串中提取纯数字版本（例如 "v0.0.1-alpha" -> "0.0.1"）
    /// </summary>
    public static string ExtractVersionString(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "0.0.0";
        }

        var match = Regex.Match(input, @"\d+(\.\d+)+");
        return match.Success ? match.Value : input.TrimStart('v', 'V');
    }

    /// <summary>
    /// 比较两个版本号大小
    /// </summary>
    /// <returns>
    /// 大于 0：v1 > v2 (有新版本)；
    /// 等于 0：v1 == v2；
    /// 小于 0：v1 < v2
    /// </returns>
    public static int CompareVersions(string v1, string v2)
    {
        string clean1 = ExtractVersionString(v1);
        string clean2 = ExtractVersionString(v2);

        if (Version.TryParse(clean1, out var parsed1) && Version.TryParse(clean2, out var parsed2))
        {
            return parsed1.CompareTo(parsed2);
        }

        return string.Compare(clean1, clean2, StringComparison.OrdinalIgnoreCase);
    }
}
