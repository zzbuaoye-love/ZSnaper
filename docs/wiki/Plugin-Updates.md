# Plugin update metadata

An optional `update` object in `manifest.json` can point to a JSON metadata endpoint:

```json
"update": {
  "checkUrl": "https://example.com/plugins/example-plugin.json",
  "channel": "stable",
  "autoCheck": true
}
```

`checkUrl` must be HTTPS. The endpoint response has this shape:

```json
{
  "pluginId": "example.plugin",
  "version": "1.2.0",
  "appVersion": ">=0.0.5",
  "pluginApi": ">=1.0.0 <2.0.0",
  "packageUrl": "https://example.com/plugins/Example.Plugin-1.2.0.zsp",
  "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "releaseNotes": "Changes in this version."
}
```

`PluginUpdateClient.CheckAsync` verifies the plugin ID, version, app/API compatibility, HTTPS package URL, and 64-digit SHA-256 text; the response size limit is **1 MiB**. It reports whether the returned version is newer. `PluginPackageService.VerifySha256` can verify a downloaded package against the metadata hash.

Current ZSnaper code only **checks metadata** through this API. It does not automatically download, install, or enable a new package. The `channel` and `autoCheck` manifest fields are metadata; they do not currently trigger a background updater. Build and install a new `.zsp` explicitly.
