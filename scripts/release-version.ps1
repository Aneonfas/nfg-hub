function ConvertTo-NfgReleaseVersion {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Version
    )

    if ($Version -ne $Version.Trim()) {
        throw "Release version '$Version' contains leading or trailing whitespace."
    }

    try {
        $semanticVersion = [System.Management.Automation.SemanticVersion]::Parse($Version)
    }
    catch {
        throw "Release version '$Version' is not valid SemVer."
    }

    if ($semanticVersion.ToString() -cne $Version) {
        throw "Release version '$Version' is not in canonical SemVer form."
    }
    if (-not [string]::IsNullOrEmpty($semanticVersion.BuildLabel)) {
        throw 'Release versions must not contain SemVer build metadata.'
    }

    return $semanticVersion.ToString()
}

function ConvertFrom-NfgReleaseTag {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Tag
    )

    if (-not $Tag.StartsWith('v', [System.StringComparison]::Ordinal) -or $Tag.Length -eq 1) {
        throw "Release tag '$Tag' must use the vX.Y.Z format."
    }

    return ConvertTo-NfgReleaseVersion -Version $Tag.Substring(1)
}
