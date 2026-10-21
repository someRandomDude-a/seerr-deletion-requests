param(
    [Parameter(Mandatory)][string]$PackagesDirectory,
    [Parameter(Mandatory)][string]$Tag,
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$metadata = Get-PluginReleaseMetadata -Tag $Tag
$names = @('GITHUB_REF', 'GITHUB_REF_TYPE', 'GITHUB_REF_NAME')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    # These are local fixtures for two tags on identical source, not new Git tags or remote releases.
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $null) }
    $root = Split-Path $PSScriptRoot -Parent
    $propsPath = Join-Path $root 'build/Plugin.props'
    $propsHash = Get-ReleaseHash $propsPath SHA256
    $work = Join-Path $root ('artifacts/upgrade-tests/' + [guid]::NewGuid().ToString('N'))
    $otherPackages = Join-Path $work 'packages'
    $catalogs = Join-Path $work 'catalogs'
    $otherVersion = if ($metadata.version -eq '0.0.0.0') { '0.0.0.1' } else { '0.0.0.0' }
    foreach ($runtime in @('linux-x64', 'linux-arm64', 'win-x64')) {
        & (Join-Path $PSScriptRoot 'package.ps1') -Runtime $runtime -Tag "v$otherVersion" -Dotnet $Dotnet -OutputDirectory $otherPackages
    }
    $oldVersion = $otherVersion; $oldPackages = $otherPackages
    $newVersion = $metadata.version; $newPackages = $PackagesDirectory
    if ([version]$otherVersion -gt [version]$metadata.version) {
        $oldVersion = $metadata.version; $oldPackages = $PackagesDirectory
        $newVersion = $otherVersion; $newPackages = $otherPackages
    }
    $repository = 'someRandomDude-a/seerr-deletion-requests'
    $update = Join-Path $PSScriptRoot 'update-manifests.ps1'
    & $update -PackagesDirectory $oldPackages -CatalogDirectory $catalogs -Repository $repository -Tag "v$oldVersion" -Timestamp '2026-01-01T00:00:00Z'
    $previous = @{}
    foreach ($file in (Get-ChildItem $catalogs -Recurse -Filter '*.json' -File)) {
        $previous[$file.FullName] = Read-ReleaseJson $file.FullName
    }
    & $update -PackagesDirectory $newPackages -CatalogDirectory $catalogs -Repository $repository -Tag "v$newVersion" -Timestamp '2026-10-21T00:00:00Z'
    $hashes = @{}
    foreach ($file in (Get-ChildItem $catalogs -Recurse -Filter '*.json' -File)) {
        $catalog = Read-ReleaseJson $file.FullName
        $expected = if ($file.Directory.Name -eq 'preview' -or $metadata.channel -eq 'stable') { 2 } else { 0 }
        if ($catalog[0].guid -cne $metadata.guid -or $catalog[0].versions.Count -ne $expected) { throw 'Upgrade changed identity or lost version/channel history.' }
        if ($expected -gt 0) {
            if ($catalog[0].versions[0].version -cne $newVersion -or $catalog[0].versions[1].version -cne $oldVersion) {
                throw 'Jellyfin cannot discover the higher numeric tag version first.'
            }
            $old = ConvertTo-Json -InputObject $previous[$file.FullName][0].versions[0] -Depth 10 -Compress
            $retained = ConvertTo-Json -InputObject $catalog[0].versions[1] -Depth 10 -Compress
            if ($old -cne $retained) { throw 'Upgrade rewrote an older version, checksum, ABI, changelog, timestamp or asset URL.' }
        }
        $hashes[$file.FullName] = Get-ReleaseHash $file.FullName SHA256
    }
    & $update -PackagesDirectory $newPackages -CatalogDirectory $catalogs -Repository $repository -Tag "v$newVersion" -Timestamp '2026-10-21T00:00:00Z'
    foreach ($path in $hashes.Keys) {
        if ((Get-ReleaseHash $path SHA256) -cne $hashes[$path]) { throw 'Upgrade retry changed the catalog.' }
    }
    if ((Get-ReleaseHash $propsPath SHA256) -cne $propsHash) { throw 'Tag builds edited the source version file.' }
    Write-Host 'PASS: two real tag-version package sets, consistent assembly/installed/catalog versions, numeric upgrade discovery, unchanged identity, preserved history and idempotent retries.'
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}
