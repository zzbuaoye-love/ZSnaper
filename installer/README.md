# ZSnaper installer

This directory contains the Windows packaging tools. Setup uses Avalonia,
the updater uses WinForms, and both share the .NET installer core for file
operations, registry entries, and Windows shortcuts.

## Project layout

```text
installer/
├─ src/
│  ├─ ZSnaper.Installer.Core/   install, rollback, checksums, registry, shortcuts
│  ├─ ZSnaper.FullInstaller/    Avalonia full installer
│  └─ ZSnaper.UpdateInstaller/  custom WinForms .zup updater
├─ scripts/
│  └─ Build-Installers.ps1
└─ tests/
   ├─ Test-InstallerArtifacts.ps1
   └─ ZSnaper.Installer.Smoke.csproj
```

## Build

To build `1.0.0` from the repository root without a differential package, run:

```powershell
.\installer\scripts\Build-Installers.ps1 -Version 1.0.0 -BaseVersion ''
```

The full installer is published self-contained, then an application ZIP is
appended to the executable with a small binary footer. The installer extracts
that payload to a temporary directory and copies it through a staging folder.

To create a differential update package, provide the published application
directory for the previous release:

```powershell
.\installer\scripts\Build-Installers.ps1 `
  -Version 1.0.0 `
  -BaseVersion 0.0.6-beta `
  -BasePayloadDirectory C:\path\to\published-0.0.6-beta-setup-payload
```

Extract the baseline from the published `0.0.6-beta` `Setup.exe`. The build
checks that the target version matches the app and installer projects and
requires a baseline payload for differential builds.

The resulting `.zup` contains `update.manifest.json`, changed application
files, SHA-256 hashes, and a delete list. The updater rejects an unexpected
base version, validates paths and hashes, creates a temporary backup, and
rolls back changed files if anything fails.

Use the `1.0.0` setup to upgrade other installed versions. Update the
single-file portable edition by downloading and extracting the new portable ZIP.

Installed applications keep only `update/Update.exe` in the `update` directory.
The Windows uninstall entry runs that executable with `--uninstall`; it copies
itself to a temporary directory before removing the installation. New update
packages remove the legacy `update/ZSnaper-Setup.exe` from older installs.
The separate release `Setup.exe` is retained for first-time installation.

The update executable is published self-contained.
Setup, the full ZIP, and the portable ZIP include the ZSnaper and Lucide
license notices.

## Safety rules

- Only the exact ZSnaper process under the selected installation directory is
  asked to close; no broad `taskkill` is used.
- Setup accepts a new or empty directory, or the registered ZSnaper directory
  for an upgrade. Uninstall requires the registered directory.
- Application files are copied through temporary names and verified before the
  registry version is changed.
- Uninstall removes only shortcuts that point to the selected ZSnaper binary.
- `%APPDATA%\ZSnaper` is not touched, so user settings survive uninstall and
  repair. Configuration stored inside the install directory is removed when
  that directory is uninstalled.
- The startup registry value is removed only when it points to this install.

## Artifacts

```text
ZSnaper-v1.0.0-win-x64-Setup.exe
ZSnaper-v1.0.0-win-x64-full.zip
ZSnaper-v1.0.0-win-x64-portable.zip
ZSnaper-v1.0.0-win-x64-Update.exe
ZSnaper-v1.0.0-win-x64-Update.zup
SHA256SUMS.txt
```

The files are written to `installer/artifacts/1.0.0-win-x64`. Verify the
installer structure and update metadata with:

```powershell
$packageDir = 'installer/artifacts/1.0.0-win-x64'
.\installer\tests\Test-InstallerArtifacts.ps1 -ArtifactDirectory $packageDir
dotnet run --project installer/tests/ZSnaper.Installer.Smoke.csproj -c Release -- `
  "$packageDir/ZSnaper-v1.0.0-win-x64-Setup.exe" `
  "$packageDir/ZSnaper-v1.0.0-win-x64-Update.zup" 0.0.6-beta 1.0.0
```
