function Get-RepositoryRoot {
    return Split-Path -Parent $PSScriptRoot
}

function Get-VersionPropsPath {
    return Join-Path (Get-RepositoryRoot) "build\Version.props"
}

function ConvertTo-VersionInfo {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    if ($Version -match '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:\.(?<revision>\d+)|-(?:alpha|beta|rc)\.(?<pre>\d+))?$') {
        $revision = if ($Matches["revision"]) { $Matches["revision"] } elseif ($Matches["pre"]) { $Matches["pre"] } else { "0" }
        return [string]::Format(
            "{0}.{1}.{2}.{3}",
            $Matches["major"],
            $Matches["minor"],
            $Matches["patch"],
            $revision)
    }

    throw "FlowEncodeVersion must use a numeric version or major.minor.patch-alpha/beta/rc.N: $Version"
}

function Assert-FlowEncodeVersionInfoConsistency {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [string]$VersionInfo
    )

    $expectedVersionInfo = ConvertTo-VersionInfo -Version $Version
    if (-not [string]::Equals($VersionInfo, $expectedVersionInfo, [System.StringComparison]::Ordinal)) {
        throw "FlowEncodeVersionInfo '$VersionInfo' does not match FlowEncodeVersion '$Version'. Expected '$expectedVersionInfo'."
    }
}

function Get-FlowEncodeVersionInfo {
    param(
        [string]$VersionPropsPath = (Get-VersionPropsPath)
    )

    if (-not (Test-Path $VersionPropsPath)) {
        throw "Version props file was not found: $VersionPropsPath"
    }

    [xml]$versionXml = Get-Content -Path $VersionPropsPath -Raw
    $propertyGroups = @($versionXml.Project.PropertyGroup)

    $version = $null
    $versionInfo = $null

    foreach ($group in $propertyGroups) {
        if (-not $version -and $group.FlowEncodeVersion -and -not [string]::IsNullOrWhiteSpace($group.FlowEncodeVersion)) {
            $version = $group.FlowEncodeVersion.Trim()
        }

        if (-not $versionInfo -and $group.FlowEncodeVersionInfo -and -not [string]::IsNullOrWhiteSpace($group.FlowEncodeVersionInfo)) {
            $versionInfo = $group.FlowEncodeVersionInfo.Trim()
        }
    }

    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "FlowEncodeVersion was not found in $VersionPropsPath"
    }

    if ([string]::IsNullOrWhiteSpace($versionInfo)) {
        throw "FlowEncodeVersionInfo was not found in $VersionPropsPath"
    }

    Assert-FlowEncodeVersionInfoConsistency -Version $version -VersionInfo $versionInfo

    return [pscustomobject]@{
        Version = $version
        VersionInfo = $versionInfo
    }
}

function Resolve-MsBuildPath {
    $command = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $vswherePath = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswherePath) {
        $installationPath = & $vswherePath -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if (-not [string]::IsNullOrWhiteSpace($installationPath)) {
            $candidate = Join-Path $installationPath "MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path $candidate) {
                return $candidate
            }
        }
    }

    $candidates = @(
        "E:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "MSBuild.exe was not found. Use Visual Studio MSBuild for this WinUI project."
}
