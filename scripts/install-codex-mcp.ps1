param(
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$serverName = 'remotecli'

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $publishRoot = Join-Path $repoRoot 'artifacts\publish'
    if (-not (Test-Path -LiteralPath $publishRoot)) {
        throw "Publish directory not found: $publishRoot"
    }

    $candidateDirectories = Get-ChildItem -LiteralPath $publishRoot -Directory |
        Where-Object { $_.Name -like 'combined-win-x64-connection-service-*' } |
        Sort-Object LastWriteTimeUtc -Descending

    foreach ($candidateDirectory in $candidateDirectories) {
        $candidatePath = Join-Path $candidateDirectory.FullName 'RemoteCli.Desktop.exe'
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            $ExecutablePath = $candidatePath
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
        throw "No combined Windows x64 executable found under $publishRoot"
    }
}

$resolvedAppPath = (Resolve-Path -LiteralPath $ExecutablePath).Path
$codexCommand = Get-Command codex -ErrorAction SilentlyContinue
if ($null -eq $codexCommand -or [string]::IsNullOrWhiteSpace($codexCommand.Source)) {
    throw 'Codex CLI was not found on PATH.'
}

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $addOutput = & $codexCommand.Source mcp add $serverName -- $resolvedAppPath 'mcp' 2>&1
    $addExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($addExitCode -ne 0) {
    $addText = @($addOutput | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    throw "Codex MCP registration failed with exit code $addExitCode. $addText"
}

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $verifyOutput = & $codexCommand.Source mcp get $serverName 2>&1
    $verifyExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

$verifyText = @($verifyOutput | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
if ($verifyExitCode -ne 0 -or
    $verifyText.IndexOf($resolvedAppPath, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
    $verifyText -notmatch 'args:\s*mcp' -or
    $verifyText -notmatch 'enabled:\s*true') {
    throw "Codex did not report the expected enabled MCP command. $verifyText"
}

$hash = (Get-FileHash -LiteralPath $resolvedAppPath -Algorithm SHA256).Hash
$logPath = Join-Path $repoRoot 'artifacts\codex-mcp-install-log.md'
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
if (-not (Test-Path -LiteralPath $logPath)) {
    [System.IO.File]::WriteAllText($logPath, "# Codex MCP installation log`r`n", $utf8WithoutBom)
}

$timestamp = [DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss zzz')
$record = "`r`n- $timestamp | server: $serverName | command: ``$resolvedAppPath mcp`` | SHA-256: ``$hash`` | verified: enabled"
[System.IO.File]::AppendAllText($logPath, $record, $utf8WithoutBom)

Write-Output "Codex MCP server '$serverName' is enabled and points to: $resolvedAppPath"
Write-Output "Installation record: $logPath"
