using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FlowEncode.Infrastructure.Automation;

namespace FlowEncode;

/// <summary>
/// Window-level operations for the automation channel: they need the window's settings
/// persistence (theme, workspace refresh) or its lifetime (closing for an update).
/// </summary>
public sealed partial class MainWindow
{
    internal bool IsAutomationReady => _hasCompletedInitialization && !_isShutdownConfirmed;

    internal async Task<AutomationSettings> UpdateSettingsForAutomationAsync(AutomationSettingsPatch patch)
    {
        bool preparedWorkspaceChange;
        try
        {
            preparedWorkspaceChange = await ViewModel.ApplyAutomationSettingsPatchAsync(patch);
        }
        catch (DesktopAutomationException)
        {
            ViewModel.CancelPreparedWorkspaceRootChange();
            throw;
        }

        var error = await PersistSettingsCoreAsync(refreshTemplateLibrary: preparedWorkspaceChange);
        if (error is not null)
        {
            if (preparedWorkspaceChange)
            {
                ViewModel.CancelPreparedWorkspaceRootChange();
            }

            throw new DesktopAutomationException(error.Length > 0
                ? error
                : "Settings cannot be saved right now; another settings change is in progress. Try again shortly.");
        }

        return ViewModel.GetAutomationSettings();
    }

    internal async Task<AppUpdateCheckResultView> CheckAppUpdateForAutomationAsync()
    {
        var result = await ViewModel.RefreshAvailableUpdatesAsync(reportStatus: true);
        return result is null
            ? new AppUpdateCheckResultView(string.Empty, null, false, null, ViewModel.AppUpdateStatusText)
            : new AppUpdateCheckResultView(
                result.CurrentVersion,
                string.IsNullOrWhiteSpace(result.LatestVersion) ? null : result.LatestVersion,
                result.UpdateAvailable,
                string.IsNullOrWhiteSpace(result.ReleaseUrl) ? null : result.ReleaseUrl,
                null);
    }

    /// <summary>
    /// Same flow as the settings page's update button, minus the confirmation dialogs: the caller
    /// asked for the install, and it is refused while any task is still running.
    /// </summary>
    internal async Task<string> InstallAppUpdateForAutomationAsync()
    {
        var check = await ViewModel.RefreshAvailableUpdatesAsync(reportStatus: true)
            ?? throw new DesktopAutomationException(ViewModel.AppUpdateStatusText);
        if (!check.UpdateAvailable)
        {
            return $"FlowEncode {check.CurrentVersion} is already the latest version.";
        }

        if (!check.CanDownloadInstaller)
        {
            throw new DesktopAutomationException(
                $"FlowEncode {check.LatestVersion} has no verified installer asset; download it from {check.ReleaseUrl}.");
        }

        ThrowIfAppWorkRunning();
        var installerPath = await ViewModel.DownloadLatestAppInstallerAsync();
        if (string.IsNullOrWhiteSpace(installerPath))
        {
            throw new DesktopAutomationException(ViewModel.AppUpdateStatusText);
        }

        ThrowIfAppWorkRunning();
        using (var process = Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath)
        }))
        {
            if (process is null)
            {
                throw new DesktopAutomationException($"The installer did not start: {installerPath}");
            }
        }

        DispatcherQueue.TryEnqueue(Close);
        return $"Started the FlowEncode {check.LatestVersion} installer ({installerPath}); the app is closing.";
    }

    private void ThrowIfAppWorkRunning()
    {
        if (ViewModel.HasActiveAppWork())
        {
            throw new DesktopAutomationException(ViewModel.Texts.AppUpdateInstallRequiresIdleMessage);
        }
    }
}
