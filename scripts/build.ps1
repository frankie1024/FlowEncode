param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$Target = "Build",
    [switch]$RestoreOnly,
    [switch]$RunTests,
    [string]$MsBuildPath
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "repo-build-common.ps1")

$repoRoot = Get-RepositoryRoot
$projectPath = Join-Path $repoRoot "FlowEncode\FlowEncode.csproj"
$versionSyncScript = Join-Path $PSScriptRoot "sync-version-metadata.ps1"
$testScript = Join-Path $PSScriptRoot "test.ps1"

if (-not (Test-Path $projectPath)) {
    throw "Project was not found: $projectPath"
}

& $versionSyncScript -Check

$resolvedMsBuildPath = if ([string]::IsNullOrWhiteSpace($MsBuildPath)) {
    Resolve-MsBuildPath
}
else {
    $MsBuildPath
}

$buildTarget = if ($RestoreOnly) { "Restore" } else { $Target }

$buildArgs = @(
    $projectPath,
    "/t:$buildTarget",
    "/p:Configuration=$Configuration",
    "/p:Platform=$Platform",
    "/m"
)

if ($env:CI -eq "true") {
    $buildArgs += "/p:RestoreLockedMode=true"
}

if (-not $RestoreOnly) {
    $buildArgs = @($projectPath, "/restore") + $buildArgs[1..($buildArgs.Count - 1)]
}

Write-Host "Using MSBuild: $resolvedMsBuildPath"
Write-Host "Build target: $buildTarget"
& $resolvedMsBuildPath @buildArgs

if ($LASTEXITCODE -ne 0) {
    throw "MSBuild $buildTarget failed with exit code $LASTEXITCODE."
}

if ($RunTests) {
    & $testScript -Configuration $Configuration
    if (-not $?) {
        throw "Tests failed."
    }
}
