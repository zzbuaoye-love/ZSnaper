# ZSnaper installer

This directory contains the handwritten Windows installer for version 0.0.6.
It uses .NET BCL, WinForms, the Windows registry, and the built-in WScript.Shell
shortcut COM API. It does not use Inno Setup, WiX, Squirrel, Velopack, or any
other installer framework.

## Project layout

```text
installer/
├─ src/
│  ├─ ZSnaper.Installer.Core/   install, rollback, checksums, registry, shortcuts
│  ├─ ZSnaper.FullInstaller/    custom WinForms full installer
│  └─ ZSnaper.UpdateInstaller/  custom WinForms .zup updater
├─ scripts/
│  └─ Build-Installers.ps1
└─ tests/
   ├─ Test-InstallerArtifacts.ps1
   └─ ZSnaper.Installer.Smoke.csproj
```

## Build

Run this from the repository root:

```powershell
.\installer\scripts\Build-Installers.ps1 -Version 0.0.6-beta
```

The full installer is published self-contained, then an application ZIP is
appended to the executable with a small binary footer. The installer extracts
that payload to a temporary directory and copies it through a staging folder.

To create a differential update package, provide the published application
directory for the previous release:

```powershell
.\installer\scripts\Build-Installers.ps1 `
  -Version 0.0.6-beta `
  -BaseVersion 0.0.5-beta `
  -BasePayloadDirectory C:\path\to\published-0.0.5-beta-setup-payload
```

Use the payload extracted from the published 0.0.5-beta `Setup.exe`, not a
local rebuild of that version. The build stops if the selected version differs
from project metadata or a differential update has no baseline payload.

The resulting `.zup` contains `update.manifest.json`, changed application
files, SHA-256 hashes, and a delete list. The updater rejects an unexpected
base version, validates paths and hashes, creates a temporary backup, and
rolls back changed files if anything fails.

Installed applications keep only `update/Update.exe` in the `update` directory.
The Windows uninstall entry runs that executable with `--uninstall`; it copies
itself to a temporary directory before removing the installation. New update
packages remove the legacy `update/ZSnaper-Setup.exe` from older installs.
The separate release `Setup.exe` is retained for first-time installation.

The update executable is published self-contained. The `.zup` itself contains
only changed application files and metadata.

## Safety rules

- Only the exact ZSnaper process under the selected installation directory is
  asked to close; no broad `taskkill` is used.
- Application files are copied through temporary names and verified before the
  registry version is changed.
- Uninstall removes only shortcuts that point to the selected ZSnaper binary.
- `%APPDATA%\ZSnaper` is not touched, so user settings survive uninstall and
  repair.
- The startup registry value is removed only when it points to this install.

## Artifacts

```text
ZSnaper-v0.0.6-beta-win-x64-Setup.exe
ZSnaper-v0.0.6-beta-win-x64-Update.exe
ZSnaper-v0.0.6-beta-win-x64-Update.zup
SHA256SUMS.txt
```
