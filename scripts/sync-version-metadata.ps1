param(
    [switch]$Check
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "repo-build-common.ps1")

function Get-UpdatedReadmeBadgeContent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    return [System.Text.RegularExpressions.Regex]::Replace(
        $Content,
        'https://img\.shields\.io/badge/version-[^-"]+-167a7f',
        "https://img.shields.io/badge/version-$Version-167a7f")
}

function Get-UpdatedXmlAttributeContent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,
        [Parameter(Mandatory = $true)]
        [string]$Pattern,
        [Parameter(Mandatory = $true)]
        [string]$Replacement
    )

    return [System.Text.RegularExpressions.Regex]::Replace(
        $Content,
        $Pattern,
        { param($match) $match.Groups[1].Value + $Replacement + $match.Groups[2].Value },
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

function Sync-TrackedFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$UpdatedContent,
        [Parameter(Mandatory = $true)]
        [string]$MismatchMessage,
        [switch]$CheckOnly
    )

    $content = Get-Content -Path $Path -Raw -Encoding utf8
    if ([string]::Equals($content, $UpdatedContent, [System.StringComparison]::Ordinal)) {
        return $false
    }

    if ($CheckOnly) {
        $script:mismatches.Add($MismatchMessage)
        return $false
    }

    Set-Content -Path $Path -Value $UpdatedContent -NoNewline -Encoding utf8
    $script:changedFiles.Add($Path)
    return $true
}

$repoRoot = Get-RepositoryRoot
$versionInfo = Get-FlowEncodeVersionInfo
$packageManifestPath = Join-Path $repoRoot "FlowEncode\Package.appxmanifest"
$applicationManifestPath = Join-Path $repoRoot "FlowEncode\app.manifest"
$readmePaths = @(
    (Join-Path $repoRoot "README.md"),
    (Join-Path $repoRoot "README.en.md")
)

$changedFiles = New-Object System.Collections.Generic.List[string]
$mismatches = New-Object System.Collections.Generic.List[string]

$packageManifestContent = Get-Content -Path $packageManifestPath -Raw
$updatedPackageManifestContent = Get-UpdatedXmlAttributeContent `
    -Content $packageManifestContent `
    -Pattern '(<Identity\b[^>]*\bVersion=")[^"]+(")' `
    -Replacement $versionInfo.VersionInfo
Sync-TrackedFile `
    -Path $packageManifestPath `
    -UpdatedContent $updatedPackageManifestContent `
    -MismatchMessage "Version metadata is out of sync: $packageManifestPath" `
    -CheckOnly:$Check | Out-Null

$applicationManifestContent = Get-Content -Path $applicationManifestPath -Raw
$updatedApplicationManifestContent = Get-UpdatedXmlAttributeContent `
    -Content $applicationManifestContent `
    -Pattern '(<assemblyIdentity\b[^>]*\bversion=")[^"]+(")' `
    -Replacement $versionInfo.VersionInfo
Sync-TrackedFile `
    -Path $applicationManifestPath `
    -UpdatedContent $updatedApplicationManifestContent `
    -MismatchMessage "Version metadata is out of sync: $applicationManifestPath" `
    -CheckOnly:$Check | Out-Null

foreach ($readmePath in $readmePaths) {
    $readmeContent = Get-Content -Path $readmePath -Raw
    $updatedReadmeContent = Get-UpdatedReadmeBadgeContent -Content $readmeContent -Version $versionInfo.Version
    Sync-TrackedFile `
        -Path $readmePath `
        -UpdatedContent $updatedReadmeContent `
        -MismatchMessage "Version badge is out of sync: $readmePath" `
        -CheckOnly:$Check | Out-Null
}

if ($Check -and $mismatches.Count -eq 0) {
    Write-Host "Version metadata is synchronized."
}
elseif ($Check) {
    throw ("Version metadata is out of sync:`n - " + ($mismatches -join "`n - "))
}
elseif (-not $Check -and $changedFiles.Count -eq 0) {
    Write-Host "No version metadata updates were required."
}
elseif (-not $Check) {
    Write-Host "Updated version metadata:"
    foreach ($file in $changedFiles) {
        Write-Host " - $file"
    }
}
