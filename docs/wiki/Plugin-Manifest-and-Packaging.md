# Manifest and packaging

A `.zsp` file is a ZIP archive. `manifest.json` must be at the archive root, and `entry.assembly` must name a DLL inside it. The [demo build script](https://github.com/zzbuaoye-love/ZSnaper/blob/main/samples/Example.Plugin/build-package.ps1) produces this layout:

```text
Example.Plugin.zsp
├── manifest.json
└── Example.Plugin.dll
```

The [demo manifest](https://github.com/zzbuaoye-love/ZSnaper/blob/main/samples/Example.Plugin/manifest.json) is:

```json
{
  "manifestVersion": 1,
  "id": "example.plugin",
  "name": "Example Plugin",
  "description": "演示截图事件、插件动作和保存截图。",
  "version": "1.1.0",
  "entry": {
    "assembly": "Example.Plugin.dll",
    "type": "Example.Plugin.ExamplePlugin"
  },
  "scope": {
    "type": "app",
    "events": ["capture.completed"],
    "runAt": "event"
  },
  "requires": {
    "hostApis": ["capture", "toolbar", "logger"],
    "pluginApi": ">=1.0.0 <2.0.0",
    "appVersion": ">=0.0.5"
  }
}
```

| Field | Meaning |
| --- | --- |
| `manifestVersion` | Must equal `1`. |
| `id` | 2–128 characters: starts with an ASCII letter or digit, followed by letters, digits, `.`, `_`, or `-`. Unique installation identity. |
| `name`, `description`, `version` | Display name, optional description, and a supported semantic version. The entry class must report the same `id` and `version`. |
| `entry.assembly`, `entry.type` | Relative DLL path in the package and fully qualified .NET type implementing `IZSnaperPlugin`. |
| `requires.pluginApi`, `requires.appVersion` | Version ranges checked at install and enable. The current API is `1.0.0`; use `>=1.0.0 <2.0.0` for this major version. Ranges support comparison clauses, `^`, `~`, and `||`. |
| `requires.hostApis` | Descriptive list of APIs the plugin uses. It is not currently a permission or capability gate. |
| `lifecycle`, `scope` | Optional descriptive metadata with defaults in the contract. They do not independently schedule execution or subscribe to events; plugin code must do that. |
| `update` | Optional `checkUrl` (HTTPS), `channel`, and `autoCheck` fields. See [Plugin updates](https://github.com/zzbuaoye-love/ZSnaper/wiki/Plugin-Updates). |

The package inspector checks the root manifest, version compatibility, entry DLL, safe paths, case-insensitive duplicate names, at most **4,096 files**, at most **512 MiB** total uncompressed size, and at most **1 MiB** for the manifest. Extraction also enforces the size limit. It does not execute code. Installation extracts into `%APPDATA%\ZSnaper\Plugins\installed` and leaves the plugin disabled. Enabling runs its code inside the ZSnaper process; there is no sandbox.

Build the demo from the repository root with:

```powershell
./samples/Example.Plugin/build-package.ps1
```

The package is written to `samples/Example.Plugin/dist/Example.Plugin.zsp`. Open ZSnaper **插件管理 → 安装插件**, select it, and enable it. Take a screenshot, return to that page, and use the demo actions. For troubleshooting, check `%LOCALAPPDATA%\ZSnaper\Logs`.
