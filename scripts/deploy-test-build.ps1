param(
    [string]$VersionSuffix
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Run this deployment script with Windows PowerShell 5.1 (powershell.exe).'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($VersionSuffix)) {
    $VersionSuffix = Get-Date -Format 'yyyyMMdd-HHmmss'
}
$buildTimestamp = [DateTime]::ParseExact($VersionSuffix, 'yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture)
$buildIdProperty = '-p:LongShellBuildId=' + $VersionSuffix
$fileVersionProperty = '-p:LongShellFileVersion=' + $buildTimestamp.ToString('yyyy.MMdd.HHmm.ss')

$publishDirectory = Join-Path $repoRoot ("artifacts\publish\combined-win-x64-connection-service-$VersionSuffix")
if (Test-Path -LiteralPath $publishDirectory) {
    throw "Refusing to overwrite an existing test deployment: $publishDirectory"
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand -or [string]::IsNullOrWhiteSpace($dotnetCommand.Source)) {
    throw 'dotnet SDK was not found on PATH.'
}
$dotnetPath = $dotnetCommand.Source

$nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
$npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
if ($null -eq $nodeCommand -or $null -eq $npmCommand) {
    throw 'Node.js and npm are required for the embedded UI bridge tests. See tools/UiBridgeTests/package.json for supported Node.js versions.'
}

function Invoke-NativeStep {
    param(
        [string]$StepName,
        [string[]]$Arguments,
        [string]$ExecutablePath = $script:dotnetPath
    )

    Write-Output "=== $StepName ==="
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $commandOutput = & $ExecutablePath @Arguments 2>&1
        $commandExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    foreach ($line in $commandOutput) {
        Write-Output $line
    }

    if ($commandExitCode -ne 0) {
        throw "$StepName failed with exit code $commandExitCode."
    }
}

$previousLocation = Get-Location
try {
    Set-Location -LiteralPath $repoRoot
    Write-Output "PowerShell: $($PSVersionTable.PSVersion) ($((Get-Command powershell.exe).Source))"
    Write-Output "dotnet: $dotnetPath"
    Write-Output "Node.js: $($nodeCommand.Source)"

    Invoke-NativeStep 'Restore UI test dependencies' @(
        'ci',
        '--prefix',
        'tools\UiBridgeTests',
        '--ignore-scripts',
        '--no-audit',
        '--no-fund',
        '--engine-strict'
    ) -ExecutablePath $npmCommand.Source

    Invoke-NativeStep 'Embedded UI bridge tests' @(
        '--test',
        'tools\UiBridgeTests\ui-bridge.test.cjs'
    ) -ExecutablePath $nodeCommand.Source

    Invoke-NativeStep 'Restore' @(
        'restore',
        'RemoteCli.slnx',
        '--ignore-failed-sources',
        '-m:1',
        '-p:NuGetAudit=false'
    )

    Invoke-NativeStep 'Release build' @(
        'build',
        'RemoteCli.slnx',
        '--configuration',
        'Release',
        '--no-restore',
        '-m:1',
        $buildIdProperty,
        $fileVersionProperty
    )

    Invoke-NativeStep 'Full automated test suite' @(
        'test',
        'tests\RemoteCli.UnitTests\RemoteCli.UnitTests.csproj',
        '--configuration',
        'Release',
        '--no-restore',
        '--no-build',
        '-m:1'
    )

    Invoke-NativeStep 'Restore Windows x64 publish assets' @(
        'restore',
        'src\RemoteCli.Desktop\RemoteCli.Desktop.csproj',
        '--runtime',
        'win-x64',
        '--ignore-failed-sources',
        '-m:1',
        '-p:NuGetAudit=false'
    )

    Invoke-NativeStep 'Restore Windows x64 Clash SSH helper assets' @(
        'restore',
        'tools\ClashSshProxy\ClashSshProxy.csproj',
        '--runtime',
        'win-x64',
        '--ignore-failed-sources',
        '-m:1',
        '-p:NuGetAudit=false'
    )

    $publishProperty = '-p:PublishDir=' + $publishDirectory.TrimEnd('\') + '\'
    Invoke-NativeStep 'Publish combined Windows x64 app' @(
        'publish',
        'src\RemoteCli.Desktop\RemoteCli.Desktop.csproj',
        '--configuration',
        'Release',
        '--runtime',
        'win-x64',
        '--self-contained',
        'true',
        '--no-restore',
        '-m:1',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:NuGetAudit=false',
        $buildIdProperty,
        $fileVersionProperty,
        $publishProperty
    )

    Invoke-NativeStep 'Publish Windows x64 Clash SSH helper' @(
        'publish',
        'tools\ClashSshProxy\ClashSshProxy.csproj',
        '--configuration',
        'Release',
        '--runtime',
        'win-x64',
        '--self-contained',
        'true',
        '--no-restore',
        '-m:1',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:NuGetAudit=false',
        $publishProperty
    )

    $publishedExecutable = Join-Path $publishDirectory 'RemoteCli.Desktop.exe'
    if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
        throw "Publish completed without the expected executable: $publishedExecutable"
    }

    $publishedClashSshProxy = Join-Path $publishDirectory 'ClashSshProxy.exe'
    if (-not (Test-Path -LiteralPath $publishedClashSshProxy -PathType Leaf)) {
        throw "Publish completed without the expected Clash SSH helper: $publishedClashSshProxy"
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $proxyHelpOutput = & $publishedClashSshProxy '--help' 2>&1
        $proxyHelpExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $proxyHelpText = @($proxyHelpOutput | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    if ($proxyHelpExitCode -ne 0 -or $proxyHelpText -notmatch 'OpenSSH ProxyCommand') {
        throw "The published Clash SSH helper smoke check failed. $proxyHelpText"
    }

    $proxyHash = (Get-FileHash -LiteralPath $publishedClashSshProxy -Algorithm SHA256).Hash
    $installLogPath = Join-Path $repoRoot 'artifacts\codex-mcp-install-log.md'
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    if (-not (Test-Path -LiteralPath $installLogPath)) {
        [System.IO.File]::WriteAllText($installLogPath, "# Codex MCP installation log`r`n", $utf8WithoutBom)
    }

    $helperTimestamp = [DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss zzz')
    $helperRecord = "`r`n- $helperTimestamp | utility: Clash SSH ProxyCommand relay | executable: ``$publishedClashSshProxy`` | SHA-256: ``$proxyHash`` | smoke test: --help PASS | SSH connection: not attempted"
    [System.IO.File]::AppendAllText($installLogPath, $helperRecord, $utf8WithoutBom)

    & (Join-Path $PSScriptRoot 'install-codex-mcp.ps1') -ExecutablePath $publishedExecutable
    & (Join-Path $PSScriptRoot 'test-codex-mcp.ps1') -ExecutablePath $publishedExecutable

    $releaseManifest = [ordered]@{
        buildId = $VersionSuffix
        executable = $publishedExecutable
        sha256 = (Get-FileHash -LiteralPath $publishedExecutable -Algorithm SHA256).Hash
        productVersion = (Get-Item -LiteralPath $publishedExecutable).VersionInfo.ProductVersion
        mcpRegistration = 'remotecli'
        packagedMcpTest = 'PASS'
    }
    [System.IO.File]::WriteAllText((Join-Path $repoRoot 'artifacts\latest-release.json'),
        ($releaseManifest | ConvertTo-Json), $utf8WithoutBom)

    Write-Output "Test deployment is ready: $publishedExecutable"
    Write-Output "Clash-only SSH helper is ready: $publishedClashSshProxy"
}
finally {
    Set-Location -LiteralPath $previousLocation
}
