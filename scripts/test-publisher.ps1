param([Parameter(Mandatory)][string]$PackagesDirectory, [string]$Tag = '')
$ErrorActionPreference = 'Stop'
$previousExitCode = Get-Variable LASTEXITCODE -ValueOnly -ErrorAction SilentlyContinue
. (Join-Path $PSScriptRoot 'release-common.ps1')
& (Join-Path $PSScriptRoot 'test-publisher-http.ps1')
Write-Host 'OFFLINE FIXTURE: git and gh are mocked; no real release or catalog is published.'
# Offline contract test: both external executables are shadowed in this script's scope.
# No GitHub request, git write, tag creation or release publication can occur here.
$script:fixture = Join-Path (Split-Path $PSScriptRoot -Parent) ("artifacts/publisher-tests/" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $script:fixture | Out-Null
$script:mock = @{
    source = ('a' * 40); head = $null; tree = $null; release = $null
    files = @{}; pendingFiles = @{}; assets = @{}; uploads = 0; publishes = 0; refs = 0
    failUpload = $true; failRef = $false; calls = 0; lightweight = $false; remoteCommit = ('a' * 40)
    readFailure = 0
    draftTagReads = 0; draftIdReads = 0; creates = 0
}
$DeletionQueuePublisherMock = $script:mock
$DeletionQueuePublisherFixture = $script:fixture
function git {
    $script:mock = $DeletionQueuePublisherMock
    $a = @($args)
    $global:LASTEXITCODE = 0
    switch ($a[0]) {
        'rev-parse' { return $script:mock.source }
        'ls-remote' { return "$($script:mock.source)`trefs/tags/mock^{}" }
        'show' { return '2026-10-21T00:00:00+00:00' }
        default { throw 'Unexpected git invocation in publisher test.' }
    }
}
function gh {
    $script:mock = $DeletionQueuePublisherMock
    $script:fixture = $DeletionQueuePublisherFixture
    $a = @($args)
    $global:LASTEXITCODE = 0
    $script:mock.calls++
    if ($a[0] -eq 'api' -and $a[1] -eq '--include') {
        $path = $a[2]
        $body = $null
        if ($path -match '/git/ref/tags/') {
            $body = if ($script:mock.lightweight) { @{ object = @{ type = 'commit'; sha = $script:mock.remoteCommit } } }
                else { @{ object = @{ type = 'tag'; sha = ('b' * 40) } } }
        } elseif ($path -match '/git/tags/') {
            $body = @{ object = @{ type = 'commit'; sha = $script:mock.remoteCommit } }
        } elseif ($path -match '/git/ref/heads/manifests$') {
            if ($script:mock.head) { $body = @{ object = @{ sha = $script:mock.head } } }
        } elseif ($path -match '/git/commits/') {
            $body = @{ tree = @{ sha = $script:mock.tree } }
        } elseif ($path -match '/contents/(.+)\?ref=') {
            $file = $Matches[1]
            if ($script:mock.files.ContainsKey($file)) {
                $body = @{ encoding = 'base64'; content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($script:mock.files[$file])) }
            }
        } elseif ($path -match '/releases/tags/') {
            if ($script:mock.readFailure -ne 0) {
                $global:LASTEXITCODE = 1
                return "HTTP/2.0 $($script:mock.readFailure) Error`nContent-Type: application/json`r`n`r`n{`"status`":`"404`",`"message`":`"not a missing release`"}"
            }
            # Match GitHub, not a convenient mock: drafts are NOT returned by this endpoint.
            if ($null -ne $script:mock.release -and $script:mock.release.draft) { $script:mock.draftTagReads++ }
            elseif ($null -ne $script:mock.release) { $body = $script:mock.release }
            if ($null -ne $body) { $body.assets = @($script:mock.assets.Keys | ForEach-Object { @{ name = $_ } }) }
        } elseif ($path -match '/releases\?per_page=100&page=1$') {
            # Put the draft on the second page to exercise complete lookup before creation/retry.
            $body = @(0..99 | ForEach-Object { @{ id = (1000 + $_); tag_name = "unrelated-$_"; draft = $false } })
        } elseif ($path -match '/releases\?per_page=100&page=2$') {
            $body = @()
            if ($null -ne $script:mock.release) { $body = @($script:mock.release) }
        } elseif ($path -match '/releases/42$') {
            $body = $script:mock.release
            if ($null -ne $body) {
                $script:mock.draftIdReads++
                $body.assets = @($script:mock.assets.Keys | ForEach-Object { @{ name = $_ } })
            }
        } else { throw "Unexpected API read in publisher test: $path" }
        if ($null -eq $body) {
            $global:LASTEXITCODE = 1
            if ($path -match '/releases/tags/') { return '{"message":"Not Found","status":"404"}' }
            return "HTTP/2.0 404 Not Found`nContent-Type: application/json`r`n`r`n{`"message`":`"Not Found`",`"status`":`"404`"}"
        }
        return "HTTP/2.0 200 OK`nContent-Type: application/json`r`n`r`n$(ConvertTo-Json -InputObject $body -Depth 20 -Compress)"
    }
    if ($a[0] -eq 'api') {
        if ($a[1] -ne '--method' -or $a[4] -ne '--input') { throw 'Unexpected API write arguments.' }
        $method = $a[2]; $path = $a[3]; $body = Read-ReleaseJson $a[5]
        if ($path -match '/releases$') {
            if ($method -ne 'POST' -or $null -ne $script:mock.release -or !$body.draft -or $body.target_commitish -cne $script:mock.source) { throw 'Unsafe or duplicate draft creation.' }
            $script:mock.creates++
            $script:mock.release = @{ id = 42; tag_name = $body.tag_name; draft = $true; prerelease = $body.prerelease; assets = @() }
            return ($script:mock.release | ConvertTo-Json)
        }
        if ($path -match '/git/trees$') {
            $files = @{} + $script:mock.files
            foreach ($entry in $body.tree) { $files[$entry.path] = $entry.content }
            $script:mock.pendingFiles = $files
            $text = ($files.Keys | Sort-Object | ForEach-Object { "$_`n$($files[$_])" }) -join "`n"
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $tree = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-', '').ToLowerInvariant() }
            finally { $sha.Dispose() }
            $script:mock.pendingTree = $tree
            return (@{ sha = $tree } | ConvertTo-Json)
        }
        if ($path -match '/git/commits$') {
            if ($script:mock.head -and ($body.parents.Count -ne 1 -or $body.parents[0] -ne $script:mock.head)) { throw 'History parent was omitted.' }
            return (@{ sha = 'new-catalog-commit' } | ConvertTo-Json)
        }
        if ($path -match '/git/refs') {
            if ($script:mock.release.draft -or $script:mock.assets.Count -ne 15) { throw 'Catalog advertised before all assets were public.' }
            if ($method -eq 'PATCH' -and $body.force -ne $false) { throw 'Publisher attempted a force update.' }
            if ($script:mock.failRef) { $global:LASTEXITCODE = 1; return '{}' }
            $script:mock.head = $body.sha; $script:mock.tree = $script:mock.pendingTree
            $script:mock.files = $script:mock.pendingFiles; $script:mock.refs++
            return '{}'
        }
        throw 'Unexpected API write in publisher test.'
    }
    if ($a[0] -ne 'release') { throw 'Unexpected gh command in publisher test.' }
    switch ($a[1]) {
        'create' { throw 'Draft creation must use the API response, not a follow-up published-tag lookup.' }
        'upload' {
            $path = $a[3]; $name = Split-Path $path -Leaf
            if ($script:mock.assets.ContainsKey($name) -or '--clobber' -in $a) { throw 'Attempted overwrite of a release asset.' }
            if ($script:mock.failUpload -and $script:mock.uploads -eq 2) { $global:LASTEXITCODE = 1; return 'simulated upload failure' }
            $destination = Join-Path $script:fixture $name
            Copy-Item -LiteralPath $path -Destination $destination
            $script:mock.assets[$name] = $destination; $script:mock.uploads++
        }
        'download' {
            $name = $a[[array]::IndexOf($a, '--pattern') + 1]
            $directory = $a[[array]::IndexOf($a, '--dir') + 1]
            Copy-Item -LiteralPath $script:mock.assets[$name] -Destination $directory
        }
        'edit' {
            if ('--draft=false' -notin $a -or $script:mock.assets.Count -ne 15) { throw 'Premature publication.' }
            $script:mock.release.draft = $false; $script:mock.publishes++
        }
        default { throw 'Unexpected release command in publisher test.' }
    }
    return 'ok'
}
$names = @('GITHUB_ACTIONS', 'GITHUB_REPOSITORY', 'GITHUB_SHA', 'GH_TOKEN')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:GITHUB_ACTIONS = 'true'; $env:GITHUB_REPOSITORY = 'someRandomDude-a/seerr-deletion-requests'
    $env:GITHUB_SHA = $script:mock.source; $env:GH_TOKEN = 'offline-fixture-not-a-real-token'
    $metadata = Get-PluginReleaseMetadata -Tag $Tag
    $scriptPath = Join-Path $PSScriptRoot 'publish-release.ps1'
    foreach ($code in @(401, 403, 429, 500)) {
        $script:mock.readFailure = $code
        $rejected = $false
        try { & $scriptPath -PackagesDirectory $PackagesDirectory -Tag "v$($metadata.version)" }
        catch { $rejected = $_.Exception.Message.Contains("HTTP $code;") }
        if (!$rejected -or $null -ne $script:mock.release -or $script:mock.uploads -ne 0 -or $script:mock.refs -ne 0) {
            throw 'API read error was treated as a missing release or caused a remote write.'
        }
    }
    $script:mock.readFailure = 0
    $failed = $false
    $failure = $null
    try { & $scriptPath -PackagesDirectory $PackagesDirectory -Tag "v$($metadata.version)" } catch { $failed = $true; $failure = $_ }
    if (!$failed -or $script:mock.head -or $null -eq $script:mock.release -or !$script:mock.release.draft) { throw "Failed upload was not safely left unindexed in a draft. Underlying error: $failure $($failure.ScriptStackTrace)" }
    $script:mock.failUpload = $false; $script:mock.failRef = $true
    $failed = $false
    try { & $scriptPath -PackagesDirectory $PackagesDirectory -Tag "v$($metadata.version)" } catch { $failed = $true }
    if (!$failed -or $script:mock.head -or $script:mock.release.draft) { throw 'Failed catalog update was not recoverable after publication.' }
    if ($script:mock.creates -ne 1 -or $script:mock.draftTagReads -lt 1 -or $script:mock.draftIdReads -lt 1) { throw 'Retry did not resume the existing draft through its release ID.' }
    $script:mock.failRef = $false
    & $scriptPath -PackagesDirectory $PackagesDirectory -Tag "v$($metadata.version)"
    $uploads = $script:mock.uploads; $refs = $script:mock.refs
    $script:mock.lightweight = $true
    & $scriptPath -PackagesDirectory $PackagesDirectory -Tag "v$($metadata.version)"
    if ($uploads -ne 15 -or $script:mock.uploads -ne $uploads -or $script:mock.publishes -ne 1 -or $script:mock.refs -ne $refs) {
        throw 'Publisher retry was not idempotent.'
    }
    $remoteZip = @($script:mock.assets.Keys | Where-Object { $_.EndsWith('.zip') })[0]
    [IO.File]::AppendAllText($script:mock.assets[$remoteZip], 'tampered remote asset')
    $failed = $false
    try { & $scriptPath -PackagesDirectory $PackagesDirectory -Tag "v$($metadata.version)" } catch { $failed = $true }
    if (!$failed -or $script:mock.uploads -ne $uploads -or $script:mock.refs -ne $refs) { throw 'Conflicting published asset was overwritten or advertised.' }
    $script:mock.remoteCommit = 'c' * 40
    $failed = $false
    try { & $scriptPath -PackagesDirectory $PackagesDirectory -Tag "v$($metadata.version)" } catch { $failed = $_.Exception.Message -like '*Remote tag moved*' }
    if (!$failed -or $script:mock.uploads -ne $uploads -or $script:mock.refs -ne $refs) { throw 'A moved tag was not rejected before publication.' }
    Write-Host 'PASS: offline publisher contract: optional release 404, non-404 read failures without writes, annotated/lightweight tags, moved-tag rejection, draft/upload/ref failures, retry, immutability and no force-push.'
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    $global:LASTEXITCODE = if ($null -eq $previousExitCode) { 0 } else { $previousExitCode }
}
