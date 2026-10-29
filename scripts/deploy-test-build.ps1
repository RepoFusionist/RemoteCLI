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
$buildIdProperty = '-p:RemoteCliBuildId=' + $VersionSuffix
$fileVersionProperty = '-p:RemoteCliFileVersion=' + $buildTimestamp.ToString('yyyy.MMdd.HHmm.ss')

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

function Test-CodexExecutablePath {
    param([string]$CandidatePath)

    if ([string]::IsNullOrWhiteSpace($CandidatePath)) {
        return $false
    }

    try {
        if (-not (Test-Path -LiteralPath $CandidatePath -PathType Leaf)) {
            return $false
        }
    }
    catch {
        return $false
    }

    $fileName = Split-Path -Leaf $CandidatePath
    return [string]::Equals($fileName, 'codex.exe', [StringComparison]::OrdinalIgnoreCase) -or
           [string]::Equals($fileName, 'codex.cmd', [StringComparison]::OrdinalIgnoreCase)
}

function Get-CodexCliPathFromConfig {
    param([string]$ConfigPath)

    if ([string]::IsNullOrWhiteSpace($ConfigPath) -or -not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        return $null
    }

    $section = ''
    foreach ($rawLine in [System.IO.File]::ReadAllLines($ConfigPath)) {
        $line = $rawLine.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith('#')) {
            continue
        }
        if ($line.StartsWith('[')) {
            $sectionEnd = $line.IndexOf(']')
            $section = if ($sectionEnd -gt 0) { $line.Substring(1, $sectionEnd - 1).Trim() } else { '' }
            continue
        }
        # Codex desktop writes the CLI path into this MCP env section.
        if ($section.Length -ne 0 -and -not [string]::Equals($section, 'mcp_servers.node_repl.env', [StringComparison]::Ordinal)) {
            continue
        }
        $separator = $line.IndexOf('=')
        if ($separator -lt 0 -or -not [string]::Equals($line.Substring(0, $separator).Trim(), 'CODEX_CLI_PATH', [StringComparison]::Ordinal)) {
            continue
        }
        $value = $line.Substring($separator + 1).Trim().Trim([char]34).Trim([char]39)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value
        }
    }

    return $null
}

function Resolve-RemoteCliCodexCliExecutable {
    $command = Get-Command codex -ErrorAction SilentlyContinue
    if ($null -ne $command -and -not [string]::IsNullOrWhiteSpace($command.Source)) {
        return $command.Source
    }

    $remoteCliSettings = Join-Path $env:LOCALAPPDATA 'RemoteCLI\codex-cli.json'
    $configuredPath = $null
    if (Test-Path -LiteralPath $remoteCliSettings -PathType Leaf) {
        try {
            $settings = (Get-Content -LiteralPath $remoteCliSettings -Raw | ConvertFrom-Json)
            $configuredPath = $settings.executablePath
        }
        catch {
            $configuredPath = $null
        }
    }

    $codexHomeConfig = if ([string]::IsNullOrWhiteSpace($env:CODEX_HOME)) { $null } else { Join-Path $env:CODEX_HOME 'config.toml' }
    $userProfileConfig = if ([string]::IsNullOrWhiteSpace($env:USERPROFILE)) { $null } else { Join-Path $env:USERPROFILE '.codex\config.toml' }
    $defaultInstallPath = if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { $null } else { Join-Path $env:LOCALAPPDATA 'Programs\OpenAI\Codex\bin\codex.exe' }

    $candidates = @(
        $configuredPath,
        (Get-CodexCliPathFromConfig $codexHomeConfig),
        (Get-CodexCliPathFromConfig $userProfileConfig),
        $defaultInstallPath
    )

    foreach ($candidate in $candidates) {
        if (Test-CodexExecutablePath $candidate) {
            return $candidate
        }
    }

    return $null
}

$previousLocation = Get-Location
try {
    Set-Location -LiteralPath $repoRoot
    Write-Output "PowerShell: $($PSVersionTable.PSVersion) ($((Get-Command powershell.exe).Source))"
    Write-Output "dotnet: $dotnetPath"
    Write-Output "Node.js: $($nodeCommand.Source)"

    # A full `npm ci` removes the whole node_modules tree, and that bulk delete is blocked in
    # some managed environments. `npm install --prefix` does not read the package.json under
    # the prefix, so verify an already installed tree and only install from scratch when the
    # UI test dependencies are missing.
    $uiTestNodeModules = Join-Path $repoRoot 'tools\UiBridgeTests\node_modules'
    $requiredUiTestPackages = @('@xterm\xterm', '@xterm\addon-serialize', '@xterm\headless', 'jsdom', 'playwright-core')
    $missingUiTestPackages = @($requiredUiTestPackages | Where-Object { -not (Test-Path -LiteralPath (Join-Path $uiTestNodeModules $_)) })
    if ($missingUiTestPackages.Count -eq 0) {
        Invoke-NativeStep 'Verify UI test dependencies' @(
            'ls',
            '--prefix',
            'tools\UiBridgeTests',
            '--depth=0'
        ) -ExecutablePath $npmCommand.Source
    }
    else {
        Invoke-NativeStep 'Restore UI test dependencies' @(
            'ci',
            '--prefix',
            'tools\UiBridgeTests',
            '--ignore-scripts',
            '--no-audit',
            '--no-fund',
            '--engine-strict'
        ) -ExecutablePath $npmCommand.Source
    }

    Invoke-NativeStep 'Embedded UI bridge tests' @(
        '--test',
        'tools\UiBridgeTests\ui-bridge.test.cjs'
    ) -ExecutablePath $nodeCommand.Source

    Invoke-NativeStep 'Restore desktop project' @(
        'restore',
        'src\RemoteCli.Desktop\RemoteCli.Desktop.csproj',
        '--ignore-failed-sources',
        '-m:1',
        '-p:NuGetAudit=false'
    )

    Invoke-NativeStep 'Restore automated test project' @(
        'restore',
        'tests\RemoteCli.UnitTests\RemoteCli.UnitTests.csproj',
        '--ignore-failed-sources',
        '-m:1',
        '-p:NuGetAudit=false'
    )

    Invoke-NativeStep 'Release desktop build' @(
        'build',
        'src\RemoteCli.Desktop\RemoteCli.Desktop.csproj',
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

    $publishedExecutable = Join-Path $publishDirectory 'RemoteCli.Desktop.exe'
    if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
        throw "Publish completed without the expected executable: $publishedExecutable"
    }

    # Keep the registered EXE intact while offering a smaller download. Store the ZIP outside
    # the source directory so the archive cannot accidentally include itself.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packageDirectory = Join-Path $repoRoot 'artifacts\packages'
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    $packagePath = Join-Path $packageDirectory ("RemoteCLI-win-x64-$VersionSuffix.zip")
    if (Test-Path -LiteralPath $packagePath) {
        throw "Refusing to overwrite an existing package: $packagePath"
    }
    if (-not [Enum]::IsDefined([System.IO.Compression.CompressionLevel], 'Optimal')) {
        throw 'The Optimal ZIP compression level is not available.'
    }
    $zipLevel = [System.IO.Compression.CompressionLevel]::Optimal
    [System.IO.Compression.ZipFile]::CreateFromDirectory($publishDirectory, $packagePath, $zipLevel, $false)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $exeEntry = $archive.GetEntry('RemoteCli.Desktop.exe')
        if ($null -eq $exeEntry) { throw 'The ZIP package does not contain the published executable.' }
        $hashAlgorithm = [System.Security.Cryptography.SHA256]::Create()
        $entryStream = $exeEntry.Open()
        try {
            $entryHash = ([BitConverter]::ToString($hashAlgorithm.ComputeHash($entryStream))).Replace('-', '')
        }
        finally { $entryStream.Dispose(); $hashAlgorithm.Dispose() }
        if ($entryHash -ne (Get-FileHash -LiteralPath $publishedExecutable -Algorithm SHA256).Hash) {
            throw 'The ZIP executable does not match the published executable.'
        }
    }
    finally { $archive.Dispose() }
    Write-Output "Compressed package verified: $packagePath"

    # The Office preview kit (@deepseek-ai/libreoffice-kit-win32-x64 0.1.2) is an optional
    # third-party runtime download owned by its upstream npm package (MPL-2.0 plus LGPL/GPL
    # LibreOffice components). It is intentionally NOT packaged or redistributed by this
    # deployment; the desktop app downloads and verifies it on first Office preview.
    # The archive must never be committed to git or pushed to a remote repository: doing so
    # would make this project the redistributor. A local copy is for tests only and is
    # ignored by .gitignore; point REMOTECLI_OFFICEKIT_PACKAGE at it if needed.

    # Codex CLI is not always on the PATH of the deployment session. Prefer PATH, then fall back
    # to the path Remote CLI already validated, the Codex desktop config, and the default install
    # directory, so registration still runs through the real `codex mcp` commands.
    $codexCliExecutable = Resolve-RemoteCliCodexCliExecutable
    if ([string]::IsNullOrWhiteSpace($codexCliExecutable)) {
        throw 'Codex CLI was not found on PATH, in Remote CLI settings, in the Codex config, or in the default install directory.'
    }
    $codexCliDirectory = Split-Path -Parent $codexCliExecutable
    if ($env:Path.Split(';') -notcontains $codexCliDirectory) {
        $env:Path = $codexCliDirectory + ';' + $env:Path
    }
    Write-Output "Codex CLI: $codexCliExecutable"

    & (Join-Path $PSScriptRoot 'install-codex-mcp.ps1') -ExecutablePath $publishedExecutable
    & (Join-Path $PSScriptRoot 'test-codex-mcp.ps1') -ExecutablePath $publishedExecutable

    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    $releaseManifest = [ordered]@{
        buildId = $VersionSuffix
        executable = $publishedExecutable
        sha256 = (Get-FileHash -LiteralPath $publishedExecutable -Algorithm SHA256).Hash
        coreSizeBytes = (Get-Item -LiteralPath $publishedExecutable).Length
        zipPackage = $packagePath
        zipPackageSha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
        zipSizeBytes = (Get-Item -LiteralPath $packagePath).Length
        productVersion = (Get-Item -LiteralPath $publishedExecutable).VersionInfo.ProductVersion
        officeKitPackage = $null
        officeKitPackageSha256 = $null
        officeKitSourceSha256 = $null
        mcpRegistration = 'remotecli'
        packagedMcpTest = 'PASS'
    }
    [System.IO.File]::WriteAllText((Join-Path $repoRoot 'artifacts\latest-release.json'),
        ($releaseManifest | ConvertTo-Json), $utf8WithoutBom)

    Write-Output "Test deployment is ready: $publishedExecutable"
}
finally {
    Set-Location -LiteralPath $previousLocation
}
