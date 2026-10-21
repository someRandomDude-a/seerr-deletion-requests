Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression

function ConvertTo-ReleaseData {
    param($Value)
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $result = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) { $result[$property.Name] = ConvertTo-ReleaseData $property.Value }
        return $result
    }
    if ($Value -is [array]) {
        $items = @(foreach ($item in $Value) { ConvertTo-ReleaseData $item })
        return ,$items
    }
    return $Value
}

function ConvertFrom-ReleaseJsonElement {
    param($Element)
    switch ($Element.ValueKind.ToString()) {
        'Object' {
            $result = [ordered]@{}
            foreach ($property in $Element.EnumerateObject()) { $result[$property.Name] = ConvertFrom-ReleaseJsonElement $property.Value }
            return $result
        }
        'Array' {
            $items = @(foreach ($item in $Element.EnumerateArray()) { ConvertFrom-ReleaseJsonElement $item })
            return ,$items
        }
        'String' { return $Element.GetString() }
        'True' { return $true }
        'False' { return $false }
        'Null' { return $null }
        'Number' { return $Element.GetDecimal() }
        default { throw 'Unsupported JSON value.' }
    }
}

function Read-ReleaseJson {
    param([string]$Path)
    $text = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    if ($PSVersionTable.PSVersion.Major -ge 7) {
        # ConvertFrom-Json on some PowerShell releases turns ISO strings into DateTime values.
        # That changes immutable catalog bytes and breaks retries. Preserve JSON strings verbatim.
        $document = [System.Text.Json.JsonDocument]::Parse($text)
        try { $converted = ConvertFrom-ReleaseJsonElement $document.RootElement } finally { $document.Dispose() }
    } else {
        $parsed = ConvertFrom-Json -InputObject $text
        $converted = ConvertTo-ReleaseData $parsed
    }
    if ($text.TrimStart().StartsWith('[')) { return ,@($converted) }
    return $converted
}

function Get-PluginReleaseMetadata {
    param([string]$Root = (Split-Path $PSScriptRoot -Parent), [string]$Tag = '')
    $eventTag = Get-ReleaseEventTag
    if (!$Tag) { $Tag = $eventTag }
    elseif ($eventTag -and $Tag -cne $eventTag) { throw 'Requested tag does not match the triggering Git tag.' }
    [xml]$xml = Get-Content -LiteralPath (Join-Path $Root 'build/Plugin.props') -Raw -Encoding UTF8
    $p = $xml.Project.PropertyGroup
    $metadata = [ordered]@{
        version = $p.SelectSingleNode('PluginVersion').InnerText; guid = [string]$p.PluginGuid
        name = [string]$p.PluginDisplayName; owner = [string]$p.PluginOwner
        category = [string]$p.PluginCategory; overview = [string]$p.PluginOverview
        description = [string]$p.PluginDescription; channel = [string]$p.PluginReleaseChannel
        targetAbi = [string]$p.PluginTargetAbi; sdkVersion = [string]$p.PluginSdkVersion
    }
    if ($Tag) { $metadata.version = ConvertFrom-ReleaseTag $Tag }
    foreach ($field in $metadata.Keys) {
        if ([string]::IsNullOrWhiteSpace($metadata[$field])) { throw "Missing release metadata: $field" }
    }
    Assert-NumericVersion $metadata.version
    Assert-NumericVersion $metadata.targetAbi
    if ($metadata.sdkVersion -ne ([version]$metadata.targetAbi).ToString(3)) { throw 'SDK version and target ABI disagree.' }
    if ($metadata.channel -notin @('stable', 'preview')) { throw 'Release channel must be stable or preview.' }
    # Changing these breaks installed-plugin upgrade identity / directory selection.
    if ($metadata.guid -ne '3de88ba1-6d5c-49f5-854b-65a72f473f21' -or $metadata.name -ne 'Deletion queue') {
        throw 'Installed plugin identity must not change during a release.'
    }
    return $metadata
}

function Get-ReleaseEventTag {
    # Use the exact event ref, not git describe: several tags can point at the same commit.
    if ($env:GITHUB_REF_TYPE -eq 'tag') {
        if (!$env:GITHUB_REF_NAME) { throw 'Missing triggering tag name.' }
        if ($env:GITHUB_REF -and $env:GITHUB_REF -cne "refs/tags/$($env:GITHUB_REF_NAME)") {
            throw 'Triggering Git tag refs disagree.'
        }
        return $env:GITHUB_REF_NAME
    }
    if ($env:GITHUB_REF -and $env:GITHUB_REF.StartsWith('refs/tags/', [StringComparison]::Ordinal)) {
        return $env:GITHUB_REF.Substring('refs/tags/'.Length)
    }
    return ''
}

function ConvertFrom-ReleaseTag {
    param([string]$Tag)
    if (!$Tag.StartsWith('v', [StringComparison]::Ordinal)) { throw 'Release tags must start with lowercase v.' }
    $version = $Tag.Substring(1)
    Assert-NumericVersion $version
    return $version
}

function Assert-NumericVersion {
    param([string]$Version)
    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or
        @($Version.Split('.') | Where-Object { [long]$_ -gt 65534 }).Count -gt 0) {
        throw "Expected four numeric version components (0..65534): $Version"
    }
}

function Assert-ReleaseTag {
    param([string]$Tag, [System.Collections.IDictionary]$Metadata)
    $version = ConvertFrom-ReleaseTag $Tag
    if ($version -cne $Metadata.version) { throw "Package version $($Metadata.version) does not match tag $Tag." }
}

function Get-PluginReleaseNotes {
    param([string]$Version, [string]$Root = (Split-Path $PSScriptRoot -Parent))
    Assert-NumericVersion $Version
    $text = Get-Content -LiteralPath (Join-Path $Root 'CHANGELOG.md') -Raw -Encoding UTF8
    $pattern = '(?ms)^## \[' + [regex]::Escape($Version) + '\][^\r\n]*\r?\n(.*?)(?=^## \[|\z)'
    $match = [regex]::Match($text, $pattern)
    if ($match.Success -and ![string]::IsNullOrWhiteSpace($match.Groups[1].Value)) { return $match.Groups[1].Value.Trim() }
    $unreleased = [regex]::Match($text, '(?ims)^## \[Unreleased\][^\r\n]*\r?\n(.*?)(?=^## \[|\z)')
    if ($unreleased.Success -and ![string]::IsNullOrWhiteSpace($unreleased.Groups[1].Value)) { return $unreleased.Groups[1].Value.Trim() }
    # Tags do not require a matching source-file version bump or a relabeled older changelog entry.
    return "Release $Version, built from its Git tag. See README.md and CHANGELOG.md in the tagged source for safety limitations and project history."
}

function Write-ReleaseJson {
    param([string]$Path, $Value)
    $parent = Split-Path $Path -Parent
    if ($parent) { New-Item -ItemType Directory -Force $parent | Out-Null }
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 30) + "`n", [Text.UTF8Encoding]::new($false))
}

function Get-ReleaseHash {
    param([string]$Path, [ValidateSet('MD5', 'SHA256')][string]$Algorithm)
    return (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash.ToLowerInvariant()
}

function ConvertFrom-GitHubApiResponse {
    param([string]$Text, [int]$ExitCode, [string]$Path, [switch]$AllowMissing)
    $text = $Text.Replace("`r`n", "`n").TrimStart([char]0xfeff)
    $status = [regex]::Match($text, '\AHTTP/\S+\s+(\d{3})\b[^\n]*\n')
    $httpStatus = $null
    $body = $text
    if ($status.Success) {
        $httpStatus = [int]$status.Groups[1].Value
        $split = $text.IndexOf("`n`n")
        if ($split -lt 0) { throw "Malformed GitHub API response: $Path" }
        $body = $text.Substring($split + 2)
    }
    $json = $null
    try { $json = ConvertTo-ReleaseData (ConvertFrom-Json -InputObject $body) } catch {
        if ($ExitCode -eq 0 -and $httpStatus -ge 200 -and $httpStatus -lt 300) { throw "Malformed GitHub API JSON: $Path" }
    }
    # Some CLI/error-output paths expose only GitHub's JSON error envelope, not the status line.
    # A real HTTP status always wins over any field in the body (e.g. HTTP 403 with status=404).
    if ($null -eq $httpStatus -and $ExitCode -ne 0 -and $null -ne $json -and $json -is [System.Collections.IDictionary] -and $json.Contains('status')) {
        $code = 0
        if ([int]::TryParse([string]$json['status'], [ref]$code) -and $code -ge 400 -and $code -le 599) { $httpStatus = $code }
    }
    if ($AllowMissing -and $ExitCode -ne 0 -and $httpStatus -eq 404) { return $null }
    if ($ExitCode -ne 0 -or $null -eq $httpStatus -or $httpStatus -lt 200 -or $httpStatus -ge 300) {
        $label = if ($null -eq $httpStatus) { 'unknown' } else { [string]$httpStatus }
        throw "GitHub API read failed: $Path (HTTP $label; gh exit $ExitCode)."
    }
    if ($null -eq $json) { throw "Missing GitHub API JSON: $Path" }
    return $json
}

function Assert-NativeLibrary {
    param([string]$Path, [string]$Runtime)
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($Runtime -eq 'win-x64') {
        if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) { throw 'Expected a Windows PE library.' }
        $offset = [BitConverter]::ToInt32($bytes, 60)
        if ($offset -lt 0 -or $offset + 6 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, $offset) -ne 0x4550 -or
            [BitConverter]::ToUInt16($bytes, $offset + 4) -ne 0x8664) { throw 'Wrong Windows native architecture.' }
    } else {
        $machine = if ($Runtime -eq 'linux-arm64') { 183 } else { 62 }
        if ($bytes.Length -lt 20 -or [BitConverter]::ToUInt32($bytes, 0) -ne 0x464c457f -or $bytes[4] -ne 2 -or
            $bytes[5] -ne 1 -or [BitConverter]::ToUInt16($bytes, 18) -ne $machine) { throw "Wrong ELF native architecture for $Runtime" }
    }
}

function Assert-AssemblyIdentity {
    param([string]$Path, [System.Collections.IDictionary]$Metadata)
    # Inspect PE metadata without loading/executing a plugin or requiring Jellyfin assemblies.
    # CI uses PowerShell 7; Windows PowerShell 5.1 still validates numeric assembly versions below.
    if ($PSVersionTable.PSVersion.Major -lt 7) { return }
    Add-Type -AssemblyName System.Reflection.Metadata
    $stream = [IO.File]::OpenRead($Path)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $found = @{}
        foreach ($handle in $reader.GetAssemblyDefinition().GetCustomAttributes()) {
            $attribute = $reader.GetCustomAttribute($handle)
            if ($attribute.Constructor.Kind.ToString() -ne 'MemberReference') { continue }
            $member = $reader.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
            if ($member.Parent.Kind.ToString() -ne 'TypeReference') { continue }
            $type = $reader.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$member.Parent)
            $name = $reader.GetString($type.Namespace) + '.' + $reader.GetString($type.Name)
            if ($name -notin @('System.Reflection.AssemblyTitleAttribute', 'System.Reflection.AssemblyDescriptionAttribute', 'System.Runtime.InteropServices.GuidAttribute')) { continue }
            if ($found.ContainsKey($name)) { throw 'Duplicate assembly identity attribute.' }
            $blob = $reader.GetBlobReader($attribute.Value)
            if ($blob.ReadUInt16() -ne 1) { throw 'Invalid assembly attribute.' }
            $found[$name] = $blob.ReadSerializedString()
        }
        if ($found['System.Reflection.AssemblyTitleAttribute'] -cne $Metadata.name -or
            $found['System.Reflection.AssemblyDescriptionAttribute'] -cne $Metadata.description -or
            $found['System.Runtime.InteropServices.GuidAttribute'] -cne $Metadata.guid) {
            throw 'Compiled plugin GUID/name/description do not match the installed/repository manifests.'
        }
    } finally { $pe.Dispose(); $stream.Dispose() }
}

function Test-ReleasePackage {
    param([string]$DescriptorPath, [System.Collections.IDictionary]$Metadata)
    $d = Read-ReleaseJson $DescriptorPath
    foreach ($key in @('guid', 'version', 'targetAbi', 'channel')) {
        if ($d[$key] -cne $Metadata[$key]) { throw "Package $key disagrees with release metadata." }
    }
    if ($d.runtime -notin @('linux-x64', 'linux-arm64', 'win-x64')) { throw 'Unsupported package runtime.' }
    $expectedName = "DeletionQueue-$($Metadata.version)-$($d.runtime).zip"
    if ($d.asset -cne $expectedName) { throw 'Invalid release asset filename.' }
    $zip = Join-Path (Split-Path $DescriptorPath -Parent) $d.asset
    if ((Get-ReleaseHash $zip MD5) -cne $d.md5 -or (Get-ReleaseHash $zip SHA256) -cne $d.sha256) { throw 'Package checksum mismatch.' }
    foreach ($algorithm in @('md5', 'sha256')) {
        $line = [IO.File]::ReadAllText("$zip.$algorithm").Trim()
        if ($line -cne "$($d[$algorithm])  $($d.asset)") { throw 'Checksum sidecar mismatch.' }
    }
    $native = if ($d.runtime -eq 'win-x64') { 'e_sqlite3.dll' } else { 'libe_sqlite3.so' }
    $expected = @('Jellyfin.Plugin.DeletionQueue.dll', 'Jellyfin.Plugin.DeletionQueue.deps.json',
        'DeletionQueue.Core.dll', 'DeletionQueue.Infrastructure.dll', 'Microsoft.Data.Sqlite.dll',
        'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'SQLitePCLRaw.batteries_v2.dll', 'meta.json', 'LICENSE', $native)
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ("dq-release-check-" + [guid]::NewGuid().ToString('N'))
    try {
        $actual = @($archive.Entries | ForEach-Object { $_.FullName })
        if ($actual.Count -ne $expected.Count -or @(Compare-Object ($expected | Sort-Object) ($actual | Sort-Object)).Count -ne 0) {
            throw 'Unexpected archive entries (missing dependency, host assembly, nested folder or secret).'
        }
        New-Item -ItemType Directory $temporary | Out-Null
        foreach ($entry in $archive.Entries) { [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $temporary $entry.FullName)) }
        $meta = Read-ReleaseJson (Join-Path $temporary 'meta.json')
        foreach ($key in @('guid', 'version', 'targetAbi', 'name', 'description', 'overview', 'owner', 'category')) {
            if ($meta[$key] -cne $Metadata[$key]) { throw "Installed manifest $key mismatch." }
        }
        foreach ($assembly in @('Jellyfin.Plugin.DeletionQueue', 'DeletionQueue.Core', 'DeletionQueue.Infrastructure')) {
            $assemblyPath = Join-Path $temporary "$assembly.dll"
            if ([Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version.ToString() -cne $Metadata.version) {
                throw "Assembly version mismatch: $assembly"
            }
            if ([Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyPath).FileVersion -cne $Metadata.version) {
                throw "File version mismatch: $assembly"
            }
        }
        Assert-AssemblyIdentity (Join-Path $temporary 'Jellyfin.Plugin.DeletionQueue.dll') $Metadata
        $deps = Read-ReleaseJson (Join-Path $temporary 'Jellyfin.Plugin.DeletionQueue.deps.json')
        if (!$deps.runtimeTarget.name.EndsWith("/$($d.runtime)", [StringComparison]::Ordinal)) { throw 'Wrong dependency runtime target.' }
        Assert-NativeLibrary (Join-Path $temporary $native) $d.runtime
    } finally {
        $archive.Dispose()
        # Only this function's unique, freshly created extraction directory is removed.
        if (Test-Path $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
    }
    return $d
}
