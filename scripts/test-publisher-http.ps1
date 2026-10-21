$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$path = 'repos/owner/repository/releases/tags/v0.1.0.3'
foreach ($text in @(
    "HTTP/2.0 404 Not Found`n`n{`"message`":`"Not Found`",`"status`":`"404`"}",
    "HTTP/2.0 404 Not Found`nContent-Type: application/json`r`n`r`n{`"message`":`"Not Found`",`"status`":`"404`"}",
    "$([char]0xfeff)HTTP/1.1 404 Not Found`r`n`r`n{`"status`":404}",
    '{"message":"Not Found","status":"404"}'
)) {
    if ($null -ne (ConvertFrom-GitHubApiResponse -Text $text -ExitCode 1 -Path $path -AllowMissing)) { throw 'Optional 404 was not treated as missing.' }
    $rejected = $false
    try { ConvertFrom-GitHubApiResponse -Text $text -ExitCode 1 -Path $path | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw 'Required API read swallowed a 404.' }
}
foreach ($code in @(401, 403, 429, 500)) {
    $text = "HTTP/2.0 $code Error`nContent-Type: application/json`r`n`r`n{`"message`":`"misleading 404 text`",`"status`":`"404`"}"
    $rejected = $false
    try { ConvertFrom-GitHubApiResponse -Text $text -ExitCode 1 -Path $path -AllowMissing | Out-Null }
    catch { $rejected = $_.Exception.Message.Contains("HTTP $code;") }
    if (!$rejected) { throw 'Non-404 failure was swallowed or lost its HTTP status.' }
}
foreach ($text in @('', 'network failure', '{"message":"Not Found"}', '{"status":"403"}', '{"status":"500"}')) {
    $rejected = $false
    try { ConvertFrom-GitHubApiResponse -Text $text -ExitCode 1 -Path $path -AllowMissing | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw 'An unverified failure was treated as missing.' }
}
foreach ($text in @(
    "HTTP/2.0 200 OK`nContent-Type: application/json`r`n`r`n{`"id`":42,`"draft`":true}",
    "HTTP/1.1 200 OK`r`nContent-Type: application/json`r`n`r`n{`"id`":42,`"draft`":true}"
)) {
    $result = ConvertFrom-GitHubApiResponse -Text $text -ExitCode 0 -Path $path
    if ($result.id -ne 42 -or !$result.draft) { throw 'Successful GitHub response was not parsed.' }
}
foreach ($count in @(0, 1, 100)) {
    $items = @(for ($i = 0; $i -lt $count; $i++) { @{ id = ($i + 1); tag_name = "v0.1.0.$i" } })
    $text = "HTTP/2.0 200 OK`nContent-Type: application/json`r`n`r`n$(ConvertTo-Json -InputObject $items -Compress)"
    $result = @(ConvertFrom-GitHubApiResponse -Text $text -ExitCode 0 -Path $path)
    if ($result.Count -ne $count) { throw 'Release-list response array was lost or unwrapped incorrectly.' }
}
if (Test-Path /bin/sh) {
    $fixture = Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/http-tests/' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $fixture | Out-Null
    $file = Join-Path $fixture 'native.response'
    $previousNativeExitCode = Get-Variable LASTEXITCODE -ValueOnly -ErrorAction SilentlyContinue
    try {
        $PSNativeCommandUseErrorActionPreference = $false
        $payload = "HTTP/2.0 404 Not Found`nContent-Type: application/json`r`n`r`n{`"message`":`"Not Found`",`"status`":`"404`"}"
        & /bin/sh -c 'printf "%s" "$1"; exit 1' 'http-fixture' $payload 1> $file 2>$null
        $code = $LASTEXITCODE
        $result = ConvertFrom-GitHubApiResponse -Text ([IO.File]::ReadAllText($file)) -ExitCode $code -Path $path -AllowMissing
        if ($code -ne 1 -or $null -ne $result) { throw 'Native-process stdout/exit handling did not recognize an optional 404.' }
    } finally {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
        $global:LASTEXITCODE = if ($null -eq $previousNativeExitCode) { 0 } else { $previousNativeExitCode }
    }
}
Write-Host 'PASS: GitHub HTTP/JSON-only 404s, mixed LF/CRLF framing, required reads, permission/rate/server/transport failures and safe status diagnostics.'
