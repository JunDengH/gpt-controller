param(
    [string]$InstallerPath
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $installer = Get-ChildItem `
        -Path (Join-Path $repoRoot "installer\Output\*.exe") `
        -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if (-not $installer) {
        throw "No installer was found."
    }

    $InstallerPath = $installer.FullName
}

$resolvedInstaller = [System.IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $resolvedInstaller -PathType Leaf)) {
    throw "Installer does not exist: $resolvedInstaller"
}

$smokeRoot = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    "GptAccountManagerInstallerSmoke-$([Guid]::NewGuid().ToString("N"))"
$resolvedSmokeRoot = [System.IO.Path]::GetFullPath($smokeRoot)
$installDirectory = Join-Path $resolvedSmokeRoot "app"
$installLog = Join-Path $resolvedSmokeRoot "install.log"
$uninstallLog = Join-Path $resolvedSmokeRoot "uninstall.log"
$installedExecutable = Join-Path $installDirectory "GptAccountManager.exe"
$uninstalled = $false

New-Item -ItemType Directory -Path $resolvedSmokeRoot | Out-Null

try {
    $installProcess = Start-Process `
        -FilePath $resolvedInstaller `
        -ArgumentList @(
            "/VERYSILENT"
            "/SUPPRESSMSGBOXES"
            "/NORESTART"
            "/DIR=`"$installDirectory`""
            "/LOG=`"$installLog`""
        ) `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($installProcess.ExitCode -ne 0) {
        throw "Installer exited with code $($installProcess.ExitCode)."
    }

    if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
        throw "Installed application executable was not found."
    }

    $uninstaller = Get-ChildItem `
        -LiteralPath $installDirectory `
        -Filter "unins*.exe" `
        -File |
        Select-Object -First 1
    if (-not $uninstaller) {
        throw "Uninstaller was not created."
    }

    $uninstallProcess = Start-Process `
        -FilePath $uninstaller.FullName `
        -ArgumentList @(
            "/VERYSILENT"
            "/SUPPRESSMSGBOXES"
            "/NORESTART"
            "/LOG=`"$uninstallLog`""
        ) `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($uninstallProcess.ExitCode -ne 0) {
        throw "Uninstaller exited with code $($uninstallProcess.ExitCode)."
    }

    $uninstalled = $true
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    while (
        (Test-Path -LiteralPath $installedExecutable) -and
        [DateTimeOffset]::UtcNow -lt $deadline
    ) {
        Start-Sleep -Milliseconds 200
    }

    if (Test-Path -LiteralPath $installedExecutable) {
        throw "Application executable remained after uninstall."
    }

    Write-Output "Installer smoke test passed: $resolvedInstaller"
}
finally {
    if (-not $uninstalled -and (Test-Path -LiteralPath $installDirectory)) {
        $fallbackUninstaller = Get-ChildItem `
            -LiteralPath $installDirectory `
            -Filter "unins*.exe" `
            -File `
            -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($fallbackUninstaller) {
            Start-Process `
                -FilePath $fallbackUninstaller.FullName `
                -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") `
                -WindowStyle Hidden `
                -Wait
        }
    }

    $resolvedTempRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::GetTempPath())
    if (
        $resolvedSmokeRoot.StartsWith(
            $resolvedTempRoot,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedSmokeRoot)
    ) {
        Remove-Item -LiteralPath $resolvedSmokeRoot -Recurse -Force
    }
}
