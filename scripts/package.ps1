param(
    [string]$Version = "0.1.0",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "artifacts"
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
$publishDirectory = Join-Path $artifactRoot "publish"
$portableArchive = Join-Path $artifactRoot "GptAccountManager-$Version-win-x64.zip"
$portableHash = "$portableArchive.sha256"

dotnet test (Join-Path $repoRoot "GptAccountManager.slnx") -c $Configuration
dotnet publish (Join-Path $repoRoot "src\GptAccountManager\GptAccountManager.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishProfile=win-x64 `
    -p:Version=$Version `
    -o $publishDirectory

if (Test-Path -LiteralPath $portableArchive) {
    Remove-Item -LiteralPath $portableArchive -Force
}

Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $portableArchive
$hash = (Get-FileHash -LiteralPath $portableArchive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $portableHash -Value "$hash  $(Split-Path $portableArchive -Leaf)" -Encoding ascii

$isccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$isccPath = if ($isccCommand) {
    $isccCommand.Source
}
else {
    $uninstallRoots = @(
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*"
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )
    $registeredPaths = Get-ItemProperty $uninstallRoots -ErrorAction SilentlyContinue |
        Where-Object {
            $_.DisplayName -like "Inno Setup*" -and
            -not [string]::IsNullOrWhiteSpace($_.InstallLocation)
        } |
        ForEach-Object { Join-Path $_.InstallLocation "ISCC.exe" }

    @(
        $registeredPaths
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if ($isccPath) {
    & $isccPath "/DMyAppVersion=$Version" (Join-Path $repoRoot "installer\GptAccountManager.iss")
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
    }

    $installer = Get-ChildItem (Join-Path $repoRoot "installer\Output\*.exe") |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if (-not $installer) {
        throw "Inno Setup did not produce an installer."
    }

    $installerHashPath = "$($installer.FullName).sha256"
    $installerHash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $installerHashPath `
        -Value "$installerHash  $($installer.Name)" `
        -Encoding ascii
    Write-Output "Created $($installer.FullName)"
    Write-Output "Created $installerHashPath"
}

Write-Output "Created $portableArchive"
Write-Output "Created $portableHash"
