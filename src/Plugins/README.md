# ZSnaper plugin development

ZSnaper loads local `.zsp` ZIP packages through **插件管理**. A new plugin is installed disabled. Enabling it runs its .NET 8 code inside the ZSnaper process with the same Windows permissions. The collectible load context helps unload assemblies for updates; it is not a security sandbox. Only enable packages you trust.

Start with the [runnable Example.Plugin](../../samples/Example.Plugin/README.md):

```powershell
./samples/Example.Plugin/build-package.ps1
```

The resulting `samples/Example.Plugin/dist/Example.Plugin.zsp` contains `manifest.json` and `Example.Plugin.dll` at the package root. Install it from the plugin page, enable it, take a screenshot, then use **记录截图信息** or **保存截图副本** under **上次截图的插件动作**. These actions are on the plugin page after capture, not on the overlay.

The public contract is [PluginContracts.cs](../Plugin.Abstractions/PluginContracts.cs). The versioned Wiki source in [docs/wiki](../../docs/wiki/Home.md) documents the [Plugin API](../../docs/wiki/Plugin-API.md), [manifest and package](../../docs/wiki/Plugin-Manifest-and-Packaging.md), and [update metadata](../../docs/wiki/Plugin-Updates.md). It is also published to the [GitHub Wiki](https://github.com/zzbuaoye-love/ZSnaper/wiki).

Installed packages are under `%APPDATA%\ZSnaper\Plugins\installed`; errors go to `%LOCALAPPDATA%\ZSnaper\Logs`. Enabled plugins are loaded at app startup. `Assembly.Location` can be empty because plugin assemblies are loaded from streams; embed assets or use a deliberate package-relative path instead of relying on it.
