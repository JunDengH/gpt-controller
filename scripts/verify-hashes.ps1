param(
    [string[]]$Roots = @("artifacts")
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$hashFiles = foreach ($root in $Roots) {
    $resolvedRoot = Join-Path $repoRoot $root
    if (Test-Path -LiteralPath $resolvedRoot) {
        Get-ChildItem -LiteralPath $resolvedRoot -Filter "*.sha256" -File
    }
}

if (-not $hashFiles) {
    throw "No SHA-256 files were found."
}

foreach ($hashFile in $hashFiles) {
    $assetPath = $hashFile.FullName.Substring(
        0,
        $hashFile.FullName.Length - ".sha256".Length)
    if (-not (Test-Path -LiteralPath $assetPath)) {
        throw "Hash target does not exist: $assetPath"
    }

    $record = (Get-Content -LiteralPath $hashFile.FullName -Raw).Trim()
    $expected = ($record -split "\s+", 2)[0].ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "SHA-256 mismatch: $assetPath"
    }

    Write-Output "Verified $assetPath"
}
