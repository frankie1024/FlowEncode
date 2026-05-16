param(
    [string]$SourceDirectoryPath,
    [string]$Version,
    [string]$OutputRoot,
    [string]$ArtifactName,
    [string]$AppName = "FlowEncode",
    [string]$DisplayName = "FlowEncode",
    [string]$Publisher = "frankie1024",
    [string]$PublisherUrl = "https://github.com/frankie1024/FlowEncode"
)

$ErrorActionPreference = "Stop"

$webView2BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"

function Resolve-PathOrThrow {
    param(
        [string]$Path,
        [string]$Label,
        [string]$RepositoryRoot
    )

    $resolved = if ([System.IO.Path]::IsPathRooted($Path)) {
        $Path
    }
    else {
        Join-Path $RepositoryRoot $Path
    }

    if (-not (Test-Path $resolved)) {
        throw "$Label not found: $resolved"
    }

    return $resolved
}

function Resolve-IsccPath {
    $command = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "ISCC.exe was not found. Install Inno Setup 6 before building release assets."
}

function Resolve-VersionInfoVersion {
    param([string]$Text)

    if ($Text -match '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:\.(?<revision>\d+))?') {
        $revision = if ($Matches["revision"].Success) { $Matches["revision"].Value } else { "0" }
        return "{0}.{1}.{2}.{3}" -f $Matches["major"].Value, $Matches["minor"].Value, $Matches["patch"].Value, $revision
    }

    throw "VersionInfoVersion could not be resolved from version '$Text'."
}

function Save-WebView2Bootstrapper {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    Write-Host "Downloading WebView2 bootstrapper: $webView2BootstrapperUrl"
    try {
        Invoke-WebRequest `
            -Uri $webView2BootstrapperUrl `
            -OutFile $DestinationPath `
            -MaximumRedirection 8
    }
    catch {
        $curlPath = (Get-Command curl.exe -ErrorAction SilentlyContinue).Source
        if ([string]::IsNullOrWhiteSpace($curlPath)) {
            throw
        }

        Write-Host "Invoke-WebRequest failed, retrying with curl.exe"
        & $curlPath `
            --fail `
            --location `
            --silent `
            --show-error `
            --output $DestinationPath `
            $webView2BootstrapperUrl

        if ($LASTEXITCODE -ne 0) {
            throw
        }
    }

    if (-not (Test-Path $DestinationPath)) {
        throw "WebView2 bootstrapper download failed: $DestinationPath"
    }

    $downloadedFile = Get-Item -LiteralPath $DestinationPath
    if ($downloadedFile.Length -le 0) {
        throw "WebView2 bootstrapper download produced an empty file: $DestinationPath"
    }

    Test-MicrosoftAuthenticodeSignature -Path $DestinationPath -Label "WebView2 bootstrapper"
}

function Test-MicrosoftAuthenticodeSignature {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($null -eq $signature) {
        throw "$Label does not have an Authenticode signature: $Path"
    }

    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "$Label signature is not valid: $($signature.Status). $($signature.StatusMessage)"
    }

    $certificate = $signature.SignerCertificate
    if ($null -eq $certificate) {
        throw "$Label does not have a signer certificate: $Path"
    }

    $subject = $certificate.Subject
    if ($subject -notlike "*O=Microsoft Corporation*" -and $subject -notlike "*CN=Microsoft Corporation*") {
        throw "$Label signer is not Microsoft Corporation: $subject"
    }

    Write-Host "$Label signature verified: $subject"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$commonScript = Resolve-PathOrThrow -Path "scripts\release-artifact-common.ps1" -Label "Common release helper script" -RepositoryRoot $repoRoot
$installerArtScript = Resolve-PathOrThrow -Path "scripts\export-installer-art.ps1" -Label "Installer art export script" -RepositoryRoot $repoRoot
$installerScriptPath = Resolve-PathOrThrow -Path "installer\FlowEncode.iss" -Label "Installer script" -RepositoryRoot $repoRoot
$generatedInstallerAssetsRoot = Join-Path $repoRoot "installer\assets"
$wizardImagePath = Join-Path $generatedInstallerAssetsRoot "WizardImage.png"
$wizardSmallImagePath = Join-Path $generatedInstallerAssetsRoot "WizardSmallImage.png"
$setupIconPath = Resolve-PathOrThrow -Path "FlowEncode\Assets\SetupIcon.ico" -Label "Setup icon" -RepositoryRoot $repoRoot
$isccPath = Resolve-IsccPath

. $commonScript

if ([string]::IsNullOrWhiteSpace($SourceDirectoryPath)) {
    throw "SourceDirectoryPath is required."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Version is required."
}

$resolvedSourceDirectoryPath = Resolve-PathOrThrow -Path $SourceDirectoryPath -Label "Release source directory" -RepositoryRoot $repoRoot
$resolvedOutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot "artifacts\release"
}
else {
    if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
        $OutputRoot
    }
    else {
        Join-Path $repoRoot $OutputRoot
    }
}

$resolvedArtifactName = if ([string]::IsNullOrWhiteSpace($ArtifactName)) {
    "FlowEncode_Setup_v$Version"
}
else {
    $ArtifactName
}

$stagingParent = Join-Path $resolvedOutputRoot ".installer-staging"
$stagingRoot = Join-Path $stagingParent $resolvedArtifactName
$payloadRoot = Join-Path $stagingRoot "payload"
$prerequisitesRoot = Join-Path $stagingRoot "prerequisites"
$webView2BootstrapperPath = Join-Path $prerequisitesRoot "MicrosoftEdgeWebView2Setup.exe"
$outputExePath = Join-Path $resolvedOutputRoot "$resolvedArtifactName.exe"
$versionInfoVersion = Resolve-VersionInfoVersion -Text $Version

New-Item -ItemType Directory -Path $resolvedOutputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $generatedInstallerAssetsRoot -Force | Out-Null

& $installerArtScript
if (-not $?) {
    throw "export-installer-art.ps1 failed."
}

$wizardImagePath = Resolve-PathOrThrow -Path $wizardImagePath -Label "Wizard image" -RepositoryRoot $repoRoot
$wizardSmallImagePath = Resolve-PathOrThrow -Path $wizardSmallImagePath -Label "Wizard small image" -RepositoryRoot $repoRoot

if (Test-Path $outputExePath) {
    Remove-Item -Path $outputExePath -Force
}

if (Test-Path $stagingRoot) {
    Remove-Item -Path $stagingRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path $prerequisitesRoot -Force | Out-Null

try {
    Copy-Item -Path (Join-Path $resolvedSourceDirectoryPath "*") -Destination $payloadRoot -Recurse -Force
    Sanitize-ReleaseLayout -RootPath $payloadRoot
    Save-WebView2Bootstrapper -DestinationPath $webView2BootstrapperPath

    & $isccPath `
        "/Qp" `
        "/DSourceDir=$payloadRoot" `
        "/DWebView2BootstrapperFile=$webView2BootstrapperPath" `
        "/DOutputDir=$resolvedOutputRoot" `
        "/DOutputBaseName=$resolvedArtifactName" `
        "/DAppName=$AppName" `
        "/DAppDisplayName=$DisplayName" `
        "/DAppPublisher=$Publisher" `
        "/DAppPublisherUrl=$PublisherUrl" `
        "/DAppVersion=$Version" `
        "/DAppVersionInfo=$versionInfoVersion" `
        "/DAppExeName=${AppName}.exe" `
        "/DSetupIconFile=$setupIconPath" `
        "/DWizardImageFile=$wizardImagePath" `
        "/DWizardSmallImageFile=$wizardSmallImagePath" `
        $installerScriptPath
}
finally {
    if (Test-Path $stagingRoot) {
        Remove-Item -Path $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path $stagingParent) {
        $remainingItems = Get-ChildItem -Path $stagingParent -Force -ErrorAction SilentlyContinue
        if (($remainingItems | Measure-Object).Count -eq 0) {
            Remove-Item -Path $stagingParent -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $outputExePath)) {
    throw "Installer was not generated: $outputExePath"
}

Write-Host "Installer artifact ready: $outputExePath"
