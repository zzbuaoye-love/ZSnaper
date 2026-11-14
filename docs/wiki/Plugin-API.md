# Plugin API 1.1.0

Reference [`ZSnaper.Plugin.Abstractions.csproj`](https://github.com/zzbuaoye-love/ZSnaper/blob/main/src/Plugin.Abstractions/ZSnaper.Plugin.Abstractions.csproj), target `net8.0`, and implement `IZSnaperPlugin`. The complete signatures are in [`PluginContracts.cs`](https://github.com/zzbuaoye-love/ZSnaper/blob/main/src/Plugin.Abstractions/PluginContracts.cs). See the [demo source](https://github.com/zzbuaoye-love/ZSnaper/tree/main/samples/Example.Plugin) for a working implementation.

```csharp
public interface IZSnaperPlugin
{
    PluginManifest Manifest { get; }
    ValueTask InitializeAsync(IZSnaperHost host, CancellationToken cancellationToken = default);
    ValueTask EnableAsync(CancellationToken cancellationToken = default);
    ValueTask DisableAsync(CancellationToken cancellationToken = default);
    ValueTask ShutdownAsync(CancellationToken cancellationToken = default);
}
```

The host compares the instance's `Manifest.Id` and `Manifest.Version` with the package manifest before initializing it. It calls `InitializeAsync` and then `EnableAsync` when enabled; `DisableAsync` and `ShutdownAsync` are called when disabled. Subscribe to events and register actions in `EnableAsync`; undo both in `DisableAsync`. If startup fails, the plugin is disabled and the error is logged. The host can reload enabled plugins at app startup.

`IZSnaperHost` exposes `Info`, `Capture`, `Ocr`, `Toolbar`, and `Logger`. `Info` contains `AppName`, `AppVersion`, and `PluginApiVersion`. The APIs currently available are:

| API | Members | Behavior |
| --- | --- | --- |
| `Capture` | `Completed`, `Latest`, `CopyAsync(snapshot)`, `SaveAsync(snapshot, fileName?)` | `Completed` reports a PNG snapshot and completion action. `Latest` is nullable until a screenshot is available. `CopyAsync` returns whether clipboard copy succeeded. `SaveAsync` returns the saved path or `null`; an explicit name must be a single `.png` filename, saved in the configured save directory. With no name, it uses the normal Pictures save behavior. |
| `Ocr` | `RecognizeAsync(snapshot)` | Uses the OCR provider selected in ZSnaper settings and returns recognized text. A configured online provider may send the image to that provider. |
| `Toolbar` | `Register(slot, item, handler)`, `Unregister(registrationId)` | Only `PluginToolbarSlot.CaptureCompleted` exists. The host passes the latest snapshot and plugin ID to the action handler. |
| `Logger` | `Log(level, message, exception?)` | Levels: `Debug`, `Info`, `Warning`, `Error`. Writes to ZSnaper diagnostics logs. |

`CaptureSnapshot` provides `PngBytes` (`ReadOnlyMemory<byte>`), `Width`, `Height`, `CapturedAt` (`DateTimeOffset`), and `Source`. `CaptureCompletedEventArgs` adds `CompletionAction`. Capture event callbacks run on a worker thread, so marshal any WinForms UI work onto the UI thread. The host catches and logs exceptions from capture event handlers. Action failures are returned to the plugin page as errors.

Register an action with a `PluginToolbarItemDefinition` (`Id`, `Label`, optional `Icon`, `Tooltip`, `Order`) and a `PluginToolbarActionHandler`. IDs must be 1–64 letters, digits, underscores, or hyphens; labels must be 1–40 non-whitespace characters. IDs must be unique within that plugin. Registered actions appear under **上次截图的插件动作** on the **插件管理** page after a screenshot. They are not injected into the capture overlay. `Register` returns a `PluginToolbarRegistration` whose `Id` is passed to `Unregister`.

The host loads plugin assemblies from streams, so `Assembly.Location` may be empty. Include any dependencies in the package and embed runtime assets or resolve them deliberately; do not assume the assembly location points to a file.

## Capture overlay arrow tools (1.1)

API 1.0 plugins remain compatible. API 1.1 adds the optional `IZSnaperOverlayHost` capability, defined in [PluginOverlayContracts.cs](https://github.com/zzbuaoye-love/ZSnaper/blob/main/src/Plugin.Abstractions/PluginOverlayContracts.cs). Cast the supplied host to this interface and use `Overlay.RegisterArrow(definition, renderer)` in `EnableAsync`; call `Overlay.UnregisterArrow(registration.Id)` in `DisableAsync`. Require `pluginApi: ">=1.1.0 <2.0.0"` in the manifest so older hosts reject the plugin before loading it.

`PluginArrowToolDefinition` supplies `Id`, `Label` and `Order`. IDs follow the same rules as post-capture actions and must be unique within the plugin. Registered styles appear after the three native styles in the existing arrow settings panel; the main capture toolbar keeps its original arrow button. The host takes a style snapshot when starting a capture; registration changes apply to subsequent captures. Selecting a style also changes the selected arrow and participates in undo/redo.

`PluginArrowRenderer` accepts a `PluginArrowContext` with `Start`, `End` (`PluginPoint`) and stroke `Width`, all in image pixels. Return one or more `PluginArrowPath` objects, each with `Points`, `Closed` and `Filled`. Paths are polylines; filled paths must be closed. Return 1–64 paths with 2–4096 finite points each. Keep this synchronous callback fast and deterministic: it runs on the UI thread and must not perform I/O or retain host objects. The host copies returned points, uses the selected annotation color, and caches geometry until endpoints or width change. Callback failures or invalid geometry are logged and fall back to a native arrow.

The host handles style panel layout and icons, drag drawing, geometry hit-testing, endpoint/body editing, colors, width, deletion, undo/redo, and final screenshot rendering. This API currently extends arrows only; it does not expose arbitrary controls or replace the capture overlay.

See [ArrowTools.Plugin](https://github.com/zzbuaoye-love/ZSnaper/tree/main/samples/ArrowTools.Plugin) for a complete plugin that registers hollow and elbow arrows without referencing the host assembly.

For ready-to-install packages and the matching API 1.1 host, see [Arrow Tools demo](https://github.com/zzbuaoye-love/ZSnaper/wiki/Arrow-Tools-Demo).
