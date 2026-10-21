$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
function Assert-Metadata { param([bool]$Condition, [string]$Message) if (!$Condition) { throw $Message } }
function Assert-MetadataRejected {
    param([scriptblock]$Action)
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Metadata $rejected 'Invalid or conflicting release tag was accepted.'
}
$names = @('GITHUB_REF', 'GITHUB_REF_TYPE', 'GITHUB_REF_NAME')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $null) }
    $root = Split-Path $PSScriptRoot -Parent
    $baseline = Get-PluginReleaseMetadata
    [xml]$props = Get-Content (Join-Path $root 'build/Plugin.props') -Raw
    Assert-Metadata ($baseline.version -ceq $props.SelectSingleNode('/Project/PropertyGroup/PluginVersion').InnerText) 'Untagged builds lost their default version.'
    foreach ($tag in @('v0.1.0.1', 'v1.2.3.4', 'v65534.65534.65534.65534')) {
        $metadata = Get-PluginReleaseMetadata -Tag $tag
        Assert-Metadata ($metadata.version -ceq $tag.Substring(1)) 'Release tag did not override the default version.'
        Assert-Metadata ($metadata.guid -ceq $baseline.guid -and $metadata.targetAbi -ceq $baseline.targetAbi -and $metadata.channel -ceq $baseline.channel) 'Tag changed plugin identity, ABI or channel.'
        Assert-ReleaseTag $tag $metadata
    }
    foreach ($invalid in @('', '0.1.0.1', 'V0.1.0.1', 'v1.2.3', 'v01.2.3.4', 'v1.2.3.65535', 'v1.2.3.4-preview', 'v../1.2.3.4')) {
        Assert-MetadataRejected { ConvertFrom-ReleaseTag $invalid }
    }
    $env:GITHUB_REF_TYPE = 'tag'; $env:GITHUB_REF_NAME = 'v0.1.0.1'; $env:GITHUB_REF = 'refs/tags/v0.1.0.1'
    Assert-Metadata ((Get-PluginReleaseMetadata).version -ceq '0.1.0.1') 'Tag event version was not automatically selected.'
    Assert-MetadataRejected { Get-PluginReleaseMetadata -Tag 'v0.1.0.2' }
    $env:GITHUB_REF = 'refs/tags/v0.1.0.2'
    Assert-MetadataRejected { Get-PluginReleaseMetadata }
    $env:GITHUB_REF_TYPE = $null; $env:GITHUB_REF_NAME = $null; $env:GITHUB_REF = 'refs/tags/v0.1.0.1'
    Assert-Metadata ((Get-PluginReleaseMetadata).version -ceq '0.1.0.1') 'Full tag ref was not resolved without the optional ref-name fields.'
    $env:GITHUB_REF_TYPE = 'branch'; $env:GITHUB_REF_NAME = 'main'; $env:GITHUB_REF = 'refs/heads/main'
    Assert-Metadata ((Get-PluginReleaseMetadata).version -ceq $baseline.version) 'Branch name was used as a release version.'
    $env:GITHUB_REF_NAME = 'v0.1.0.1'; $env:GITHUB_REF = 'refs/heads/v0.1.0.1'
    Assert-Metadata ((Get-PluginReleaseMetadata).version -ceq $baseline.version) 'A tag-shaped branch name was used as a release version.'
    Assert-Metadata ((Get-PluginReleaseMetadata -Tag 'v0.1.0.1').version -ceq '0.1.0.1') 'Explicit local tag was not accepted.'
    $fixture = Join-Path $root ('artifacts/metadata-tests/' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $fixture | Out-Null
    $changelog = Join-Path $fixture 'CHANGELOG.md'
    [IO.File]::WriteAllText($changelog, "## [Unreleased]`nNext release notes.`n## [0.1.0.1]`nExact tagged notes.`n## [0.1.0.0]`nOlder-only notes.`n")
    Assert-Metadata ((Get-PluginReleaseNotes '0.1.0.1' $fixture) -ceq 'Exact tagged notes.') 'Exact tag notes were not selected.'
    Assert-Metadata ((Get-PluginReleaseNotes '0.1.0.2' $fixture) -ceq 'Next release notes.') 'Unreleased notes were not selected.'
    [IO.File]::WriteAllText($changelog, "## [0.1.0.0]`nOlder-only notes.`n")
    $notes = Get-PluginReleaseNotes '0.1.0.2' $fixture
    Assert-Metadata ($notes.Contains('0.1.0.2') -and !$notes.Contains('Older-only notes')) 'Missing tag notes blocked release or relabeled older history.'
    Write-Host 'PASS: tag-derived versions, untagged defaults, event/ref conflicts, numeric tag validation and release-note fallbacks.'
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}
