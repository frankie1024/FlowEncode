using System;
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
                OnPropertyChanged(nameof(UninstallActionEnabled));
                OnPropertyChanged(nameof(ManualSelectEnabled));
                OnPropertyChanged(nameof(ClearManualPinEnabled));
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
        string description,
        ReadinessState state,
        string statusTitle,
        string statusText,
        string installedVersionText,
        string latestVersionText,
        string warningText,
        string detailText,
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
        string advancedUpdateText = "Update tool (not recommended)")
    {
        Kind = kind;
        MoreActionText = moreActionText;
        AdvancedUpdateText = advancedUpdateText;
        Title = title;
        Description = description;
        State = state;
        StatusTitle = statusTitle;
        StatusText = statusText;
        InstalledVersionText = installedVersionText;
        LatestVersionText = latestVersionText;
        WarningText = warningText;
        DetailText = detailText;
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

    public SetupDependencyKind Kind { get; }
    public string MoreActionText { get; }
    public string AdvancedUpdateText { get; }
    public Visibility AdvancedUpdateVisibility => ToolchainBaseline.HasBaseline(Kind) ? Visibility.Visible : Visibility.Collapsed;
    public bool AdvancedUpdateEnabled => !IsBusy;


    public string Title { get; }

    public string Description { get; }

    public ReadinessState State { get; }

    public string StatusTitle { get; }

    public string StatusText { get; }

    public string InstalledVersionText { get; }

    public string LatestVersionText { get; }

    public string WarningText { get; }

    public string DetailText { get; }

    public string ExecutablePath { get; }

    public string ReleaseUrl { get; }

    public string PrimaryActionText { get; }

    public bool IsPrimaryActionVisible { get; }

    public bool IsPrimaryActionBaseEnabled { get; }

    public bool IsUninstallVisible { get; }

    public bool IsUninstallBaseEnabled { get; }

    public bool IsManualSelectVisible { get; }

    public bool IsManualPinned { get; }

    public Visibility ReadyBadgeVisibility => State == ReadinessState.Ready ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WarningBadgeVisibility => State is ReadinessState.Partial or ReadinessState.Unknown ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BlockedBadgeVisibility => State is ReadinessState.Missing or ReadinessState.Misconfigured ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InstalledVersionVisibility => string.IsNullOrWhiteSpace(InstalledVersionText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LatestVersionVisibility => string.IsNullOrWhiteSpace(LatestVersionText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility WarningVisibility => string.IsNullOrWhiteSpace(WarningText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DetailVisibility => string.IsNullOrWhiteSpace(DetailText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PathVisibility => string.IsNullOrWhiteSpace(ExecutablePath) ? Visibility.Collapsed : Visibility.Visible;

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
