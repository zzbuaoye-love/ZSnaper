using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.Loader;
using ZSnaper.Helpers;
using ZSnaper.Services;

namespace ZSnaper.Plugins;

public sealed record PluginOperationResult(bool Success, string Message);
public sealed record PluginActionInfo(string PluginId, PluginToolbarItemDefinition Item);

/// <summary>
/// Runs explicitly enabled, locally installed plugins. Plugins are trusted code
/// in this process; collectible load contexts support updates, not isolation.
/// </summary>
public sealed class PluginRuntimeManager : IDisposable
{
    private sealed record RunningPlugin(IZSnaperPlugin Instance, PluginLoadContext Context, PluginHost Host);

    private readonly PluginInstaller _installer;
    private readonly Dictionary<string, RunningPlugin> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CaptureSnapshot? _latest;
    private bool _disposed;

    public event Action? Changed;
    public PluginRuntimeManager(PluginInstaller? installer = null) => _installer = installer ?? new PluginInstaller();
    public IReadOnlyList<InstalledPlugin> List() => _installer.List();
    public bool IsRunning(string id) => _running.ContainsKey(id);
    public string? GetError(string id) => _errors.GetValueOrDefault(id);
    public bool HasCapture => _latest is not null;

    public PluginOperationResult Install(string packagePath)
    {
        try
        {
            InstalledPlugin plugin = _installer.Install(packagePath);
            Changed?.Invoke();
            return new(true, $"已安装 {plugin.Manifest.Name}。启用后才会运行插件代码。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return new(false, exception.Message);
        }
    }

    public async Task StartEnabledAsync()
    {
        foreach (InstalledPlugin plugin in List().Where(item => item.Enabled))
            await EnableAsync(plugin.Manifest.Id);
    }

    public async Task<PluginOperationResult> EnableAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return new(false, "插件管理器已关闭。");
            if (_running.ContainsKey(id)) return new(true, "插件已启用。");
            InstalledPlugin? installed = List().FirstOrDefault(item =>
                string.Equals(item.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));
            if (installed is null) return new(false, "未找到已安装插件。");
            if (!installed.Compatible) return new(false, "插件与当前 ZSnaper 或插件接口版本不兼容。");

            string assemblyPath = Path.GetFullPath(Path.Combine(installed.Directory,
                installed.Manifest.Entry.Assembly.Replace('/', Path.DirectorySeparatorChar)));
            string root = Path.GetFullPath(installed.Directory) + Path.DirectorySeparatorChar;
            if (!assemblyPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath))
                return new(false, "插件入口程序集不存在。");

            var context = new PluginLoadContext(assemblyPath);
            IZSnaperPlugin? instance = null;
            PluginHost? host = null;
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(assemblyPath);
                Type type = assembly.GetType(installed.Manifest.Entry.Type, throwOnError: true)!;
                if (!typeof(IZSnaperPlugin).IsAssignableFrom(type))
                    throw new InvalidDataException("插件入口未实现 IZSnaperPlugin。");
                instance = (IZSnaperPlugin)Activator.CreateInstance(type)!;
                if (!string.Equals(instance.Manifest.Id, installed.Manifest.Id, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(instance.Manifest.Version, installed.Manifest.Version, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("插件入口的 ID 或版本与清单不一致。");

                host = new PluginHost(id, _latest, () => { if (!_disposed) Changed?.Invoke(); });
                await instance.InitializeAsync(host);
                await instance.EnableAsync();
                if (_disposed) throw new OperationCanceledException("插件管理器已关闭。");
                _running.Add(id, new RunningPlugin(instance, context, host));
                _installer.SetEnabled(id, true);
                _errors.Remove(id);
                Changed?.Invoke();
                return new(true, $"已启用 {installed.Manifest.Name}。");
            }
            catch (Exception exception)
            {
                AppDiagnostics.LogException($"Plugin.Enable.{id}", exception);
                _errors[id] = exception.GetBaseException().Message;
                _running.Remove(id);
                if (instance is not null)
                {
                    try { await instance.ShutdownAsync(); } catch { }
                }
                host?.Dispose();
                context.Unload();
                try { _installer.SetEnabled(id, false); } catch { }
                Changed?.Invoke();
                return new(false, $"插件启用失败：{exception.GetBaseException().Message}");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<PluginOperationResult> DisableAsync(string id)
    {
        await _gate.WaitAsync();
        try { return await DisableCoreAsync(id); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppDiagnostics.LogException($"Plugin.Disable.{id}", exception);
            return new(false, "停用插件时出错：" + exception.Message);
        }
        finally { _gate.Release(); }
    }

    private async Task<PluginOperationResult> DisableCoreAsync(string id)
    {
        if (!_running.Remove(id, out RunningPlugin? running))
        {
            _installer.SetEnabled(id, false);
            _errors.Remove(id);
            if (_running.Count == 0) _latest = null;
            Changed?.Invoke();
            return new(true, "插件已停用。");
        }

        Exception? failure = null;
        try { await running.Instance.DisableAsync(); }
        catch (Exception exception) { failure = exception; AppDiagnostics.LogException($"Plugin.Disable.{id}", exception); }
        try { await running.Instance.ShutdownAsync(); }
        catch (Exception exception) { failure ??= exception; AppDiagnostics.LogException($"Plugin.Shutdown.{id}", exception); }
        running.Host.Dispose();
        running.Context.Unload();
        if (_running.Count == 0) _latest = null;
        _installer.SetEnabled(id, false);
        _errors.Remove(id);
        Changed?.Invoke();
        return failure is null
            ? new(true, "插件已停用。")
            : new(false, "插件已停用，但清理时出错：" + failure.GetBaseException().Message);
    }

    public async Task<PluginOperationResult> RemoveAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            if (_running.ContainsKey(id)) await DisableCoreAsync(id);
            _installer.Remove(id);
            _errors.Remove(id);
            Changed?.Invoke();
            return new(true, "插件已卸载。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(false, "卸载失败：" + exception.Message);
        }
        finally { _gate.Release(); }
    }

    public void PublishCapture(Bitmap bitmap, string action)
    {
        if (_disposed || _running.Count == 0) return;
        using MemoryStream stream = new();
        bitmap.Save(stream, ImageFormat.Png);
        _latest = new CaptureSnapshot(stream.ToArray(), bitmap.Width, bitmap.Height, DateTimeOffset.Now);
        foreach (RunningPlugin running in _running.Values.ToArray())
            running.Host.CaptureApi.Publish(_latest, action);
        Changed?.Invoke();
    }

    public IReadOnlyList<PluginActionInfo> GetActions() =>
        _running.SelectMany(pair => pair.Value.Host.ToolbarApi.Items.Select(item =>
            new PluginActionInfo(pair.Key, item.Definition)))
            .OrderBy(item => item.Item.Order).ToArray();

    public async Task<PluginOperationResult> RunActionAsync(string pluginId, string actionId)
    {
        if (_latest is null) return new(false, "还没有可供插件处理的截图。");
        if (!_running.TryGetValue(pluginId, out RunningPlugin? running)) return new(false, "插件未启用。");
        try
        {
            await running.Host.ToolbarApi.RunAsync(actionId, _latest);
            return new(true, "插件动作已完成。");
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException($"Plugin.Action.{pluginId}.{actionId}", exception);
            return new(false, "插件动作失败：" + exception.GetBaseException().Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (RunningPlugin running in _running.Values)
        {
            try { running.Instance.DisableAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { running.Instance.ShutdownAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
            running.Host.Dispose();
            running.Context.Unload();
        }
        _running.Clear();
        _latest = null;
    }

    private sealed class PluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IZSnaperPlugin).Assembly.GetName().Name)
                return typeof(IZSnaperPlugin).Assembly;
            string? path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string name)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
