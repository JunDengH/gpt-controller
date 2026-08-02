function Get-RepositoryVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot
    )

    $versionFile = Join-Path $RepositoryRoot "Version.props"
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
        throw "Version source does not exist: $versionFile"
    }

    [xml]$document = Get-Content -LiteralPath $versionFile -Raw
    $versions = @(
        $document.Project.PropertyGroup.Version |
            ForEach-Object {
                if ($_ -is [System.Xml.XmlElement]) {
                    $_.InnerText.Trim()
                }
                else {
                    "$($_)".Trim()
                }
            } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($versions.Count -ne 1) {
        throw "Version.props must contain exactly one non-empty Version property."
    }

    return $versions[0]
}

function Assert-SemanticVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $numericIdentifier = '(?:0|[1-9]\d*)'
    $prereleaseIdentifier =
        "(?:$numericIdentifier|\d*[A-Za-z-][0-9A-Za-z-]*)"
    $buildIdentifier = '[0-9A-Za-z-]+'
    $pattern = "^$numericIdentifier\.$numericIdentifier\.$numericIdentifier" +
        "(?:-$prereleaseIdentifier(?:\.$prereleaseIdentifier)*)?" +
        "(?:\+$buildIdentifier(?:\.$buildIdentifier)*)?$"
    if ($Version -notmatch $pattern) {
        throw "Version is not valid SemVer 2.0: $Version"
    }
}
