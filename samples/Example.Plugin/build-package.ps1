param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Example.Plugin.csproj'
$manifestPath = Join-Path $PSScriptRoot 'manifest.json'
$assemblyPath = Join-Path $PSScriptRoot 'bin/Release/net8.0/Example.Plugin.dll'
if (-not $OutputPath) {
    $OutputPath = Join-Path $PSScriptRoot 'dist/Example.Plugin.zsp'
}

& dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Demo plugin build failed.' }

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.entry.assembly -ne 'Example.Plugin.dll') {
    throw 'The manifest entry assembly does not match the demo output.'
}
if (-not (Test-Path -LiteralPath $assemblyPath)) { throw 'Demo plugin DLL was not built.' }

$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($OutputPath)
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
if (Test-Path -LiteralPath $OutputPath) { Remove-Item -LiteralPath $OutputPath -Force }

Add-Type -AssemblyName System.IO.Compression
$archive = [System.IO.Compression.ZipFile]::Open($OutputPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $manifestPath, 'manifest.json') | Out-Null
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $assemblyPath, 'Example.Plugin.dll') | Out-Null
}
finally {
    $archive.Dispose()
}

$hash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
Write-Output "Package: $OutputPath"
Write-Output "SHA-256: $hash"
