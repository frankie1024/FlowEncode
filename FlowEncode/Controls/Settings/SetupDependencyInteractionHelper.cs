using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Controls.Shared;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowEncode.Controls.Settings;

internal static class SetupDependencyInteractionHelper
{
    public static async Task RunGuardedAsync(
        ISetupDependencyModuleViewModel viewModel,
        FrameworkElement owner,
        string diagnosticSource,
        string diagnosticAction,
        string errorTitle,
        Func<Task> action)
    {
        await UiActionGuard.RunAsync(
            owner,
            diagnosticSource,
            diagnosticAction,
            errorTitle,
            viewModel.Texts.OkButton,
            action);
    }

    public static async Task InstallSetupDependencyAsync(
        ISetupDependencyModuleViewModel viewModel,
        FrameworkElement owner,
        SetupDependencyKind kind,
        string diagnosticSource)
    {
        try
        {
            string? error;
            if (viewModel.RequiresSetupDependencyManualImport(kind))
            {
                var filePath = PickExecutableFilePath(viewModel, kind);
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    return;
                }

                error = await viewModel.ImportSetupDependencyBinaryAsync(kind, filePath);
            }
            else
            {
                if (viewModel.HasManualPinnedSetupDependency(kind))
                {
                    var dependencyLabel = viewModel.GetSetupDependencyDisplayName(kind);
                    var confirmed = await ShowConfirmationAsync(
                        viewModel,
                        owner,
                        viewModel.Texts.ManualToolUpdateOverrideTitle,
                        viewModel.Texts.ManualToolUpdateOverrideMessage(dependencyLabel),
                        viewModel.Texts.UpdateButton,
                        viewModel.Texts.CancelButton,
                        ContentDialogButton.Close);
                    if (!confirmed)
                    {
                        return;
                    }

                    error = await viewModel.ClearManualPinnedSetupDependencyAsync(kind, refreshAfterClear: false);
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        await ShowMessageAsync(viewModel, owner, viewModel.Texts.ErrorInstallFailedTitle, error);
                        return;
                    }
                }

                error = await viewModel.InstallSetupDependencyAsync(kind);
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                await ShowMessageAsync(viewModel, owner, viewModel.Texts.ErrorInstallFailedTitle, error);
            }
        }
        catch (Exception ex)
        {
            TryWriteDiagnostic(diagnosticSource, $"Failed to install setup dependency '{kind}'. {ex.GetType().Name}: {ex.Message}");
            await ShowMessageAsync(viewModel, owner, viewModel.Texts.ErrorInstallFailedTitle, ex.Message);
        }
    }

    public static async Task ManualSelectSetupDependencyAsync(
        ISetupDependencyModuleViewModel viewModel,
        FrameworkElement owner,
        SetupDependencyKind kind,
        string diagnosticSource)
    {
        try
        {
            var filePath = PickExecutableFilePath(viewModel, kind);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            var error = await viewModel.PinSetupDependencyBinaryAsync(kind, filePath);
            if (!string.IsNullOrWhiteSpace(error))
            {
                await ShowMessageAsync(viewModel, owner, viewModel.Texts.ErrorSaveSettingsFailedTitle, error);
            }
        }
        catch (Exception ex)
        {
            TryWriteDiagnostic(diagnosticSource, $"Failed to manually pin setup dependency '{kind}'. {ex.GetType().Name}: {ex.Message}");
            await ShowMessageAsync(viewModel, owner, viewModel.Texts.ErrorSaveSettingsFailedTitle, ex.Message);
        }
    }

    public static async Task ClearManualPinnedSetupDependencyAsync(
        ISetupDependencyModuleViewModel viewModel,
        FrameworkElement owner,
        SetupDependencyKind kind,
        string diagnosticSource = nameof(SetupDependencyInteractionHelper))
    {
        await RunGuardedAsync(
            viewModel,
            owner,
            diagnosticSource,
            $"Failed to clear manually pinned setup dependency '{kind}'",
            viewModel.Texts.ErrorSaveSettingsFailedTitle,
            async () =>
            {
                var error = await viewModel.ClearManualPinnedSetupDependencyAsync(kind);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    await ShowMessageAsync(viewModel, owner, viewModel.Texts.ErrorSaveSettingsFailedTitle, error);
                }
            });
    }

    public static async Task UninstallSetupDependencyAsync(
        ISetupDependencyModuleViewModel viewModel,
        FrameworkElement owner,
        SetupDependencyKind kind,
        string diagnosticSource = nameof(SetupDependencyInteractionHelper))
    {
        await RunGuardedAsync(
            viewModel,
            owner,
            diagnosticSource,
            $"Failed to uninstall setup dependency '{kind}'",
            viewModel.Texts.ErrorUninstallFailedTitle,
            async () =>
            {
                var error = await viewModel.UninstallSetupDependencyAsync(kind);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    await ShowMessageAsync(viewModel, owner, viewModel.Texts.ErrorUninstallFailedTitle, error);
                }
            });
    }

    public static Task<bool> ShowConfirmationAsync(
        ISetupDependencyModuleViewModel viewModel,
        FrameworkElement owner,
        string title,
        string message,
        string primaryButtonText,
        string closeButtonText,
        ContentDialogButton defaultButton = ContentDialogButton.Primary)
    {
        return WindowInteractionHelper.ShowConfirmationAsync(
            owner.XamlRoot,
            owner.ActualTheme,
            title,
            message,
            primaryButtonText,
            closeButtonText,
            defaultButton);
    }

    public static Task ShowMessageAsync(
        ISetupDependencyModuleViewModel viewModel,
        FrameworkElement owner,
        string title,
        string message)
    {
        return WindowInteractionHelper.ShowMessageAsync(
            owner.XamlRoot,
            owner.ActualTheme,
            viewModel.Texts.OkButton,
            title,
            message);
    }

    public static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            if (process is null)
            {
                TryWriteDiagnostic(nameof(SetupDependencyInteractionHelper), $"Explorer process did not start for path '{path}'.");
            }
        }
        catch (Exception ex)
        {
            TryWriteDiagnostic(nameof(SetupDependencyInteractionHelper), $"Failed to open path '{path}'. {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        if (process is null)
        {
            TryWriteDiagnostic(nameof(SetupDependencyInteractionHelper), $"URL process did not start for '{url}'.");
        }
    }

    public static void TryWriteDiagnostic(string source, string message)
    {
        try
        {
            App.GetService<IAppDiagnostics>().Write(source, message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write setup dependency diagnostic. {ex}");
        }
    }

    private static string? PickExecutableFilePath(ISetupDependencyModuleViewModel viewModel, SetupDependencyKind kind)
    {
        try
        {
            return WindowInteractionHelper.PickOpenFilePath(
                WindowInteractionHelper.GetMainWindowHandle(),
                viewModel.GetSetupDependencyDisplayName(kind),
                viewModel.GetSetupDependencyCurrentPath(kind),
                false,
                new NativeFileDialogHelper.FileDialogFilter(viewModel.Texts.AllFilesTypeDescription, "*.exe"));
        }
        catch (Exception ex)
        {
            TryWriteDiagnostic(nameof(SetupDependencyInteractionHelper), $"Failed to pick executable file. {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
