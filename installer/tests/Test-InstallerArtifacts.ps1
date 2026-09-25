[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory
)

$ErrorActionPreference = "Stop"
$artifactRoot = (Resolve-Path $ArtifactDirectory).Path
$setup = Get-ChildItem -LiteralPath $artifactRoot -Filter "*-Setup.exe" -File | Select-Object -First 1
if ($null -eq $setup) {
    throw "No setup executable was found in $artifactRoot."
}

$bytes = [IO.File]::ReadAllBytes($setup.FullName)
$marker = [Text.Encoding]::ASCII.GetBytes("ZSNAPER_PAYLOAD_V1")
$footerLength = 8 + $marker.Length
if ($bytes.Length -le $footerLength) {
    throw "The setup executable is too small to contain an embedded payload."
}

$markerOffset = $bytes.Length - $marker.Length
for ($index = 0; $index -lt $marker.Length; $index++) {
    if ($bytes[$markerOffset + $index] -ne $marker[$index]) {
        throw "The setup executable has no valid payload marker."
    }
}

$payloadLength = [BitConverter]::ToInt64($bytes, $bytes.Length - $footerLength)
$payloadOffset = $bytes.Length - $footerLength - $payloadLength
if ($payloadLength -le 0 -or $payloadOffset -lt 0) {
    throw "The embedded payload range is invalid."
}

$shortcutSelfTest = Start-Process -FilePath $setup.FullName -ArgumentList "--self-test-shortcuts" -Wait -PassThru
if ($shortcutSelfTest.ExitCode -ne 0) {
    throw "The packaged installer failed its shortcut COM self-test with exit code $($shortcutSelfTest.ExitCode)."
}

$update = Get-ChildItem -LiteralPath $artifactRoot -Filter "*.zup" -File | Select-Object -First 1
if ($null -ne $update) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($update.FullName)
    try {
        $manifestEntry = $archive.GetEntry("update.manifest.json")
        if ($null -eq $manifestEntry) {
            throw "The update package has no update.manifest.json."
        }
        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try {
            $manifest = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }
        if ($manifest.format -ne "zsnaper-update-1" -or [string]::IsNullOrWhiteSpace($manifest.to)) {
            throw "The update manifest has an invalid format or target version."
        }
    }
    finally {
        $archive.Dispose()
    }
}

$fullZip = Get-ChildItem -LiteralPath $artifactRoot -Filter "*-full.zip" -File | Select-Object -First 1
if ($null -eq $fullZip) {
    throw "No full ZIP was found in $artifactRoot."
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($fullZip.FullName)
try {
    $updateFiles = @($archive.Entries | Where-Object {
        $_.FullName.Replace('\', '/') -like 'update/*' -and -not [string]::IsNullOrEmpty($_.Name)
    })
    if ($updateFiles.Count -ne 1 -or $updateFiles[0].FullName.Replace('\', '/') -ne 'update/Update.exe') {
        throw "The full ZIP must contain only update/Update.exe in its update directory."
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Installer artifact structure is valid: $($setup.Name)"
