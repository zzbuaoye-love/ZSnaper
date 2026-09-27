using ZSnaper.Plugins;

namespace Example.Plugin;

public sealed class ExamplePlugin : IZSnaperPlugin
{
    private IZSnaperHost? _host;
    private PluginToolbarRegistration? _action;
    private PluginToolbarRegistration? _saveAction;

    public PluginManifest Manifest { get; } = new()
    {
        Id = "example.plugin",
        Name = "Example Plugin",
        Version = "1.1.0"
    };

    public ValueTask InitializeAsync(IZSnaperHost host, CancellationToken cancellationToken = default)
    {
        _host = host;
        return ValueTask.CompletedTask;
    }

    public ValueTask EnableAsync(CancellationToken cancellationToken = default)
    {
        if (_host is null) throw new InvalidOperationException("Plugin was not initialized.");
        _host.Capture.Completed += OnCaptureCompleted;
        _action = _host.Toolbar.Register(PluginToolbarSlot.CaptureCompleted,
            new PluginToolbarItemDefinition
            {
                Id = "log_capture",
                Label = "记录截图信息",
                Tooltip = "在插件日志中记录上次截图尺寸"
            },
            (context, token) =>
            {
                _host.Logger.Log(PluginLogLevel.Info,
                    $"Action: {context.Snapshot.Width} x {context.Snapshot.Height}");
                return ValueTask.CompletedTask;
            });
        _saveAction = _host.Toolbar.Register(PluginToolbarSlot.CaptureCompleted,
            new PluginToolbarItemDefinition
            {
                Id = "save_capture",
                Label = "保存截图副本",
                Tooltip = "将上次截图保存为 PNG 文件",
                Order = 1
            },
            async (context, token) =>
            {
                string fileName = $"ZSnaper-demo-{context.Snapshot.CapturedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png";
                string? path = await _host.Capture.SaveAsync(context.Snapshot, fileName, token);
                if (path is null) throw new IOException("截图保存失败。");
                _host.Logger.Log(PluginLogLevel.Info, $"Saved capture to {path}");
            });
        return ValueTask.CompletedTask;
    }

    public ValueTask DisableAsync(CancellationToken cancellationToken = default)
    {
        if (_host is not null)
        {
            _host.Capture.Completed -= OnCaptureCompleted;
            if (_action is not null) _host.Toolbar.Unregister(_action.Id);
            if (_saveAction is not null) _host.Toolbar.Unregister(_saveAction.Id);
        }
        _action = null;
        _saveAction = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _host = null;
        return ValueTask.CompletedTask;
    }

    private void OnCaptureCompleted(object? sender, CaptureCompletedEventArgs e) =>
        _host?.Logger.Log(PluginLogLevel.Info,
            $"Capture: {e.Snapshot.Width} x {e.Snapshot.Height}, {e.CompletionAction}");
}
