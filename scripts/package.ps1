param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$OutputRoot = "artifacts"
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "version.ps1")
$sourceVersion = Get-RepositoryVersion -RepositoryRoot $repoRoot
Assert-SemanticVersion -Version $sourceVersion
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $sourceVersion
}
else {
    Assert-SemanticVersion -Version $Version
}

$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
$publishDirectory = Join-Path $artifactRoot "publish"
$helperPublishDirectory = Join-Path $artifactRoot "helper-publish"
$portableArchive = Join-Path $artifactRoot "GptController-$Version-win-x64.zip"
$portableHash = "$portableArchive.sha256"
$installer = Join-Path $artifactRoot "GptController-$Version-win-x64-setup.exe"
$installerHashPath = "$installer.sha256"

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
foreach ($stalePath in @(
    $publishDirectory
    $helperPublishDirectory
    $portableArchive
    $portableHash
    $installer
    $installerHashPath
)) {
    if (Test-Path -LiteralPath $stalePath) {
        Remove-Item -LiteralPath $stalePath -Recurse -Force
    }
}

dotnet publish (Join-Path $repoRoot "src\GptController\GptController.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishProfile=win-x64 `
    -p:Version=$Version `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "GPT Controller publish failed with exit code $LASTEXITCODE."
}

dotnet publish (Join-Path $repoRoot "src\GptController.CredentialHelper\GptController.CredentialHelper.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -o $helperPublishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Credential helper publish failed with exit code $LASTEXITCODE."
}

$helperExecutable = Join-Path $helperPublishDirectory "GptController.CredentialHelper.exe"
if (-not (Test-Path -LiteralPath $helperExecutable -PathType Leaf)) {
    throw "Credential helper publish did not produce the expected executable."
}

Copy-Item -LiteralPath $helperExecutable -Destination $publishDirectory -Force

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
    & $isccPath "/DMyAppVersion=$Version" (Join-Path $repoRoot "installer\GptController.iss")
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
        throw "Inno Setup did not produce an installer."
    }

    $installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $installerHashPath `
        -Value "$installerHash  $(Split-Path $installer -Leaf)" `
        -Encoding ascii
    Write-Output "Created $installer"
    Write-Output "Created $installerHashPath"
}

Write-Output "Created $portableArchive"
Write-Output "Created $portableHash"

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $helperPublishDirectory) {
    Remove-Item -LiteralPath $helperPublishDirectory -Recurse -Force
}
