using System;
using System.Collections.Generic;
using FlowEncode.Domain;
using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public sealed class SetupGuideDependencyItemViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private bool _isBusy;
    private bool _isInstallIndeterminate;
    private double _installProgressPercent;
    private string _installProgressText;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(ProgressVisibility));
                OnPropertyChanged(nameof(PrimaryActionEnabled));
                OnPropertyChanged(nameof(AdvancedUpdateEnabled));
                OnPropertyChanged(nameof(DeewComponentsDownloadEnabled));
                OnPropertyChanged(nameof(UninstallActionEnabled));
                OnPropertyChanged(nameof(ManualSelectEnabled));
                OnPropertyChanged(nameof(ClearManualPinEnabled));
                OnPropertyChanged(nameof(CanInstallOtherPlugins));
                OnPropertyChanged(nameof(OtherPluginControlsEnabled));
            }
        }
    }

    public bool IsInstallIndeterminate
    {
        get => _isInstallIndeterminate;
        private set
        {
            if (SetProperty(ref _isInstallIndeterminate, value))
            {
                OnPropertyChanged(nameof(InstallProgressPercentText));
            }
        }
    }

    public double InstallProgressPercent
    {
        get => _installProgressPercent;
        private set
        {
            if (SetProperty(ref _installProgressPercent, value))
            {
                OnPropertyChanged(nameof(InstallProgressPercentText));
            }
        }
    }

    public string InstallProgressText
    {
        get => _installProgressText;
        private set => SetProperty(ref _installProgressText, value);
    }

    public SetupGuideDependencyItemViewModel(
        SetupDependencyKind kind,
        string title,
        ReadinessState state,
        string statusTitle,
        string inlineStatusText,
        string statusText,
        string warningText,
        string executablePath,
        string releaseUrl,
        string primaryActionText,
        bool isPrimaryActionVisible,
        bool isPrimaryActionEnabled,
        bool isUninstallVisible,
        bool isUninstallEnabled,
        bool isManualSelectVisible,
        bool isManualPinned,
        string moreActionText = "More",
        string advancedUpdateText = "Update tool (not recommended)",
        bool isUpdateAction = false,
        string recommendedVersionHelp = "")
    {
        Kind = kind;
        RecommendedVersionHelp = recommendedVersionHelp;
        IsUpdateAction = isUpdateAction;
        MoreActionText = moreActionText;
        AdvancedUpdateText = advancedUpdateText;
        Title = title;
        State = state;
        StatusTitle = statusTitle;
        InlineStatusText = inlineStatusText;
        StatusText = statusText;
        WarningText = warningText;
        ExecutablePath = executablePath;
        ReleaseUrl = releaseUrl;
        PrimaryActionText = primaryActionText;
        IsPrimaryActionVisible = isPrimaryActionVisible;
        IsPrimaryActionBaseEnabled = isPrimaryActionEnabled;
        IsUninstallVisible = isUninstallVisible;
        IsUninstallBaseEnabled = isUninstallEnabled;
        IsManualSelectVisible = isManualSelectVisible;
        IsManualPinned = isManualPinned;
        _installProgressText = string.Empty;
    }

    public string PrimaryActionToolTip => string.IsNullOrWhiteSpace(RecommendedVersionHelp) ? PrimaryActionText : RecommendedVersionHelp;
    public string RecommendedVersionHelp { get; }
    public SetupDependencyKind Kind { get; }
    public bool IsUpdateAction { get; }
    public Visibility SettingsPrimaryActionVisibility => IsUpdateAction ? Visibility.Collapsed : PrimaryActionVisibility;
    public Visibility MoreActionsVisibility => IsManualSelectVisible || IsManualPinned || ToolchainBaseline.HasBaseline(Kind) || IsUpdateAction
        || !string.IsNullOrWhiteSpace(ReleaseUrl) ? Visibility.Visible : Visibility.Collapsed;
    public string MoreActionText { get; }
    public string AdvancedUpdateText { get; }
    public Visibility AdvancedUpdateVisibility => ToolchainBaseline.HasBaseline(Kind) ? Visibility.Visible : Visibility.Collapsed;
    public bool AdvancedUpdateEnabled => !IsBusy;

    public IReadOnlyList<PythonPackageStatus> PythonPackages { get; init; } = [];
    public IReadOnlyList<OtherVapourSynthPluginItem> OtherPlugins { get; init; } = [];
    public Visibility OtherPluginsVisibility => Kind == SetupDependencyKind.VapourSynthUtilityPlugins ? Visibility.Visible : Visibility.Collapsed;
    public bool IsOtherPluginInstallationAvailable { get; init; }
    public bool OtherPluginControlsEnabled => !IsBusy;
    public bool CanInstallOtherPlugins => IsOtherPluginInstallationAvailable && OtherPluginControlsEnabled;

    public Visibility JetpackPackagesVisibility => Kind == SetupDependencyKind.Vsjetpack && PythonPackages.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool DeewComponentsDownloadEnabled => Kind == SetupDependencyKind.Deew && !IsBusy
        && State == ReadinessState.Ready && !string.IsNullOrWhiteSpace(ExecutablePath);


    public string Title { get; }


    public ReadinessState State { get; }

    public string StatusTitle { get; }

    public string InlineStatusText { get; }
    public Visibility InlineStatusVisibility => string.IsNullOrWhiteSpace(InlineStatusText) ? Visibility.Collapsed : Visibility.Visible;

    public string StatusText { get; }
    public Visibility StatusTextVisibility => string.IsNullOrWhiteSpace(StatusText) ? Visibility.Collapsed : Visibility.Visible;

    public string WarningText { get; }


    public string ExecutablePath { get; }


    public string ReleaseUrl { get; }

    public string PrimaryActionText { get; }

    public bool IsPrimaryActionVisible { get; }

    public bool IsPrimaryActionBaseEnabled { get; }

    public bool IsUninstallVisible { get; }

    public bool IsUninstallBaseEnabled { get; }
    public string UninstallToolTip { get; init; } = string.Empty;

    public bool IsManualSelectVisible { get; }

    public bool IsManualPinned { get; }

    public Visibility ReadyBadgeVisibility => State == ReadinessState.Ready ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WarningBadgeVisibility => State is ReadinessState.Partial or ReadinessState.Unknown ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BlockedBadgeVisibility => State is ReadinessState.Missing or ReadinessState.Misconfigured ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WarningVisibility => string.IsNullOrWhiteSpace(WarningText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PrimaryActionVisibility => IsPrimaryActionVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UninstallVisibility => IsUninstallVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ManualSelectVisibility => IsManualSelectVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ClearManualPinVisibility => IsManualPinned ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ReleaseVisibility => string.IsNullOrWhiteSpace(ReleaseUrl) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ProgressVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public string InstallProgressPercentText => IsInstallIndeterminate
        ? "--"
        : $"{Math.Round(InstallProgressPercent):0}%";

    public bool PrimaryActionEnabled => IsPrimaryActionBaseEnabled && !IsBusy;

    public bool UninstallActionEnabled => IsUninstallBaseEnabled && !IsBusy;

    public bool ManualSelectEnabled => IsManualSelectVisible && !IsBusy;

    public bool ClearManualPinEnabled => IsManualPinned && !IsBusy;

    public void BeginOperation()
    {
        IsBusy = true;
        IsInstallIndeterminate = false;
        InstallProgressPercent = 0;
        InstallProgressText = string.Empty;
    }

    public void ReportProgress(SetupInstallProgress progress)
    {
        IsInstallIndeterminate = progress.IsIndeterminate;
        InstallProgressPercent = progress.Percent;
        InstallProgressText = progress.StatusText;
    }

    public void FinishOperation()
    {
        IsInstallIndeterminate = false;
        IsBusy = false;
    }
}

public sealed record OtherVapourSynthPluginItem(
    string Name, string Label, string Status, string InstallLabel, string UpdateLabel, string UninstallLabel,
    string UninstallAutomationName, bool IsInstalled, bool CanInstall)
{
    public string Summary => $"{Label} · {Status}";
    public Visibility InstallVisibility => IsInstalled ? Visibility.Collapsed : Visibility.Visible;
    public Visibility UpdateVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
}
