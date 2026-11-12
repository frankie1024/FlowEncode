param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$OutputRoot,
    [string[]]$SatelliteLanguageDirectories = @("en-us", "zh-cn")
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "repo-build-common.ps1")

function Remove-DirectoryIfExists {
    param([string]$Path)

    $resolvedTarget = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $allowedTargets = @($publishRoot, $resolvedOutputRoot) | ForEach-Object { [System.IO.Path]::GetFullPath($_).TrimEnd('\', '/') }
    if ($resolvedTarget -notin $allowedTargets -or $resolvedTarget -eq $repoRoot -or
        $resolvedTarget -eq [System.IO.Path]::GetPathRoot($resolvedTarget).TrimEnd('\', '/') -or
        $repoRoot.StartsWith($resolvedTarget + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unsafe release directory: $resolvedTarget"
    }
    if (Test-Path -LiteralPath $resolvedTarget) {
        if ((Get-Item -LiteralPath $resolvedTarget).Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw "Release directory is a reparse point: $resolvedTarget" }
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
}

function Get-Sha256Hash {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $hashCommand = Get-Command Get-FileHash -ErrorAction SilentlyContinue
    if ($null -ne $hashCommand) {
        return (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    }

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $bytes = $sha256.ComputeHash($stream)
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    return (-join ($bytes | ForEach-Object { $_.ToString("x2") })).ToUpperInvariant()
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "FlowEncode\FlowEncode.csproj"
$commonScript = Join-Path $repoRoot "scripts\release-artifact-common.ps1"
$installerBuildScript = Join-Path $repoRoot "scripts\build-installer.ps1"
$versionSyncScript = Join-Path $repoRoot "scripts\sync-version-metadata.ps1"
$msbuildPath = Resolve-MsBuildPath

if (-not (Test-Path $projectPath)) {
    throw "Project was not found: $projectPath"
}

if (-not (Test-Path $commonScript)) {
    throw "Common release helper script was not found: $commonScript"
}

if (-not (Test-Path $installerBuildScript)) {
    throw "Installer build script was not found: $installerBuildScript"
}

. $commonScript

$versionInfo = Get-FlowEncodeVersionInfo
$projectVersion = $versionInfo.Version

& $versionSyncScript -Check

$resolvedVersion = if ([string]::IsNullOrWhiteSpace($Version)) {
    $projectVersion
}
else {
    $Version.Trim()
}

if (-not [string]::Equals($resolvedVersion, $projectVersion, [System.StringComparison]::Ordinal)) {
    throw "Explicit -Version '$resolvedVersion' does not match project version '$projectVersion'. Update build\\Version.props first or omit -Version."
}

$resolvedOutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot "artifacts\release\v$resolvedVersion"
}
else {
    if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
        $OutputRoot
    }
    else {
        Join-Path $repoRoot $OutputRoot
    }
}

$publishRoot = Join-Path $repoRoot "artifacts\publish\v$resolvedVersion\app"
$installerBaseName = "FlowEncode_Setup_v$resolvedVersion"
$installerArtifactPath = Join-Path $resolvedOutputRoot "$installerBaseName.exe"
$publishDirArgument = if ($publishRoot.EndsWith("\") -or $publishRoot.EndsWith("/")) { $publishRoot } else { "$publishRoot\" }

Remove-DirectoryIfExists -Path $publishRoot
Remove-DirectoryIfExists -Path $resolvedOutputRoot
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $resolvedOutputRoot -Force | Out-Null

$buildArgs = @(
    $projectPath,
    "/restore",
    "/t:Publish",
    "/p:Configuration=$Configuration",
    "/p:Platform=$Platform",
    "/p:PublishProfile=win-$Platform",
    "/p:PublishDir=$publishDirArgument",
    "/m:1"
)

Write-Host "Using MSBuild: $msbuildPath"
Write-Host "Publishing version: $resolvedVersion"
& $msbuildPath @buildArgs

if ($LASTEXITCODE -ne 0) {
    throw "MSBuild Publish failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path (Join-Path $publishRoot "FlowEncode.exe"))) {
    throw "Publish completed but executable was not found in $publishRoot"
}

Sanitize-ReleaseLayout -RootPath $publishRoot -SatelliteLanguageDirectories $SatelliteLanguageDirectories

$mcpPublishRoot = Join-Path $publishRoot "mcp"
& dotnet publish (Join-Path $repoRoot "FlowEncode\FlowEncode.Mcp\FlowEncode.Mcp.csproj") -c $Configuration -r win-x64 --self-contained true -o $mcpPublishRoot --nologo
if ($LASTEXITCODE -ne 0) { throw "MCP publish failed." }
if (-not (Test-Path -LiteralPath (Join-Path $mcpPublishRoot "FlowEncode.Mcp.exe"))) { throw "MCP executable missing." }
$portablePath = Join-Path $resolvedOutputRoot "FlowEncode_Portable_v$resolvedVersion.zip"
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $portablePath -CompressionLevel Optimal

& $installerBuildScript `
    -SourceDirectoryPath $publishRoot `
    -Version $resolvedVersion `
    -OutputRoot $resolvedOutputRoot `
    -ArtifactName $installerBaseName

if (-not $?) {
    throw "Installer build failed."
}

if (-not (Test-Path $installerArtifactPath)) {
    throw "Installer artifact was not generated: $installerArtifactPath"
}

$installerHash = Get-Sha256Hash -Path $installerArtifactPath
# Republish the exact verified multilib binary and matching source/license bundle
# alongside the stable app: its managed x265 baseline resolves assets from this release.
$x265Assets = @(
    @{ Name = 'x265-4.3-x64.zip'; Sha256 = '499913e1e898b64078a795c9edcd0ea8805fe05417091ce9abad0572f70f0b3d' },
    @{ Name = 'x265-4.3-multilib-source-license.zip'; Sha256 = 'db428fc8cdba3110ead30155207e7e529fcaacf0a1f8fc5e23ea698092b83077' }
)
foreach ($asset in $x265Assets) {
    $assetPath = Join-Path $resolvedOutputRoot $asset.Name
    Invoke-WebRequest -Uri ("https://github.com/frankie1024/FlowEncode/releases/download/v3.0.0-beta.1/" + $asset.Name) -OutFile $assetPath
    if ((Get-Sha256Hash -Path $assetPath) -ne $asset.Sha256) {
        throw "Verified x265 release asset checksum mismatch: $($asset.Name)"
    }
}
$checksums = @($installerArtifactPath, $portablePath) + @($x265Assets | ForEach-Object { Join-Path $resolvedOutputRoot $_.Name }) | ForEach-Object {
    "{0} *{1}" -f (Get-Sha256Hash -Path $_).ToLowerInvariant(), [System.IO.Path]::GetFileName($_)
}
$checksums | Set-Content -LiteralPath (Join-Path $resolvedOutputRoot 'SHA256SUMS.txt') -Encoding ascii

[pscustomobject]@{
    Version = $resolvedVersion
    InstallerExe = $installerArtifactPath
    InstallerExeSha256 = $installerHash
    PortableZip = $portablePath
} | Format-List
