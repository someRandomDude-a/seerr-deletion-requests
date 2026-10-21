param(
    [string]$Dotnet = 'dotnet',
    [ValidateSet('linux-x64', 'linux-arm64', 'win-x64')][string]$Runtime = 'linux-x64',
    [string]$OutputDirectory = '',
    [switch]$ContinuousIntegration
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$root = Split-Path $PSScriptRoot -Parent
$metadata = Get-PluginReleaseMetadata $root
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$asset = "DeletionQueue-$($metadata.version)-$Runtime.zip"
$zip = Join-Path $OutputDirectory $asset
$descriptor = Join-Path $OutputDirectory "DeletionQueue-$($metadata.version)-$Runtime.package.json"
foreach ($path in @($zip, "$zip.md5", "$zip.sha256", $descriptor)) {
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite $path. Use a fresh output directory." }
}
# Never package a reused stage: stale binaries or local settings must not enter a release.
$work = Join-Path $root ("artifacts/work/" + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $work 'publish'
$stage = Join-Path $work 'package'
New-Item -ItemType Directory -Force $stage | Out-Null
$project = Join-Path $root 'src/Jellyfin.Plugin.DeletionQueue/Jellyfin.Plugin.DeletionQueue.csproj'
$arguments = @('publish', $project, '-c', 'Release', '-r', $Runtime, '--self-contained', 'false', '-o', $publish)
if ($ContinuousIntegration) { $arguments += '-p:ContinuousIntegrationBuild=true' }
& $Dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Plugin publish failed.' }
$native = if ($Runtime -eq 'win-x64') { 'e_sqlite3.dll' } else { 'libe_sqlite3.so' }
$keep = @('Jellyfin.Plugin.DeletionQueue.dll', 'Jellyfin.Plugin.DeletionQueue.deps.json', 'DeletionQueue.Core.dll',
    'DeletionQueue.Infrastructure.dll', 'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.core.dll',
    'SQLitePCLRaw.provider.e_sqlite3.dll', 'SQLitePCLRaw.batteries_v2.dll', $native)
foreach ($file in $keep) {
    $path = Join-Path $publish $file
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing required dependency: $file" }
    Copy-Item -LiteralPath $path -Destination $stage
}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $stage
$installed = [ordered]@{}
foreach ($key in @('guid', 'name', 'description', 'overview', 'owner', 'category', 'version', 'targetAbi')) { $installed[$key] = $metadata[$key] }
Write-ReleaseJson (Join-Path $stage 'meta.json') $installed
$stream = [IO.File]::Open($zip, [IO.FileMode]::CreateNew)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem -LiteralPath $stage -File | Sort-Object Name)) {
        $entry = $archive.CreateEntry($file.Name, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $inputStream = $file.OpenRead(); $outputStream = $entry.Open()
        try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
    }
} finally { $archive.Dispose(); $stream.Dispose() }
$record = [ordered]@{
    guid = $metadata.guid; version = $metadata.version; targetAbi = $metadata.targetAbi
    channel = $metadata.channel; runtime = $Runtime; asset = $asset
    md5 = Get-ReleaseHash $zip MD5; sha256 = Get-ReleaseHash $zip SHA256
}
foreach ($algorithm in @('md5', 'sha256')) {
    [IO.File]::WriteAllText("$zip.$algorithm", "$($record[$algorithm])  $asset`n", [Text.UTF8Encoding]::new($false))
}
Write-ReleaseJson $descriptor $record
$null = Test-ReleasePackage $descriptor $metadata
Write-Host "Validated $zip"
