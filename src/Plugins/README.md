# ZSnaper plugins

ZSnaper accepts local `.zsp` packages on the **插件管理** page. Importing a
package checks its manifest and extracts it to `%APPDATA%\ZSnaper\Plugins\installed`.
Installed plugins start disabled. Enabling a plugin runs its .NET assembly in
the ZSnaper process, with the same Windows permissions as ZSnaper. Only enable
packages you trust. The load context supports unloading managed assemblies;
it is **not a sandbox**.

The page can enable, disable, and uninstall a plugin. Enabled plugins are
reloaded when ZSnaper starts. If initialization fails, the plugin is disabled
and the error is written to `%LOCALAPPDATA%\ZSnaper\Logs`.

## `.zsp` package shape

```text
example.zsp
├─ manifest.json
└─ Example.Plugin.dll
```

Example manifest:

```json
{
  "manifestVersion": 1,
  "id": "example.plugin",
  "name": "Example Plugin",
  "description": "演示截图事件与截图完成后的插件动作。",
  "version": "1.0.0",
  "entry": {
    "assembly": "Example.Plugin.dll",
    "type": "Example.Plugin.ExamplePlugin"
  },
  "requires": {
    "pluginApi": ">=1.0.0 <2.0.0",
    "appVersion": ">=0.0.5"
  }
}
```

`PluginPackageService.Inspect` validates the manifest, entry assembly, version
constraints, HTTPS update URL, duplicate paths, archive traversal, file count,
and uncompressed size. Extraction also enforces a byte limit. `VerifySha256`
can verify a package downloaded from a plugin update endpoint.

## Develop a plugin

Reference `src/Plugin.Abstractions/ZSnaper.Plugin.Abstractions.csproj` and
implement `IZSnaperPlugin`. The sample under `samples/Example.Plugin` subscribes
to the capture event and registers an action for the latest screenshot.

Build and package the sample with PowerShell:

```powershell
dotnet build .\samples\Example.Plugin\Example.Plugin.csproj -c Release
$source = '.\samples\Example.Plugin'
$package = Join-Path $env:TEMP 'example-plugin-package'
New-Item -ItemType Directory -Force -Path $package | Out-Null
Copy-Item "$source\manifest.json" $package
Copy-Item "$source\bin\Release\net8.0\Example.Plugin.dll" $package
Compress-Archive -Path "$package\*" -DestinationPath '.\example.zip' -Force
Rename-Item '.\example.zip' 'example.zsp'
```

`IZSnaperHost` provides capture snapshots, clipboard/save methods, the configured
OCR provider, logging, and `CaptureCompleted` actions. Capture event callbacks
run on a worker thread; WinForms UI updates must be marshalled to the UI thread.
Actions registered through `IZSnaperToolbarApi` appear on the plugin page after
a screenshot. Plugin code should unregister its actions and event handlers in
`DisableAsync`. Automatic plugin downloads and process isolation are not enabled.
