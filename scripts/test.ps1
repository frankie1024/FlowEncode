param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "repo-build-common.ps1")

$repoRoot = Get-RepositoryRoot
$versionSyncScript = Join-Path $PSScriptRoot "sync-version-metadata.ps1"
$testProjectPath = Join-Path $repoRoot "FlowEncode\FlowEncode.Domain.Tests\FlowEncode.Domain.Tests.csproj"

if (-not (Test-Path $testProjectPath)) {
    throw "Test project was not found: $testProjectPath"
}

& $versionSyncScript -Check

$testArgs = @(
    "test",
    $testProjectPath,
    "--configuration",
    $Configuration,
    "--nologo",
    "--verbosity",
    "minimal"
)

if ($env:CI -eq "true") {
    $testArgs += "/p:RestoreLockedMode=true"
}

Write-Host "Running tests: $testProjectPath"
& dotnet @testArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet test failed with exit code $LASTEXITCODE."
}
