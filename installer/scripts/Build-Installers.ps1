[CmdletBinding()]
param(
    [string]$Version = "0.0.6-beta",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$BasePayloadDirectory = "",
    [string]$BaseVersion = "0.0.5-beta",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$installerRoot = Join-Path $repoRoot "installer"
$workRoot = Join-Path $installerRoot ".work\$Version-$Runtime"
$artifactRoot = Join-Path $installerRoot "artifacts\$Version-$Runtime"
$appPublish = Join-Path $workRoot "app-publish"
$fullPublish = Join-Path $workRoot "full-installer-publish"
$updatePublish = Join-Path $workRoot "update-installer-publish"
$portablePublish = Join-Path $workRoot "portable-publish"
$installedPayload = Join-Path $workRoot "installed-payload"
$payloadZip = Join-Path $workRoot "application-payload.zip"

if ([string]::IsNullOrWhiteSpace($BasePayloadDirectory)) {
    $candidateBase = Join-Path $installerRoot ".work\published-$BaseVersion\setup-payload"
    if (-not (Test-Path $candidateBase -PathType Container)) {
        $candidateBase = Join-Path $installerRoot ".work\base-$BaseVersion-$Runtime"
    }
    if (Test-Path $candidateBase -PathType Container) {
        $BasePayloadDirectory = $candidateBase
    }
}

$appProject = [xml](Get-Content -LiteralPath (Join-Path $repoRoot "ZSnaper.csproj") -Raw)
if ($Version -ne $appProject.Project.PropertyGroup.Version) {
    throw "Requested version $Version differs from the application project version $($appProject.Project.PropertyGroup.Version)."
}
foreach ($projectPath in @(
        (Join-Path $installerRoot "src\ZSnaper.FullInstaller\ZSnaper.FullInstaller.csproj"),
        (Join-Path $installerRoot "src\ZSnaper.UpdateInstaller\ZSnaper.UpdateInstaller.csproj")
    )) {
    $project = [xml](Get-Content -LiteralPath $projectPath -Raw)
    if ($Version -ne $project.Project.PropertyGroup.InformationalVersion) {
        throw "Requested version $Version differs from the installer project version in $projectPath."
    }
}
if (-not [string]::IsNullOrWhiteSpace($BaseVersion) -and [string]::IsNullOrWhiteSpace($BasePayloadDirectory)) {
    throw "The published $BaseVersion payload is required to build a differential update. Pass -BasePayloadDirectory."
}

function Invoke-Dotnet {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE."
    }
}

function Get-RelativeFileMap {
    param([string]$Root)

    $rootPath = (Resolve-Path $Root).Path
    $rootUri = [Uri]::new(($rootPath.TrimEnd('\') + '\'))
    $map = @{}
    foreach ($file in Get-ChildItem -LiteralPath $rootPath -File -Recurse) {
        $relative = $rootUri.MakeRelativeUri([Uri]$file.FullName).ToString().Replace('/', '/')
        $map[$relative] = $file.FullName
    }
    return $map
}

function Add-EmbeddedPayload {
    param(
        [string]$InstallerPath,
        [string]$PayloadPath
    )

    $payloadBytes = [IO.File]::ReadAllBytes($PayloadPath)
    $markerBytes = [Text.Encoding]::ASCII.GetBytes("ZSNAPER_PAYLOAD_V1")
    $stream = [IO.File]::Open($InstallerPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $stream.Write($payloadBytes, 0, $payloadBytes.Length)
        $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::UTF8, $true)
        try {
            $writer.Write([int64]$payloadBytes.Length)
            $writer.Write($markerBytes)
            $writer.Flush()
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

New-Item -ItemType Directory -Force -Path $workRoot, $artifactRoot | Out-Null
if (-not $SkipBuild) {
    foreach ($publishDirectory in @($appPublish, $fullPublish, $updatePublish, $portablePublish, $installedPayload)) {
        if (Test-Path $publishDirectory) {
            Remove-Item -LiteralPath $publishDirectory -Recurse -Force
        }
    }

    Invoke-Dotnet @(
        "publish", (Join-Path $repoRoot "ZSnaper.csproj"),
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", "true",
        "-p:PublishSingleFile=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $appPublish,
        "--nologo"
    )

    Invoke-Dotnet @(
        "publish", (Join-Path $repoRoot "ZSnaper.csproj"),
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $portablePublish,
        "--nologo"
    )

    Invoke-Dotnet @(
        "publish", (Join-Path $installerRoot "src\ZSnaper.FullInstaller\ZSnaper.FullInstaller.csproj"),
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $fullPublish,
        "--nologo"
    )

    Invoke-Dotnet @(
        "publish", (Join-Path $installerRoot "src\ZSnaper.UpdateInstaller\ZSnaper.UpdateInstaller.csproj"),
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $updatePublish,
        "--nologo"
    )
}

if (-not (Test-Path (Join-Path $appPublish "ZSnaper.exe"))) {
    throw "Application publish output is missing ZSnaper.exe."
}
$portableSource = Join-Path $portablePublish "ZSnaper.exe"
$updateSource = Join-Path $updatePublish "ZSnaper.UpdateInstaller.exe"
if (-not (Test-Path $portableSource) -or -not (Test-Path $updateSource)) {
    throw "The installed single-file application or updater output is missing."
}
if (Test-Path $installedPayload) {
    Remove-Item -LiteralPath $installedPayload -Recurse -Force
}
New-Item -ItemType Directory -Force -Path (Join-Path $installedPayload "langs"),(Join-Path $installedPayload "update") | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $appPublish -File) {
    Copy-Item -LiteralPath $file.FullName -Destination $installedPayload -Force
}
foreach ($directory in Get-ChildItem -LiteralPath $appPublish -Directory) {
    $destinationRoot = if ($directory.Name -match '^[a-z]{2}(-[A-Za-z]{2,4})?$') {
        Join-Path $installedPayload "langs"
    }
    else {
        $installedPayload
    }
    Copy-Item -LiteralPath $directory.FullName -Destination $destinationRoot -Recurse -Force
}
Copy-Item -LiteralPath $updateSource -Destination (Join-Path $installedPayload "update\Update.exe") -Force

$payloadParent = Split-Path $payloadZip -Parent
New-Item -ItemType Directory -Force -Path $payloadParent | Out-Null
if (Test-Path $payloadZip) {
    Remove-Item -LiteralPath $payloadZip -Force
}
Compress-Archive -Path (Join-Path $installedPayload "*") -DestinationPath $payloadZip -CompressionLevel Optimal

$setupSource = Join-Path $fullPublish "ZSnaper.FullInstaller.exe"
if (-not (Test-Path $setupSource)) {
    throw "Full installer publish output is missing ZSnaper.FullInstaller.exe."
}
$artifactNames = @(
    "ZSnaper-v$Version-$Runtime-Setup.exe",
    "ZSnaper-v$Version-$Runtime-Update.exe",
    "ZSnaper-v$Version-$Runtime-Update.zup",
    "ZSnaper-v$Version-$Runtime-full.zip",
    "ZSnaper-v$Version-$Runtime-portable.zip",
    "SHA256SUMS.txt"
)
foreach ($artifactName in $artifactNames) {
    $artifactPath = Join-Path $artifactRoot $artifactName
    if (Test-Path $artifactPath -PathType Leaf) {
        Remove-Item -LiteralPath $artifactPath -Force
    }
}

$setupPath = Join-Path $artifactRoot "ZSnaper-v$Version-$Runtime-Setup.exe"
Copy-Item -LiteralPath $setupSource -Destination $setupPath -Force
Add-EmbeddedPayload -InstallerPath $setupPath -PayloadPath $payloadZip

$fullZipPath = Join-Path $artifactRoot "ZSnaper-v$Version-$Runtime-full.zip"
Compress-Archive -Path (Join-Path $installedPayload "*") -DestinationPath $fullZipPath -CompressionLevel Optimal

if (Test-Path $portableSource) {
    $portableZipPath = Join-Path $artifactRoot "ZSnaper-v$Version-$Runtime-portable.zip"
    Compress-Archive -Path $portableSource -DestinationPath $portableZipPath -CompressionLevel Optimal
}

if (Test-Path $updateSource) {
    Copy-Item -LiteralPath $updateSource -Destination (Join-Path $artifactRoot "ZSnaper-v$Version-$Runtime-Update.exe") -Force
}

if (-not [string]::IsNullOrWhiteSpace($BasePayloadDirectory)) {
    if (-not (Test-Path $BasePayloadDirectory -PathType Container)) {
        throw "Base payload directory was not found: $BasePayloadDirectory"
    }

    $baseMap = Get-RelativeFileMap $BasePayloadDirectory
    $newMap = Get-RelativeFileMap $installedPayload
    $updateRoot = Join-Path $workRoot "update-content"
    if (Test-Path $updateRoot) {
        Remove-Item -LiteralPath $updateRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $updateRoot | Out-Null

    $changedFiles = [Collections.Generic.List[object]]::new()
    foreach ($relative in $newMap.Keys) {
        $newPath = $newMap[$relative]
        $isChanged = $true
        if ($baseMap.ContainsKey($relative)) {
            $oldHash = (Get-FileHash -LiteralPath $baseMap[$relative] -Algorithm SHA256).Hash
            $newHash = (Get-FileHash -LiteralPath $newPath -Algorithm SHA256).Hash
            $isChanged = -not [string]::Equals($oldHash, $newHash, [StringComparison]::OrdinalIgnoreCase)
        }
        if ($isChanged) {
            $destination = Join-Path $updateRoot $relative.Replace('/', '\')
            New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
            Copy-Item -LiteralPath $newPath -Destination $destination -Force
            $hash = Get-FileHash -LiteralPath $newPath -Algorithm SHA256
            $changedFiles.Add([ordered]@{
                    path = $relative
                    sha256 = $hash.Hash
                    size = (Get-Item -LiteralPath $newPath).Length
                })
        }
    }

    $deletedFiles = [Collections.Generic.List[string]]::new()
    foreach ($relative in $baseMap.Keys) {
        if (-not $newMap.ContainsKey($relative)) {
            $deletedFiles.Add($relative)
        }
    }
    # Older installers copied the entire setup executable into the installation.
    # It was absent from saved payload baselines, so explicitly migrate it away.
    $legacySetup = "update/ZSnaper-Setup.exe"
    if (-not $deletedFiles.Contains($legacySetup)) {
        $deletedFiles.Add($legacySetup)
    }

    $manifest = [ordered]@{
        format = "zsnaper-update-1"
        from = $BaseVersion
        to = $Version
        files = $changedFiles.ToArray()
        delete = $deletedFiles.ToArray()
    }
    $manifestPath = Join-Path $updateRoot "update.manifest.json"
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $updatePath = Join-Path $artifactRoot "ZSnaper-v$Version-$Runtime-Update.zup"
    $updateZipPath = Join-Path $workRoot "update-package.zip"
    if (Test-Path $updatePath) {
        Remove-Item -LiteralPath $updatePath -Force
    }
    if (Test-Path $updateZipPath) {
        Remove-Item -LiteralPath $updateZipPath -Force
    }
    Compress-Archive -Path (Join-Path $updateRoot "*") -DestinationPath $updateZipPath -CompressionLevel Optimal
    Move-Item -LiteralPath $updateZipPath -Destination $updatePath -Force
}

$hashLines = foreach ($artifact in Get-ChildItem -LiteralPath $artifactRoot -File | Where-Object Name -ne "SHA256SUMS.txt") {
    $hash = Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256
    "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $artifact.Name
}
$hashLines | Set-Content -LiteralPath (Join-Path $artifactRoot "SHA256SUMS.txt") -Encoding ASCII
$savedBase = Join-Path $installerRoot ".work\base-$Version-$Runtime"
if (Test-Path $savedBase) {
    Remove-Item -LiteralPath $savedBase -Recurse -Force
}
Copy-Item -LiteralPath $installedPayload -Destination $savedBase -Recurse -Force
Write-Host "Installer artifacts written to $artifactRoot"
