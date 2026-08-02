[CmdletBinding()]
param(
    # Keep the historical release URL: GitHub redirects it after a repository
    # rename, while the pinned digest below preserves the binary identity.
    [uri]$LegacyInstallerUri =
        "https://github.com/JunDengH/gpt-account-manager/releases/download/v1.1.5/GptAccountManager-1.1.5-win-x64-setup.exe",
    [ValidatePattern("^[0-9a-fA-F]{64}$")]
    [string]$LegacyInstallerSha256 =
        "76d04e6a0283ad207f98858db9d80d0b0ee806a9a83e4b7003ad100f0022b0d5",
    [string]$CurrentInstallerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

if ($env:GITHUB_ACTIONS -ne "true") {
    throw "This destructive installer smoke test may only run on a GitHub Actions runner."
}
if ($env:RUNNER_OS -ne "Windows" -or
    $env:GPT_CONTROLLER_SMOKE_RUNNER_ENVIRONMENT -ne "github-hosted") {
    throw "This installer smoke test requires a GitHub-hosted Windows runner."
}
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw "RUNNER_TEMP is required."
}
if ($LegacyInstallerUri.Scheme -cne [Uri]::UriSchemeHttps) {
    throw "The legacy installer must be downloaded over HTTPS."
}

function Get-ContainedPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string]$Description
    )

    $trimCharacters = [char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd($trimCharacters)
    $normalizedPath = [IO.Path]::GetFullPath($Path)
    $rootPrefix = $normalizedRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $normalizedPath.StartsWith(
            $rootPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must remain inside '$normalizedRoot'."
    }

    return $normalizedPath
}

function Assert-FileExists {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found at '$Path'."
    }
}

function Assert-FileMissing {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Description
    )

    if (Test-Path -LiteralPath $Path) {
        throw "$Description was not removed from '$Path'."
    }
}

function Assert-SingleUninstaller {
    param(
        [Parameter(Mandatory)]
        [string]$InstallDirectory,
        [Parameter(Mandatory)]
        [string]$Stage
    )

    $uninstallers = @(
        Get-ChildItem -LiteralPath $InstallDirectory -Filter "unins*.exe" `
            -File -ErrorAction SilentlyContinue
    )
    if ($uninstallers.Count -ne 1) {
        throw "$Stage should have exactly one shared Inno uninstaller; found $($uninstallers.Count)."
    }
}

function Invoke-InnoInstaller {
    param(
        [Parameter(Mandatory)]
        [string]$InstallerPath,
        [Parameter(Mandatory)]
        [string]$InstallDirectory,
        [Parameter(Mandatory)]
        [string]$LogPath
    )

    $arguments = @(
        "/VERYSILENT"
        "/SUPPRESSMSGBOXES"
        "/NORESTART"
        "/SP-"
        "/DIR=`"$InstallDirectory`""
        "/LOG=`"$LogPath`""
    )
    $startProcessArguments = @{
        FilePath = $InstallerPath
        ArgumentList = $arguments
        Wait = $true
        PassThru = $true
        WindowStyle = "Hidden"
    }
    $process = Start-Process @startProcessArguments
    if ($process.ExitCode -ne 0) {
        throw "Installer '$InstallerPath' exited with code $($process.ExitCode)."
    }
}

function Invoke-InnoUninstaller {
    param(
        [Parameter(Mandatory)]
        [string]$InstallDirectory
    )

    if (-not (Test-Path -LiteralPath $InstallDirectory -PathType Container)) {
        return
    }

    $uninstallers = @(
        Get-ChildItem -LiteralPath $InstallDirectory -Filter "unins*.exe" `
            -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending
    )
    if ($uninstallers.Count -eq 0) {
        return
    }

    foreach ($uninstaller in $uninstallers) {
        if (-not (Test-Path -LiteralPath $uninstaller.FullName -PathType Leaf)) {
            continue
        }
        $startProcessArguments = @{
            FilePath = $uninstaller.FullName
            ArgumentList = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART")
            Wait = $true
            PassThru = $true
            WindowStyle = "Hidden"
        }
        $process = Start-Process @startProcessArguments
        if ($process.ExitCode -ne 0) {
            throw "Uninstaller exited with code $($process.ExitCode)."
        }
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "version.ps1")
$currentVersion = Get-RepositoryVersion -RepositoryRoot $repoRoot
Assert-SemanticVersion -Version $currentVersion
$currentSemanticVersion =
    [System.Management.Automation.SemanticVersion]::new($currentVersion)
$legacySemanticVersion =
    [System.Management.Automation.SemanticVersion]::new("1.1.5")
if ($currentSemanticVersion -le $legacySemanticVersion) {
    throw "The current package version '$currentVersion' must be newer than 1.1.5."
}

if ([string]::IsNullOrWhiteSpace($CurrentInstallerPath)) {
    $CurrentInstallerPath = Join-Path $repoRoot `
        "artifacts\GptController-$currentVersion-win-x64-setup.exe"
}
elseif (-not [IO.Path]::IsPathRooted($CurrentInstallerPath)) {
    $CurrentInstallerPath = Join-Path $repoRoot $CurrentInstallerPath
}
$CurrentInstallerPath = [IO.Path]::GetFullPath($CurrentInstallerPath)
Assert-FileExists `
    -Path $CurrentInstallerPath `
    -Description "Current GPT Controller installer"

$expectedInstallerName =
    "GptController-$currentVersion-win-x64-setup.exe"
if ((Split-Path $CurrentInstallerPath -Leaf) -cne $expectedInstallerName) {
    throw "Current installer must be named '$expectedInstallerName'."
}

$runnerTemp = [IO.Path]::GetFullPath($env:RUNNER_TEMP)
if (-not (Test-Path -LiteralPath $runnerTemp -PathType Container)) {
    throw "RUNNER_TEMP does not exist at '$runnerTemp'."
}
$workRootCandidate = Join-Path $runnerTemp (
    "gpt-controller-upgrade-smoke-" + [guid]::NewGuid().ToString("N"))
$workRoot = Get-ContainedPath `
    -Path $workRootCandidate `
    -Root $runnerTemp `
    -Description "Smoke-test work directory"
$installRoot = Get-ContainedPath `
    -Path (Join-Path $workRoot "install") `
    -Root $workRoot `
    -Description "Smoke-test install directory"
$legacyInstallerPath = Join-Path $workRoot "legacy-v1.1.5-setup.exe"
$currentInstallerCopy = Join-Path $workRoot $expectedInstallerName
$legacyInstallLog = Join-Path $workRoot "legacy-install.log"
$upgradeInstallLog = Join-Path $workRoot "upgrade-install.log"

$userProfile =
    [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
$localAppData =
    [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$programsDirectory =
    [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
if ([string]::IsNullOrWhiteSpace($userProfile) -or
    [string]::IsNullOrWhiteSpace($localAppData) -or
    [string]::IsNullOrWhiteSpace($programsDirectory)) {
    throw "The runner user-profile paths could not be resolved."
}

$legacyDataRoot = Get-ContainedPath `
    -Path (Join-Path $localAppData "GptAccountManager") `
    -Root $userProfile `
    -Description "Legacy test data directory"
$legacyShortcut = Get-ContainedPath `
    -Path (Join-Path $programsDirectory "GPT Account Manager.lnk") `
    -Root $userProfile `
    -Description "Legacy shortcut"
$currentShortcut = Get-ContainedPath `
    -Path (Join-Path $programsDirectory "GPT Controller.lnk") `
    -Root $userProfile `
    -Description "Current shortcut"

$legacyExe = Join-Path $installRoot "GptAccountManager.exe"
$legacyHelper = Join-Path $installRoot "GptAccountManager.CredentialHelper.exe"
$legacyBareHelper = Join-Path $installRoot "CredentialHelper.exe"
$currentExe = Join-Path $installRoot "GptController.exe"
$currentHelper = Join-Path $installRoot "GptController.CredentialHelper.exe"
$sentinelPath = Join-Path $legacyDataRoot "upgrade-smoke-sentinel.txt"

$workRootCreated = $false
$legacyDataRootCreated = $false
$shortcutsOwnedByTest = $false
try {
    if (Test-Path -LiteralPath $legacyDataRoot) {
        throw "Refusing to reuse an existing legacy data directory '$legacyDataRoot'."
    }
    if (Test-Path -LiteralPath $legacyShortcut) {
        throw "Refusing to overwrite an existing legacy shortcut '$legacyShortcut'."
    }
    if (Test-Path -LiteralPath $currentShortcut) {
        throw "Refusing to overwrite an existing current shortcut '$currentShortcut'."
    }
    $shortcutsOwnedByTest = $true

    New-Item -ItemType Directory -Path $workRoot | Out-Null
    $workRootCreated = $true
    Copy-Item -LiteralPath $CurrentInstallerPath -Destination $currentInstallerCopy

    $downloadArguments = @{
        UseBasicParsing = $true
        Uri = $LegacyInstallerUri
        OutFile = $legacyInstallerPath
        MaximumRedirection = 10
        TimeoutSec = 120
    }
    Invoke-WebRequest @downloadArguments
    Assert-FileExists `
        -Path $legacyInstallerPath `
        -Description "Downloaded v1.1.5 installer"
    $actualLegacyHash =
        (Get-FileHash -LiteralPath $legacyInstallerPath -Algorithm SHA256).
            Hash.ToLowerInvariant()
    if ($actualLegacyHash -cne $LegacyInstallerSha256.ToLowerInvariant()) {
        throw "The downloaded v1.1.5 installer SHA-256 did not match the pinned value."
    }
    Unblock-File -LiteralPath $legacyInstallerPath

    New-Item -ItemType Directory -Path $legacyDataRoot | Out-Null
    $legacyDataRootCreated = $true
    $sentinelValue =
        "gpt-controller-upgrade-smoke/" + [guid]::NewGuid().ToString("N")
    [IO.File]::WriteAllText(
        $sentinelPath,
        $sentinelValue,
        [Text.UTF8Encoding]::new($false))
    $sentinelTimestamp =
        [DateTime]::new(2024, 1, 2, 3, 4, 5, [DateTimeKind]::Utc)
    [IO.File]::SetLastWriteTimeUtc($sentinelPath, $sentinelTimestamp)
    $sentinelHash =
        (Get-FileHash -LiteralPath $sentinelPath -Algorithm SHA256).Hash

    Invoke-InnoInstaller `
        -InstallerPath $legacyInstallerPath `
        -InstallDirectory $installRoot `
        -LogPath $legacyInstallLog
    Assert-FileExists -Path $legacyExe -Description "Legacy executable"
    Assert-FileExists -Path $legacyShortcut -Description "Legacy shortcut"
    Assert-SingleUninstaller `
        -InstallDirectory $installRoot `
        -Stage "The v1.1.5 install"

    # v1.1.5 predates the helper. Seed both known historical names so the
    # upgrade validates their exact cleanup without introducing real binaries.
    [IO.File]::WriteAllText($legacyHelper, "legacy helper sentinel")
    [IO.File]::WriteAllText($legacyBareHelper, "legacy helper sentinel")

    Invoke-InnoInstaller `
        -InstallerPath $currentInstallerCopy `
        -InstallDirectory $installRoot `
        -LogPath $upgradeInstallLog
    Assert-SingleUninstaller `
        -InstallDirectory $installRoot `
        -Stage "The in-place upgrade"

    Assert-FileMissing -Path $legacyExe -Description "Legacy executable"
    Assert-FileMissing -Path $legacyHelper -Description "Legacy credential helper"
    Assert-FileMissing `
        -Path $legacyBareHelper `
        -Description "Legacy bare credential helper"
    Assert-FileMissing -Path $legacyShortcut -Description "Legacy shortcut"
    Assert-FileExists -Path $currentExe -Description "GPT Controller executable"
    Assert-FileExists `
        -Path $currentHelper `
        -Description "GPT Controller credential helper"
    Assert-FileExists -Path $currentShortcut -Description "GPT Controller shortcut"

    Assert-FileExists -Path $sentinelPath -Description "Legacy data sentinel"
    $sentinelHashAfterUpgrade =
        (Get-FileHash -LiteralPath $sentinelPath -Algorithm SHA256).Hash
    if ($sentinelHashAfterUpgrade -cne $sentinelHash) {
        throw "The legacy data sentinel content changed during upgrade."
    }
    if ([IO.File]::GetLastWriteTimeUtc($sentinelPath) -ne $sentinelTimestamp) {
        throw "The legacy data sentinel timestamp changed during upgrade."
    }

    Write-Host "Installer upgrade smoke test passed: v1.1.5 -> $currentVersion."
}
catch {
    foreach ($logPath in @($legacyInstallLog, $upgradeInstallLog)) {
        if (Test-Path -LiteralPath $logPath -PathType Leaf) {
            Write-Warning "Installer log '$logPath':"
            Get-Content -LiteralPath $logPath -Tail 200 |
                ForEach-Object { Write-Warning $_ }
        }
    }
    throw
}
finally {
    $cleanupFailures = [Collections.Generic.List[string]]::new()
    try {
        Invoke-InnoUninstaller -InstallDirectory $installRoot
    }
    catch {
        $cleanupFailures.Add("Uninstall failed: $($_.Exception.Message)")
    }

    if ($shortcutsOwnedByTest) {
        foreach ($shortcut in @($legacyShortcut, $currentShortcut)) {
            if (Test-Path -LiteralPath $shortcut) {
                try {
                    Remove-Item -LiteralPath $shortcut -Force -ErrorAction Stop
                }
                catch {
                    $cleanupFailures.Add(
                        "Could not remove shortcut '$shortcut': $($_.Exception.Message)")
                }
            }
        }
    }
    if ($legacyDataRootCreated -and (Test-Path -LiteralPath $legacyDataRoot)) {
        try {
            Remove-Item -LiteralPath $legacyDataRoot -Recurse -Force `
                -ErrorAction Stop
        }
        catch {
            $cleanupFailures.Add(
                "Could not remove test data '$legacyDataRoot': $($_.Exception.Message)")
        }
    }
    if ($workRootCreated -and (Test-Path -LiteralPath $workRoot)) {
        try {
            Remove-Item -LiteralPath $workRoot -Recurse -Force `
                -ErrorAction Stop
        }
        catch {
            $cleanupFailures.Add(
                "Could not remove work directory '$workRoot': $($_.Exception.Message)")
        }
    }
    if ($cleanupFailures.Count -gt 0) {
        throw "Smoke-test cleanup failed: $($cleanupFailures -join '; ')"
    }
}
