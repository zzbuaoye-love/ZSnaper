# Example.Plugin demo

This runnable demo subscribes to `Capture.Completed` and adds two actions on the **插件管理** page after a capture:

- **记录截图信息** logs the latest screenshot dimensions.
- **保存截图副本** saves the latest screenshot as a uniquely named PNG in ZSnaper's configured save directory.

It uses only `ZSnaper.Plugin.Abstractions` and targets .NET 8. No OCR or network access is needed.

From the repository root, build a `.zsp` package with PowerShell:

```powershell
./samples/Example.Plugin/build-package.ps1
```

The script builds the project and writes `samples/Example.Plugin/dist/Example.Plugin.zsp`, then prints its SHA-256 hash. In ZSnaper, open **插件管理 → 安装插件**, select that file, and enable **Example Plugin**. Take a screenshot, return to **插件管理**, and click either action under **上次截图的插件动作**. The action buttons are on the plugin page, not on the capture overlay.

`EnableAsync` subscribes/registers; `DisableAsync` unsubscribes/unregisters. The host calls `ShutdownAsync` when the plugin is unloaded. Capture callbacks run on a worker thread. The demo only logs from that callback and does not touch WinForms controls.

See the [Plugin API Wiki](https://github.com/zzbuaoye-love/ZSnaper/wiki/Plugin-API) for the contract, manifest fields, packaging limits, and update format.
