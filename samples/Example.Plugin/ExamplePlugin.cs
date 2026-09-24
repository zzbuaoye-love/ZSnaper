using ZSnaper.Plugins;

namespace Example.Plugin;

public sealed class ExamplePlugin : IZSnaperPlugin
{
    private IZSnaperHost? _host;
    private PluginToolbarRegistration? _action;

    public PluginManifest Manifest { get; } = new()
    {
        Id = "example.plugin",
        Name = "Example Plugin",
        Version = "1.0.0"
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
        return ValueTask.CompletedTask;
    }

    public ValueTask DisableAsync(CancellationToken cancellationToken = default)
    {
        if (_host is not null)
        {
            _host.Capture.Completed -= OnCaptureCompleted;
            if (_action is not null) _host.Toolbar.Unregister(_action.Id);
        }
        _action = null;
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
