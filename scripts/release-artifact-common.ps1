function Get-ReleaseTransientTopLevelDirectories {
    return @(
        "data",
        "downloads",
        "encoders",
        "tools",
        "toolset"
    )
}

function Get-ReleaseTransientDirectoryNames {
    return @(
        "__pycache__",
        "NpuDetect"
    )
}

function Get-ReleaseTransientFilePatterns {
    return @(
        "*.bak",
        "*.cache",
        "*.log",
        "*.old",
        "*.pdb",
        "*.pyc",
        "*.pyo",
        "*.tmp",
        "desktop.ini",
        "Thumbs.db"
    )
}

function Get-ReleaseOptionalRuntimeFilePatterns {
    return @(
        "createdump.exe",
        "DirectML.dll",
        "Microsoft.DiaSymReader.Native.amd64.dll",
        "Microsoft.ML.OnnxRuntime.dll",
        "Microsoft.Windows.AI.*",
        "Microsoft.Windows.Widgets.dll",
        "Microsoft.Windows.Widgets.Projection.dll",
        "Microsoft.Windows.Widgets.winmd",
        "Microsoft.Windows.Workloads*",
        "mscordaccore.dll",
        "mscordaccore_*",
        "mscordbi.dll",
        "onnxruntime.dll",
        "onnxruntime_providers_shared.dll",
        "PushNotificationsLongRunningTask.ProxyStub.dll",
        "RestartAgent.exe",
        "SessionHandleIPCProxyStub.dll",
        "WindowsAppRuntime.DeploymentExtensions.OneCore.dll",
        "WindowsAppSdk.AppxDeploymentExtensions.Desktop-EventLog-Instrumentation.dll",
        "WindowsAppSdk.AppxDeploymentExtensions.Desktop.dll",
        "workloads*.json"
    )
}

function Get-ReleaseAssetAllowList {
    return @(
        "FlowEncodeHammer.png",
        "VapourSynthScript.vpy"
    )
}

function Get-ReleaseAssetDirectoryAllowList {
    return @(
        "ThirdPartyNotices",
        "VapourSynthEditor"
    )
}

function Remove-UnwantedSatelliteResourceDirectories {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [string[]]$LanguagesToKeep = @("en-us", "zh-cn")
    )

    if (-not (Test-Path $RootPath)) {
        return
    }

    $normalizedLanguagesToKeep = $LanguagesToKeep |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Trim().ToLowerInvariant() }

    if ($normalizedLanguagesToKeep.Count -eq 0) {
        return
    }

    $cultureDirectoryPattern = '^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})+$'
    Get-ChildItem -Path $RootPath -Directory -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -match $cultureDirectoryPattern -and
            ($normalizedLanguagesToKeep -notcontains $_.Name.ToLowerInvariant())
        } |
        Remove-Item -Recurse -Force
}

function Remove-ReleaseDirectoriesByNames {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [Parameter(Mandatory = $true)]
        [string[]]$DirectoryNames,
        [switch]$TopLevelOnly
    )

    if (-not (Test-Path $RootPath)) {
        return
    }

    if ($TopLevelOnly) {
        foreach ($directoryName in $DirectoryNames) {
            if ([string]::IsNullOrWhiteSpace($directoryName)) {
                continue
            }

            $candidatePath = Join-Path $RootPath $directoryName
            if (Test-Path $candidatePath) {
                Remove-Item -Path $candidatePath -Recurse -Force
            }
        }

        return
    }

    $normalizedDirectoryNames = $DirectoryNames |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Trim() }

    if ($normalizedDirectoryNames.Count -eq 0) {
        return
    }

    Get-ChildItem -Path $RootPath -Recurse -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $normalizedDirectoryNames -contains $_.Name } |
        Sort-Object -Property FullName -Descending |
        Remove-Item -Recurse -Force
}

function Remove-ReleaseFilesByPatterns {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [Parameter(Mandatory = $true)]
        [string[]]$Patterns
    )

    if (-not (Test-Path $RootPath)) {
        return
    }

    foreach ($pattern in $Patterns) {
        if ([string]::IsNullOrWhiteSpace($pattern)) {
            continue
        }

        Get-ChildItem -Path $RootPath -Recurse -File -Force -Filter $pattern -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }
}

function Sanitize-ReleaseAssetDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    $assetDirectory = Join-Path $RootPath "Assets"
    if (-not (Test-Path $assetDirectory)) {
        return
    }

    $allowedAssets = Get-ReleaseAssetAllowList
    $allowedDirectories = Get-ReleaseAssetDirectoryAllowList
    Get-ChildItem -Path $assetDirectory -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $allowedAssets -notcontains $_.Name } |
        Remove-Item -Force

    Get-ChildItem -Path $assetDirectory -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $allowedDirectories -notcontains $_.Name } |
        Remove-Item -Recurse -Force

    if (-not (Get-ChildItem -Path $assetDirectory -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -Path $assetDirectory -Force -ErrorAction SilentlyContinue
    }
}

function Remove-EmptyDirectories {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    if (-not (Test-Path $RootPath)) {
        return
    }

    Get-ChildItem -Path $RootPath -Recurse -Directory -Force -ErrorAction SilentlyContinue |
        Sort-Object -Property FullName -Descending |
        Where-Object { -not (Get-ChildItem -Path $_.FullName -Force -ErrorAction SilentlyContinue) } |
        Remove-Item -Force
}

function Sanitize-ReleaseLayout {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [string[]]$SatelliteLanguageDirectories = @("en-us", "zh-cn")
    )

    if (-not (Test-Path $RootPath)) {
        return
    }

    Remove-ReleaseDirectoriesByNames -RootPath $RootPath -DirectoryNames (Get-ReleaseTransientTopLevelDirectories) -TopLevelOnly
    Remove-ReleaseDirectoriesByNames -RootPath $RootPath -DirectoryNames (Get-ReleaseTransientDirectoryNames)
    Remove-ReleaseFilesByPatterns -RootPath $RootPath -Patterns (Get-ReleaseTransientFilePatterns)
    Remove-ReleaseFilesByPatterns -RootPath $RootPath -Patterns (Get-ReleaseOptionalRuntimeFilePatterns)
    Sanitize-ReleaseAssetDirectory -RootPath $RootPath
    Remove-UnwantedSatelliteResourceDirectories -RootPath $RootPath -LanguagesToKeep $SatelliteLanguageDirectories
    Remove-EmptyDirectories -RootPath $RootPath
}
