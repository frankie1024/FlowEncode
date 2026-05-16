param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "repo-build-common.ps1")

$repoRoot = Get-RepositoryRoot
$versionSyncScript = Join-Path $PSScriptRoot "sync-version-metadata.ps1"
$testProjectPath = Join-Path $repoRoot "FlowEncode\FlowEncode.Domain.Tests\FlowEncode.Domain.Tests.csproj"
$testResultsDirectory = Join-Path $repoRoot "artifacts\test-results\$Configuration"

if (-not (Test-Path $testProjectPath)) {
    throw "Test project was not found: $testProjectPath"
}

& $versionSyncScript -Check
New-Item -ItemType Directory -Path $testResultsDirectory -Force | Out-Null
$runExternalSmokeTests = $env:FLOWENCODE_RUN_EXTERNAL_SMOKE_TESTS
$externalSmokeTestsEnabled = $runExternalSmokeTests -eq "true" -or $runExternalSmokeTests -eq "1"

$testArgs = @(
    "test",
    $testProjectPath,
    "--configuration",
    $Configuration,
    "--nologo",
    "--verbosity",
    "minimal",
    "--logger",
    "trx",
    "--results-directory",
    $testResultsDirectory
)

if ($env:CI -eq "true") {
    $testArgs += "/p:RestoreLockedMode=true"
}

if (-not $externalSmokeTestsEnabled) {
    $testArgs += @(
        "--filter",
        "TestCategory!=ExternalToolSmoke"
    )
}

Write-Host "Running tests: $testProjectPath"
& dotnet @testArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet test failed with exit code $LASTEXITCODE."
}
