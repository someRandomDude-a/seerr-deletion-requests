param(
    [Parameter(Mandatory)][string]$PackagesDirectory,
    [Parameter(Mandatory)][string]$CatalogDirectory,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$Timestamp
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$metadata = Get-PluginReleaseMetadata -Tag $Tag
Assert-ReleaseTag $Tag $metadata
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Expected a GitHub owner/repository.' }
if ($Timestamp -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$') { throw 'Expected a UTC release timestamp.' }
$null = [DateTimeOffset]::Parse($Timestamp, [Globalization.CultureInfo]::InvariantCulture)
$notes = Get-PluginReleaseNotes $metadata.version
$runtimes = @('linux-x64', 'linux-arm64', 'win-x64')
$descriptors = @(Get-ChildItem -LiteralPath $PackagesDirectory -Filter '*.package.json' -File)
if ($descriptors.Count -ne $runtimes.Count) { throw 'All three runtime packages are required before publishing catalogs.' }
$packages = @{}
foreach ($descriptor in $descriptors) {
    $package = Test-ReleasePackage $descriptor.FullName $metadata
    if ($packages.ContainsKey($package.runtime)) { throw 'Duplicate runtime package.' }
    $packages[$package.runtime] = $package
}
# Validate everything before changing any output file. Existing version URLs/checksums are immutable.
$pending = [ordered]@{}
foreach ($channel in @('stable', 'preview')) {
    foreach ($runtime in $runtimes) {
        $path = Join-Path $CatalogDirectory "$channel/manifest-$runtime.json"
        $versions = @()
        if (Test-Path -LiteralPath $path) {
            $old = Read-ReleaseJson $path
            if ($old -isnot [array] -or $old.Count -ne 1 -or $old[0].guid -ne $metadata.guid -or $old[0].name -ne $metadata.name) {
                throw "Invalid or changed catalog identity: $path"
            }
            $versions = @($old[0].versions)
            $seen = @{}
            foreach ($version in $versions) {
                Assert-NumericVersion $version.version
                Assert-NumericVersion $version.targetAbi
                if ($seen.ContainsKey($version.version) -or $version.checksum -notmatch '^[a-f0-9]{32}$' -or
                    $version.sourceUrl -notmatch '^https://github\.com/[^/]+/[^/]+/releases/download/v[^/]+/[^/]+\.zip$' -or
                    $version.sourceUrl -cne "https://github.com/$Repository/releases/download/v$($version.version)/DeletionQueue-$($version.version)-$runtime.zip" -or
                    [string]::IsNullOrWhiteSpace($version.changelog) -or
                    $version.timestamp -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$') { throw 'Invalid catalog history.' }
                $seen[$version.version] = $true
            }
        }
        if ($channel -eq 'preview' -or $metadata.channel -eq 'stable') {
            $package = $packages[$runtime]
            $entry = [ordered]@{
                version = $metadata.version; changelog = $notes; targetAbi = $metadata.targetAbi
                sourceUrl = "https://github.com/$Repository/releases/download/$Tag/$($package.asset)"
                checksum = $package.md5; timestamp = $Timestamp
            }
            $existing = @($versions | Where-Object { $_.version -eq $metadata.version })
            if ($existing.Count -gt 0) {
                foreach ($key in $entry.Keys) {
                    if ($existing[0][$key] -cne $entry[$key]) { throw "Refusing to rewrite published version $($metadata.version) in $path" }
                }
            } else { $versions += $entry }
        } elseif (@($versions | Where-Object { $_.version -eq $metadata.version }).Count -gt 0) {
            throw 'A preview version is already present in the stable catalog; refusing to hide the inconsistency.'
        }
        $catalog = [ordered]@{}
        foreach ($key in @('guid', 'name', 'description', 'overview', 'owner', 'category')) { $catalog[$key] = $metadata[$key] }
        $catalog.versions = @($versions | Sort-Object { [version]$_.version } -Descending)
        $pending[$path] = @($catalog)
    }
}
foreach ($path in $pending.Keys) { Write-ReleaseJson $path $pending[$path] }
Write-Host 'Validated and updated six architecture/channel catalogs; prior versions retained.'
