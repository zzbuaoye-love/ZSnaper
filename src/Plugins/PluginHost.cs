using System.Drawing.Imaging;
using ZSnaper.Helpers;
using ZSnaper.Services;

namespace ZSnaper.Plugins;

internal sealed class PluginHost : IZSnaperHost, IDisposable
{
    public PluginHost(string id, CaptureSnapshot? latest, Action changed)
    {
        CaptureApi = new PluginCaptureApi(id, latest);
        ToolbarApi = new PluginToolbarApi(id, changed);
        Logger = new PluginLogger(id);
    }

    public PluginHostInfo Info { get; } = new() { AppVersion = AppVersionInfo.Version };
    public PluginCaptureApi CaptureApi { get; }
    public PluginToolbarApi ToolbarApi { get; }
    public IZSnaperCaptureApi Capture => CaptureApi;
    public IZSnaperOcrApi Ocr { get; } = new PluginOcrApi();
    public IZSnaperToolbarApi Toolbar => ToolbarApi;
    public IPluginLogger Logger { get; }

    public void Dispose()
    {
        CaptureApi.Dispose();
        ToolbarApi.Dispose();
    }
}

internal sealed class PluginCaptureApi(string pluginId, CaptureSnapshot? latest) : IZSnaperCaptureApi, IDisposable
{
    private EventHandler<CaptureCompletedEventArgs>? _completed;
    private bool _disposed;
    public event EventHandler<CaptureCompletedEventArgs>? Completed
    {
        add => _completed += value;
        remove => _completed -= value;
    }

    public CaptureSnapshot? Latest { get; private set; } = latest;

    public void Publish(CaptureSnapshot snapshot, string action)
    {
        if (_disposed) return;
        Latest = snapshot;
        EventHandler<CaptureCompletedEventArgs>? handlers = _completed;
        if (handlers is null) return;
        _ = Task.Run(() =>
        {
            foreach (EventHandler<CaptureCompletedEventArgs> handler in handlers.GetInvocationList())
            {
                if (_disposed) break;
                try { handler(this, new CaptureCompletedEventArgs(snapshot, action)); }
                catch (Exception exception) { AppDiagnostics.LogException($"Plugin.Capture.{pluginId}", exception); }
            }
        });
    }

    public async ValueTask<bool> CopyAsync(CaptureSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Bitmap bitmap = Decode(snapshot);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(CaptureService.TryCopyToClipboard(bitmap)); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        bool copied = await completion.Task;
        cancellationToken.ThrowIfCancellationRequested();
        return copied;
    }

    public ValueTask<string?> SaveAsync(CaptureSnapshot snapshot, string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Bitmap bitmap = Decode(snapshot);
        if (string.IsNullOrWhiteSpace(fileName))
            return ValueTask.FromResult<string?>(CaptureService.SaveToPictures(bitmap));
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
            !fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("文件名必须是单独的 .png 名称。", nameof(fileName));
        string directory = ConfigService.GetEffectiveSavePath();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        bitmap.Save(path, ImageFormat.Png);
        return ValueTask.FromResult<string?>(path);
    }

    private static Bitmap Decode(CaptureSnapshot snapshot)
    {
        using MemoryStream stream = new(snapshot.PngBytes.ToArray());
        using Image image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    public void Dispose() { _disposed = true; _completed = null; Latest = null; }
}

internal sealed class PluginOcrApi : IZSnaperOcrApi
{
    public async ValueTask<string> RecognizeAsync(CaptureSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        using MemoryStream stream = new(snapshot.PngBytes.ToArray());
        using Image image = Image.FromStream(stream);
        using Bitmap bitmap = new(image);
        return await OcrService.RecognizeAsync(bitmap, cancellationToken);
    }
}

internal sealed class PluginToolbarApi(string pluginId, Action changed) : IZSnaperToolbarApi, IDisposable
{
    internal sealed record ActionEntry(PluginToolbarItemDefinition Definition, PluginToolbarActionHandler Handler);
    private readonly object _sync = new();
    private readonly Dictionary<string, ActionEntry> _items = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<ActionEntry> Items
    {
        get { lock (_sync) return _items.Values.ToArray(); }
    }

    public PluginToolbarRegistration Register(PluginToolbarSlot slot, PluginToolbarItemDefinition item,
        PluginToolbarActionHandler handler)
    {
        if (slot != PluginToolbarSlot.CaptureCompleted) throw new ArgumentOutOfRangeException(nameof(slot));
        if (string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 64 ||
            item.Id.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '_' and not '-') ||
            string.IsNullOrWhiteSpace(item.Label) || item.Label.Length > 40)
            throw new ArgumentException("插件动作 ID 或名称无效。", nameof(item));
        lock (_sync)
        {
            if (!_items.TryAdd(item.Id, new ActionEntry(item, handler)))
                throw new InvalidOperationException("插件动作 ID 已存在。");
        }
        changed();
        return new PluginToolbarRegistration(item.Id);
    }

    public bool Unregister(string registrationId)
    {
        bool removed;
        lock (_sync) removed = _items.Remove(registrationId);
        if (removed) changed();
        return removed;
    }

    public async Task RunAsync(string id, CaptureSnapshot snapshot)
    {
        ActionEntry action;
        lock (_sync)
        {
            if (!_items.TryGetValue(id, out ActionEntry? found))
                throw new InvalidOperationException("插件动作不存在。");
            action = found;
        }
        await action.Handler(new PluginToolbarActionContext { Snapshot = snapshot, PluginId = pluginId },
            CancellationToken.None);
    }

    public void Dispose()
    {
        lock (_sync) _items.Clear();
    }
}

internal sealed class PluginLogger(string pluginId) : IPluginLogger
{
    public void Log(PluginLogLevel level, string message, Exception? exception = null)
    {
        Serilog.Events.LogEventLevel eventLevel = level switch
        {
            PluginLogLevel.Debug => Serilog.Events.LogEventLevel.Debug,
            PluginLogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
            PluginLogLevel.Error => Serilog.Events.LogEventLevel.Error,
            _ => Serilog.Events.LogEventLevel.Information
        };
        if (exception is not null) AppDiagnostics.LogException($"Plugin.{pluginId}: {message}", exception, eventLevel);
        else AppDiagnostics.LogMessage($"Plugin.{pluginId}", message, eventLevel);
    }
}
