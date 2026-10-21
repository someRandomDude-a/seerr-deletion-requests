param(
    [string]$Dotnet = 'dotnet',
    [ValidateSet('all', 'linux-x64', 'linux-arm64', 'win-x64')][string]$Runtime = 'all',
    [string]$Tag = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
function Assert-Check { param([bool]$Condition, [string]$Message) if (!$Condition) { throw $Message } }
function Assert-Rejected {
    param([scriptblock]$Action, [string]$Message)
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Check $rejected $Message
}
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'test-release-metadata.ps1')
$metadata = Get-PluginReleaseMetadata -Root $root -Tag $Tag
Assert-ReleaseTag "v$($metadata.version)" $metadata
$otherVersion = if ($metadata.version -eq '0.0.0.0') { '0.0.0.1' } else { '0.0.0.0' }
Assert-Rejected { Assert-ReleaseTag "v$otherVersion" $metadata } 'A mismatched tag was accepted.'
foreach ($invalid in @('1.2.3', '01.2.3.4', '1.2.3.4-preview.1', '1.2.3.65535', '../1.2.3.4')) {
    Assert-Rejected { Assert-NumericVersion $invalid } "Invalid version accepted: $invalid"
}
$null = Get-PluginReleaseNotes $metadata.version
$work = Join-Path $root ("artifacts/release-tests/" + [guid]::NewGuid().ToString('N'))
$packages = Join-Path $work 'packages'
$catalogs = Join-Path $work 'catalogs'
# Conditional pipeline output unwraps a single string; keep [0] indexing runtime IDs, not characters.
[string[]]$runtimes = if ($Runtime -eq 'all') { @('linux-x64', 'linux-arm64', 'win-x64') } else { @($Runtime) }
$expectedFirstRuntime = if ($Runtime -eq 'all') { 'linux-x64' } else { $Runtime }
Assert-Check ($runtimes -is [array] -and $runtimes[0] -ceq $expectedFirstRuntime) 'Runtime selection must preserve complete runtime identifiers in an array.'
foreach ($rid in $runtimes) {
    & (Join-Path $PSScriptRoot 'package.ps1') -Dotnet $Dotnet -Runtime $rid -OutputDirectory $packages -Tag "v$($metadata.version)"
    $descriptor = Join-Path $packages "DeletionQueue-$($metadata.version)-$rid.package.json"
    $null = Test-ReleasePackage $descriptor $metadata
}
Assert-Rejected { & (Join-Path $PSScriptRoot 'package.ps1') -Dotnet $Dotnet -Runtime $runtimes[0] -OutputDirectory $packages -Tag "v$($metadata.version)" } 'Existing output was overwritten.'
$firstDescriptor = Join-Path $packages "DeletionQueue-$($metadata.version)-$($runtimes[0]).package.json"
$first = Read-ReleaseJson $firstDescriptor
$zip = Join-Path $packages $first.asset
$originalZip = [IO.File]::ReadAllBytes($zip)
try {
    $broken = [byte[]]$originalZip.Clone(); $broken[0] = $broken[0] -bxor 1
    [IO.File]::WriteAllBytes($zip, $broken)
    Assert-Rejected { Test-ReleasePackage $firstDescriptor $metadata } 'Tampered ZIP was accepted.'
} finally { [IO.File]::WriteAllBytes($zip, $originalZip) }
# Hashes alone cannot prove contents/architecture. Rewrite checksums after introducing an extra key file.
try {
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Update)
    try { $null = $archive.CreateEntry('keys.json') } finally { $archive.Dispose() }
    $bad = Read-ReleaseJson $firstDescriptor
    $bad.md5 = Get-ReleaseHash $zip MD5; $bad.sha256 = Get-ReleaseHash $zip SHA256
    Write-ReleaseJson $firstDescriptor $bad
    foreach ($hash in @('md5', 'sha256')) { [IO.File]::WriteAllText("$zip.$hash", "$($bad[$hash])  $($bad.asset)`n") }
    Assert-Rejected { Test-ReleasePackage $firstDescriptor $metadata } 'A secret/extra archive entry was accepted.'
} finally {
    [IO.File]::WriteAllBytes($zip, $originalZip)
    Write-ReleaseJson $firstDescriptor $first
    foreach ($hash in @('md5', 'sha256')) { [IO.File]::WriteAllText("$zip.$hash", "$($first[$hash])  $($first.asset)`n") }
}
Write-Host 'PASS: version/tag checks, native package contents, assembly versions, checksums, tamper/secret rejection and no-clobber packaging.'
if ($Runtime -ne 'all') { return }
$repository = 'someRandomDude-a/seerr-deletion-requests'
$timestamp = '2026-10-21T00:00:00Z'
function Update-Catalogs {
    & (Join-Path $PSScriptRoot 'update-manifests.ps1') -PackagesDirectory $packages -CatalogDirectory $catalogs `
        -Repository $repository -Tag "v$($metadata.version)" -Timestamp $timestamp
}
Update-Catalogs
foreach ($rid in $runtimes) {
    $stable = Read-ReleaseJson (Join-Path $catalogs "stable/manifest-$rid.json")
    $preview = Read-ReleaseJson (Join-Path $catalogs "preview/manifest-$rid.json")
    $expectedStableCount = if ($metadata.channel -eq 'stable') { 1 } else { 0 }
    Assert-Check ($stable[0].versions.Count -eq $expectedStableCount) 'Preview leaked into the stable catalog.'
    Assert-Check ($preview[0].versions.Count -eq 1) 'Preview catalog must include the current release.'
    $record = Read-ReleaseJson (Join-Path $packages "DeletionQueue-$($metadata.version)-$rid.package.json")
    Assert-Check ($preview[0].versions[0].checksum -ceq $record.md5) 'Catalog did not use Jellyfin MD5.'
    Assert-Check ($preview[0].versions[0].sourceUrl -ceq "https://github.com/$repository/releases/download/v$($metadata.version)/$($record.asset)") 'Catalog URL mismatch.'
}
foreach ($channel in @('stable', 'preview')) {
    foreach ($rid in $runtimes) {
        $path = Join-Path $catalogs "$channel/manifest-$rid.json"
        $catalog = Read-ReleaseJson $path
        $catalog[0].versions += [ordered]@{
            version = $otherVersion; targetAbi = $metadata.targetAbi; changelog = 'Synthetic retained release.'
            sourceUrl = "https://github.com/$repository/releases/download/v$otherVersion/DeletionQueue-$otherVersion-$rid.zip"
            checksum = ('a' * 32); timestamp = '2026-01-01T00:00:00Z'
        }
        Write-ReleaseJson $path $catalog
    }
}
Update-Catalogs
$before = @{}
foreach ($file in (Get-ChildItem $catalogs -Filter '*.json' -Recurse -File)) {
    $catalog = Read-ReleaseJson $file.FullName
    if (Get-Command Test-Json -ErrorAction SilentlyContinue) {
        Assert-Check (Test-Json -Json ([IO.File]::ReadAllText($file.FullName)) -SchemaFile (Join-Path $root 'manifests/repository.schema.json')) 'Catalog failed JSON schema validation.'
    }
    Assert-Check (@($catalog[0].versions | Where-Object { $_.version -eq $otherVersion }).Count -eq 1) 'Historical version was omitted.'
    $before[$file.FullName] = Get-ReleaseHash $file.FullName SHA256
}
Update-Catalogs
foreach ($path in $before.Keys) { Assert-Check ((Get-ReleaseHash $path SHA256) -ceq $before[$path]) 'Retry changed an existing catalog.' }
$previewPath = Join-Path $catalogs 'preview/manifest-linux-x64.json'
$originalCatalog = [IO.File]::ReadAllBytes($previewPath)
try {
    $catalog = Read-ReleaseJson $previewPath
    $catalog[0].versions[0].checksum = 'b' * 32
    Write-ReleaseJson $previewPath $catalog
    Assert-Rejected { Update-Catalogs } 'Existing published version was silently rewritten.'
} finally { [IO.File]::WriteAllBytes($previewPath, $originalCatalog) }
$backup = [IO.File]::ReadAllBytes($firstDescriptor)
try {
    $d = Read-ReleaseJson $firstDescriptor; $d.sha256 = '0' * 64; Write-ReleaseJson $firstDescriptor $d
    Assert-Rejected { Update-Catalogs } 'Manifest accepted mismatched package checksums.'
} finally { [IO.File]::WriteAllBytes($firstDescriptor, $backup) }
foreach ($path in $before.Keys) { Assert-Check ((Get-ReleaseHash $path SHA256) -ceq $before[$path]) 'Rejected update partially modified catalogs.' }
Write-Host 'PASS: architecture-specific URLs, MD5 compatibility, channel isolation, history retention, idempotency and immutable version rejection.'
& (Join-Path $PSScriptRoot 'test-publisher.ps1') -PackagesDirectory $packages -Tag "v$($metadata.version)"
& (Join-Path $PSScriptRoot 'test-catalog-upgrades.ps1') -PackagesDirectory $packages -Tag "v$($metadata.version)" -Dotnet $Dotnet
Write-Host "Release fixtures retained at $work"
