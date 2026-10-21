param(
    [Parameter(Mandatory)][string]$PackagesDirectory,
    [Parameter(Mandatory)][string]$Tag
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
if ($env:GITHUB_ACTIONS -ne 'true' -or !$env:GH_TOKEN -or !$env:GITHUB_REPOSITORY) {
    throw 'Publishing is only supported inside the protected GitHub tag workflow.'
}
$metadata = Get-PluginReleaseMetadata -Tag $Tag
Assert-ReleaseTag $Tag $metadata
$repository = $env:GITHUB_REPOSITORY
if ($repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository.' }
$head = & git rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $head.Trim() -cne $env:GITHUB_SHA) { throw 'Checkout does not match the event commit.' }
$tagCommit = & git rev-parse "$Tag^{commit}"
if ($LASTEXITCODE -ne 0 -or $tagCommit.Trim() -cne $head.Trim()) { throw 'Tag does not match the checkout.' }
$timestamp = & git show -s --format=%cI HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine release commit timestamp.' }
$timestamp = ([DateTimeOffset]::Parse($timestamp.Trim())).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
$PackagesDirectory = [IO.Path]::GetFullPath($PackagesDirectory)
$work = Join-Path (Split-Path $PSScriptRoot -Parent) ("artifacts/publish/" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null

function Invoke-Gh {
    param([string[]]$Arguments)
    $result = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI failed: $($Arguments[0])" }
    return ($result -join "`n")
}
function Get-GitHubJson {
    param([string]$Path, [switch]$AllowMissing)
    $result = & gh api -i $Path 2>$null
    $exitCode = $LASTEXITCODE
    $text = $result -join "`n"
    if ($exitCode -ne 0) {
        if ($AllowMissing -and $text -match '^HTTP/\S+ 404\b') { return $null }
        throw "GitHub API read failed: $Path"
    }
    $split = $text.IndexOf("`n`n")
    if ($split -lt 0) { throw 'Malformed GitHub API response.' }
    return ConvertTo-ReleaseData (ConvertFrom-Json -InputObject $text.Substring($split + 2))
}
function Send-GitHubJson {
    param([string]$Method, [string]$Path, $Body)
    $file = Join-Path $work ([guid]::NewGuid().ToString('N') + '.json')
    Write-ReleaseJson $file $Body
    $result = Invoke-Gh @('api', '--method', $Method, $Path, '--input', $file)
    return ConvertTo-ReleaseData (ConvertFrom-Json -InputObject $result)
}

# Use the authenticated API rather than unauthenticated git ls-remote (also works for private repositories).
$remoteTag = Get-GitHubJson "repos/$repository/git/ref/tags/$Tag"
$remoteObject = $remoteTag.object
$depth = 0
while ($remoteObject.type -eq 'tag' -and $depth -lt 10) {
    $peeled = Get-GitHubJson "repos/$repository/git/tags/$($remoteObject.sha)"
    $remoteObject = $peeled.object
    $depth++
}
if ($remoteObject.type -ne 'commit' -or $remoteObject.sha -cne $head.Trim()) { throw 'Remote tag moved; refusing to publish.' }

$catalogDirectory = Join-Path $work 'catalog'
$reference = Get-GitHubJson "repos/$repository/git/ref/heads/manifests" -AllowMissing
$parent = $null
$baseTree = $null
if ($null -ne $reference) {
    $parent = $reference.object.sha
    $commit = Get-GitHubJson "repos/$repository/git/commits/$parent"
    $baseTree = $commit.tree.sha
    foreach ($channel in @('stable', 'preview')) {
        foreach ($runtime in @('linux-x64', 'linux-arm64', 'win-x64')) {
            $path = "$channel/manifest-$runtime.json"
            # Missing files on an existing branch are not silently replaced: history may have been omitted.
            $content = Get-GitHubJson "repos/$repository/contents/${path}?ref=$parent"
            if ($content.encoding -ne 'base64') { throw 'Unexpected catalog encoding.' }
            $destination = Join-Path $catalogDirectory $path
            New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
            [IO.File]::WriteAllBytes($destination, [Convert]::FromBase64String($content.content))
        }
    }
}
& (Join-Path $PSScriptRoot 'update-manifests.ps1') -PackagesDirectory $PackagesDirectory -CatalogDirectory $catalogDirectory `
    -Repository $repository -Tag $Tag -Timestamp $timestamp

$notesFile = Join-Path $work 'release-notes.md'
$notes = Get-PluginReleaseNotes $metadata.version
$notes += "`n`nInstall using the architecture-specific $($metadata.channel) Jellyfin repository catalog. See https://github.com/$repository/blob/$Tag/docs/releases.md."
[IO.File]::WriteAllText($notesFile, $notes, [Text.UTF8Encoding]::new($false))
$release = Get-GitHubJson "repos/$repository/releases/tags/$Tag" -AllowMissing
if ($null -eq $release) {
    $arguments = @('release', 'create', $Tag, '--repo', $repository, '--verify-tag', '--draft',
        '--title', "$($metadata.name) $($metadata.version)", '--notes-file', $notesFile)
    if ($metadata.channel -eq 'preview') { $arguments += '--prerelease' }
    $null = Invoke-Gh $arguments
    $release = Get-GitHubJson "repos/$repository/releases/tags/$Tag"
} elseif ([bool]$release.prerelease -ne ($metadata.channel -eq 'preview')) {
    throw 'Existing release channel disagrees with metadata.'
}

# Include the version's catalog snapshots with the immutable binaries; the manifests branch retains all history.
$upload = @{}
foreach ($descriptor in (Get-ChildItem -LiteralPath $PackagesDirectory -Filter '*.package.json' -File)) {
    $package = Test-ReleasePackage $descriptor.FullName $metadata
    foreach ($name in @($package.asset, "$($package.asset).md5", "$($package.asset).sha256", $descriptor.Name)) {
        $upload[$name] = Join-Path $PackagesDirectory $name
    }
}
foreach ($runtime in @('linux-x64', 'linux-arm64', 'win-x64')) {
    $name = "manifest-$runtime.json"
    # A per-release snapshot contains only this version, not history that can change on a later retry.
    $snapshot = Read-ReleaseJson (Join-Path $catalogDirectory "$($metadata.channel)/$name")
    $snapshot[0].versions = @($snapshot[0].versions | Where-Object { $_.version -eq $metadata.version })
    $path = Join-Path $work $name
    Write-ReleaseJson $path $snapshot
    $upload[$name] = $path
}
$existingAssets = @($release.assets)
$compare = Join-Path $work 'existing'
New-Item -ItemType Directory $compare | Out-Null
foreach ($name in ($upload.Keys | Sort-Object)) {
    $existing = @($existingAssets | Where-Object { $_.name -ceq $name })
    if ($existing.Count -gt 0) {
        $null = Invoke-Gh @('release', 'download', $Tag, '--repo', $repository, '--pattern', $name, '--dir', $compare)
        if ((Get-ReleaseHash (Join-Path $compare $name) SHA256) -cne (Get-ReleaseHash $upload[$name] SHA256)) {
            throw "Existing release asset differs: $name. Never overwrite published artifacts; use a new version."
        }
    } else {
        if (!$release.draft) { throw "Published release is missing asset $name; do not amend an immutable published release." }
        $null = Invoke-Gh @('release', 'upload', $Tag, $upload[$name], '--repo', $repository)
    }
}
if ($release.draft) {
    $null = Invoke-Gh @('release', 'edit', $Tag, '--repo', $repository, '--draft=false', "--prerelease=$($metadata.channel -eq 'preview')", '--latest=false')
}

# Advertise only after all assets are uploaded and the release is public.
$treeEntries = @()
foreach ($channel in @('stable', 'preview')) {
    foreach ($runtime in @('linux-x64', 'linux-arm64', 'win-x64')) {
        $path = "$channel/manifest-$runtime.json"
        $treeEntries += @{ path = $path; mode = '100644'; type = 'blob'; content = [IO.File]::ReadAllText((Join-Path $catalogDirectory $path)) }
    }
}
$root = Split-Path $PSScriptRoot -Parent
$treeEntries += @{ path = 'README.md'; mode = '100644'; type = 'blob'; content = [IO.File]::ReadAllText((Join-Path $root 'manifests/README.md')) }
$treeEntries += @{ path = 'repository.schema.json'; mode = '100644'; type = 'blob'; content = [IO.File]::ReadAllText((Join-Path $root 'manifests/repository.schema.json')) }
$treeBody = @{ tree = $treeEntries }
if ($baseTree) { $treeBody.base_tree = $baseTree }
$tree = Send-GitHubJson POST "repos/$repository/git/trees" $treeBody
if ($baseTree -eq $tree.sha) { Write-Host 'Catalog is already current.'; return }
$commitBody = @{ message = "Publish deletion queue $($metadata.version) ($($metadata.channel))"; tree = $tree.sha; parents = @() }
if ($parent) { $commitBody.parents = @($parent) }
$newCommit = Send-GitHubJson POST "repos/$repository/git/commits" $commitBody
if ($parent) {
    $null = Send-GitHubJson PATCH "repos/$repository/git/refs/heads/manifests" @{ sha = $newCommit.sha; force = $false }
} else {
    $null = Send-GitHubJson POST "repos/$repository/git/refs" @{ ref = 'refs/heads/manifests'; sha = $newCommit.sha }
}
Write-Host "Published $Tag and fast-forwarded the architecture-specific repository catalogs."
