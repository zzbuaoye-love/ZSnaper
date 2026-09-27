# ZSnaper Plugin API

ZSnaper supports .NET 8 plugins packaged as `.zsp` archives. The current public contract is **Plugin API 1.0.0** with **manifest version 1**. A plugin is installed disabled and runs only after the user enables it. Enabled plugins load on the next app startup.

Get started with the [Example.Plugin demo](https://github.com/zzbuaoye-love/ZSnaper/tree/main/samples/Example.Plugin): build its `.zsp` with `./samples/Example.Plugin/build-package.ps1`, install it on the **插件管理** page, enable it, take a screenshot, and use its actions on that page.

- [Plugin API](https://github.com/zzbuaoye-love/ZSnaper/wiki/Plugin-API): lifecycle, capture, OCR, actions, logging, and threading.
- [Manifest and packaging](https://github.com/zzbuaoye-love/ZSnaper/wiki/Plugin-Manifest-and-Packaging): `.zsp` layout, fields, compatibility, and limits.
- [Plugin updates](https://github.com/zzbuaoye-love/ZSnaper/wiki/Plugin-Updates): optional update-check JSON and supported behavior.

The contract is defined in [`PluginContracts.cs`](https://github.com/zzbuaoye-love/ZSnaper/blob/main/src/Plugin.Abstractions/PluginContracts.cs). Plugin code runs in the ZSnaper process with the same user permissions; the load context supports unloading but does not isolate untrusted code.
