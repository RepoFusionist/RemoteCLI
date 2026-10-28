param(
    [string]$ExecutablePath,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$serverName = 'remotecli'
$codexCommand = Get-Command codex -ErrorAction SilentlyContinue
if ($null -eq $codexCommand -or [string]::IsNullOrWhiteSpace($codexCommand.Source)) {
    throw 'Codex CLI was not found on PATH.'
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
if ($verifyExitCode -ne 0 -or $verifyText -notmatch 'enabled:\s*true' -or $verifyText -notmatch 'args:\s*mcp') {
    throw "Codex MCP server '$serverName' is not enabled with the expected mcp argument. $verifyText"
}
if ($verifyText -notmatch '(?m)^\s*command:\s*(.+)\s*$') {
    throw "Codex did not report a command for MCP server '$serverName'. $verifyText"
}

$registeredPath = (Resolve-Path -LiteralPath $Matches[1].Trim()).Path
if (-not [string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $requestedPath = (Resolve-Path -LiteralPath $ExecutablePath).Path
    if (-not [string]::Equals($registeredPath, $requestedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Codex is configured for '$registeredPath', not requested executable '$requestedPath'."
    }
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand -or [string]::IsNullOrWhiteSpace($dotnetCommand.Source)) {
    throw 'dotnet SDK was not found on PATH.'
}

$testProject = Join-Path $repoRoot 'tests\RemoteCli.UnitTests\RemoteCli.UnitTests.csproj'
$dotnetArguments = @(
    'test',
    $testProject,
    '--configuration',
    $Configuration,
    '--no-restore',
    '--filter',
    'FullyQualifiedName~McpSurfaceTests',
    '--logger',
    'console;verbosity=detailed',
    '-m:1'
)

$hadOriginalTestExecutable = Test-Path Env:REMOTECLI_TEST_MCP_EXE
$originalTestExecutable = $env:REMOTECLI_TEST_MCP_EXE
$env:REMOTECLI_TEST_MCP_EXE = $registeredPath
$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $testOutput = & $dotnetCommand.Source @dotnetArguments 2>&1
    $testExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
    if ($hadOriginalTestExecutable) {
        $env:REMOTECLI_TEST_MCP_EXE = $originalTestExecutable
    }
    else {
        Remove-Item Env:REMOTECLI_TEST_MCP_EXE -ErrorAction SilentlyContinue
    }
}

$testText = @($testOutput | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
foreach ($line in $testOutput) {
    Write-Output $line
}

$connectionState = 'not reported'
$stateMatch = [regex]::Match($testText, 'Remote CLI MCP local connection state:\s*([A-Za-z]+)')
if ($stateMatch.Success) {
    $connectionState = $stateMatch.Groups[1].Value
}

$hash = (Get-FileHash -LiteralPath $registeredPath -Algorithm SHA256).Hash
$logPath = Join-Path $repoRoot 'artifacts\codex-mcp-install-log.md'
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
if (-not (Test-Path -LiteralPath $logPath)) {
    [System.IO.File]::WriteAllText($logPath, "# Codex MCP installation log`r`n", $utf8WithoutBom)
}

$timestamp = [DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss zzz')
$result = 'PASS'
if ($testExitCode -ne 0) {
    $result = "FAIL (exit code $testExitCode)"
}
$record = "`r`n- $timestamp | test: McpSurfaceTests via $serverName | executable: ``$registeredPath`` | SHA-256: ``$hash`` | result: $result | local connection state: $connectionState"
[System.IO.File]::AppendAllText($logPath, $record, $utf8WithoutBom)

if ($testExitCode -ne 0) {
    throw "MCP Surface test failed with exit code $testExitCode. $testText"
}

Write-Output "Codex MCP '$serverName' package integration test passed; local connection state: $connectionState. Record: $logPath"
