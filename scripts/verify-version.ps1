param(
    [string]$ExpectedVersion
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "version.ps1")

$sourceVersion = Get-RepositoryVersion -RepositoryRoot $repoRoot
Assert-SemanticVersion -Version $sourceVersion

if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $normalizedExpected = $ExpectedVersion.Trim()
    if ($normalizedExpected.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $normalizedExpected = $normalizedExpected.Substring(1)
    }

    Assert-SemanticVersion -Version $normalizedExpected
    if (-not $sourceVersion.Equals(
            $normalizedExpected,
            [StringComparison]::Ordinal)) {
        throw "Version mismatch: Version.props contains '$sourceVersion' but release expects '$normalizedExpected'."
    }
}

Write-Output "Verified repository version $sourceVersion"
