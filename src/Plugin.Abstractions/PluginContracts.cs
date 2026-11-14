using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZSnaper.Plugins;

/// <summary>
/// ZSnaper 插件包、清单和 Host API 的稳定协议常量
/// </summary>
public static class PluginContract
{
    public const int ManifestVersion = 1;
    public const string ApiVersion = "1.1.0";
    public const string PackageExtension = ".zsp";
    public const string ManifestFileName = "manifest.json";
}

/// <summary>
/// .zsp 包根目录中的 manifest.json。
/// </summary>
public sealed class PluginManifest
{
    [JsonPropertyName("manifestVersion")]
    public int ManifestVersion { get; set; } = PluginContract.ManifestVersion;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("entry")]
    public PluginEntryPoint Entry { get; set; } = new();

    [JsonPropertyName("lifecycle")]
    public PluginLifecycleMetadata Lifecycle { get; set; } = new();

    [JsonPropertyName("scope")]
    public PluginScopeMetadata Scope { get; set; } = new();

    [JsonPropertyName("requires")]
    public PluginRequirements Requires { get; set; } = new();

    [JsonPropertyName("update")]
    public PluginUpdateMetadata? Update { get; set; }
}

public sealed class PluginEntryPoint
{
    [JsonPropertyName("assembly")]
    public string Assembly { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

public sealed class PluginLifecycleMetadata
{
    [JsonPropertyName("load")]
    public string Load { get; set; } = "app_start";

    [JsonPropertyName("enable")]
    public string Enable { get; set; } = "manual_or_app_start";

    [JsonPropertyName("disable")]
    public string Disable { get; set; } = "manual_or_app_exit";

    [JsonPropertyName("unload")]
    public string Unload { get; set; } = "app_exit";
}

public sealed class PluginScopeMetadata
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "app";

    [JsonPropertyName("events")]
    public List<string> Events { get; set; } = [];

    [JsonPropertyName("runAt")]
    public string RunAt { get; set; } = "event";
}

public sealed class PluginRequirements
{
    [JsonPropertyName("hostApis")]
    public List<string> HostApis { get; set; } = [];

    [JsonPropertyName("pluginApi")]
    public string PluginApi { get; set; } = ">=1.0.0 <2.0.0";

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = ">=0.0.0";
}

public sealed class PluginUpdateMetadata
{
    [JsonPropertyName("checkUrl")]
    public string CheckUrl { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "stable";

    [JsonPropertyName("autoCheck")]
    public bool AutoCheck { get; set; } = true;
}

/// <summary>
/// 插件更新检查 API 返回的数据格式。
/// </summary>
public sealed class PluginUpdateInfo
{
    [JsonPropertyName("pluginId")]
    public string PluginId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = ">=0.0.0";

    [JsonPropertyName("pluginApi")]
    public string PluginApi { get; set; } = ">=1.0.0 <2.0.0";

    [JsonPropertyName("packageUrl")]
    public string PackageUrl { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("releaseNotes")]
    public string ReleaseNotes { get; set; } = string.Empty;
}

public sealed class PluginHostInfo
{
    public string AppName { get; init; } = "ZSnaper";
    public string AppVersion { get; init; } = string.Empty;
    public string PluginApiVersion { get; init; } = PluginContract.ApiVersion;
}

public interface IZSnaperPlugin
{
    PluginManifest Manifest { get; }

    ValueTask InitializeAsync(IZSnaperHost host, CancellationToken cancellationToken = default);

    ValueTask EnableAsync(CancellationToken cancellationToken = default);

    ValueTask DisableAsync(CancellationToken cancellationToken = default);

    ValueTask ShutdownAsync(CancellationToken cancellationToken = default);
}

public interface IZSnaperHost
{
    PluginHostInfo Info { get; }
    IZSnaperCaptureApi Capture { get; }
    IZSnaperOcrApi Ocr { get; }
    IZSnaperToolbarApi Toolbar { get; }
    IPluginLogger Logger { get; }
}

public sealed class CaptureSnapshot
{
    public CaptureSnapshot(
        ReadOnlyMemory<byte> pngBytes,
        int width,
        int height,
        DateTimeOffset capturedAt,
        string source = "screen")
    {
        PngBytes = pngBytes;
        Width = width;
        Height = height;
        CapturedAt = capturedAt;
        Source = source;
    }

    public ReadOnlyMemory<byte> PngBytes { get; }
    public int Width { get; }
    public int Height { get; }
    public DateTimeOffset CapturedAt { get; }
    public string Source { get; }
}

public sealed class CaptureCompletedEventArgs(CaptureSnapshot snapshot, string completionAction) : EventArgs
{
    public CaptureSnapshot Snapshot { get; } = snapshot;
    public string CompletionAction { get; } = completionAction;
}

public interface IZSnaperCaptureApi
{
    event EventHandler<CaptureCompletedEventArgs>? Completed;

    CaptureSnapshot? Latest { get; }

    ValueTask<bool> CopyAsync(CaptureSnapshot snapshot, CancellationToken cancellationToken = default);

    ValueTask<string?> SaveAsync(
        CaptureSnapshot snapshot,
        string? fileName = null,
        CancellationToken cancellationToken = default);
}

public interface IZSnaperOcrApi
{
    ValueTask<string> RecognizeAsync(
        CaptureSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

public enum PluginToolbarSlot
{
    CaptureCompleted
}

public sealed class PluginToolbarItemDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public string Tooltip { get; init; } = string.Empty;
    public int Order { get; init; }
}

public sealed class PluginToolbarActionContext
{
    public required CaptureSnapshot Snapshot { get; init; }
    public required string PluginId { get; init; }
}

public delegate ValueTask PluginToolbarActionHandler(
    PluginToolbarActionContext context,
    CancellationToken cancellationToken);

public sealed record PluginToolbarRegistration(string Id);

public interface IZSnaperToolbarApi
{
    PluginToolbarRegistration Register(
        PluginToolbarSlot slot,
        PluginToolbarItemDefinition item,
        PluginToolbarActionHandler handler);

    bool Unregister(string registrationId);
}

public enum PluginLogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public interface IPluginLogger
{
    void Log(PluginLogLevel level, string message, Exception? exception = null);
}

public static class PluginManifestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static PluginManifest Deserialize(string json) =>
        JsonSerializer.Deserialize<PluginManifest>(json, Options)
        ?? throw new JsonException("插件 manifest.json 为空或格式无效");

    public static string Serialize(PluginManifest manifest) =>
        JsonSerializer.Serialize(manifest, Options);
}
