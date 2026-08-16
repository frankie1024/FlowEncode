using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace FlowEncode;

internal static class WindowInteractionHelper
{
    private static readonly string[] CommonDialogDirectoryCandidates =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
    ];

    public static async Task<ContentDialogResult> ShowContentDialogAsync(ContentDialog dialog, string diagnosticSource)
    {
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            TryWriteDiagnostic($"{diagnosticSource}: failed to show content dialog. {ex.GetType().Name}: {ex.Message}");
            return ContentDialogResult.None;
        }
    }

    public static async Task ApplyPickedPathAsync(TextBox textBox, string path, Action<string> applyPath)
    {
        textBox.Text = path;
        await Task.Yield();
        applyPath(path);
    }

    public static string? PickFolderPath(
        nint windowHandle,
        string dialogTitle,
        string currentPath)
    {
        return PickFolderPath(windowHandle, dialogTitle, currentPath, useSharedRecentDirectory: true);
    }

    public static string? PickFolderPath(
        nint windowHandle,
        string dialogTitle,
        string currentPath,
        bool useSharedRecentDirectory)
    {
        try
        {
            var initialDirectory = ResolveInitialFileDialogDirectory(currentPath, useSharedRecentDirectory);
            var selectedPath = NativeFileDialogHelper.ShowFolderDialog(
                windowHandle,
                dialogTitle,
                initialDirectory);
            if (useSharedRecentDirectory)
            {
                RememberLastFileDialogDirectory(selectedPath);
            }

            return selectedPath;
        }
        catch (Exception ex)
        {
            TryWriteDiagnostic($"Failed to pick folder path. {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static string? PickOpenFilePath(
        nint windowHandle,
        string dialogTitle,
        string currentPath,
        params NativeFileDialogHelper.FileDialogFilter[] filters)
    {
        return PickOpenFilePath(windowHandle, dialogTitle, currentPath, useSharedRecentDirectory: true, filters);
    }

    public static string? PickOpenFilePath(
        nint windowHandle,
        string dialogTitle,
        string currentPath,
        bool useSharedRecentDirectory,
        params NativeFileDialogHelper.FileDialogFilter[] filters)
    {
        var initialDirectory = ResolveInitialFileDialogDirectory(currentPath, useSharedRecentDirectory);
        var result = NativeFileDialogHelper.ShowOpenFileDialog(
            windowHandle,
            dialogTitle,
            initialDirectory,
            filters);
        if (useSharedRecentDirectory)
        {
            RememberLastFileDialogDirectory(result?.Path);
        }

        return result?.Path;
    }

    public static NativeFileDialogHelper.FileDialogResult? PickSaveFilePath(
        nint windowHandle,
        string dialogTitle,
        string currentPath,
        string defaultFileName,
        string defaultExtension,
        params NativeFileDialogHelper.FileDialogFilter[] filters)
    {
        return PickSaveFilePath(
            windowHandle,
            dialogTitle,
            currentPath,
            defaultFileName,
            defaultExtension,
            useSharedRecentDirectory: true,
            filters);
    }

    public static NativeFileDialogHelper.FileDialogResult? PickSaveFilePath(
        nint windowHandle,
        string dialogTitle,
        string currentPath,
        string defaultFileName,
        string defaultExtension,
        bool useSharedRecentDirectory,
        params NativeFileDialogHelper.FileDialogFilter[] filters)
    {
        var initialDirectory = ResolveInitialFileDialogDirectory(currentPath, useSharedRecentDirectory);
        var result = NativeFileDialogHelper.ShowSaveFileDialog(
            windowHandle,
            dialogTitle,
            initialDirectory,
            defaultFileName,
            defaultExtension,
            filters);
        if (useSharedRecentDirectory && result is { } fileResult)
        {
            RememberLastFileDialogDirectory(fileResult.Path);
        }

        return result;
    }

    public static string? PickFilteredFilePath(
        nint windowHandle,
        string dialogTitle,
        string currentPath,
        string primaryFilterLabel,
        string primaryFilterPattern,
        string allFilesFilterLabel)
    {
        return PickOpenFilePath(
            windowHandle,
            dialogTitle,
            currentPath,
            new NativeFileDialogHelper.FileDialogFilter(primaryFilterLabel, primaryFilterPattern),
            new NativeFileDialogHelper.FileDialogFilter(allFilesFilterLabel, "*.*"));
    }

    public static async Task<bool> ShowConfirmationAsync(
        XamlRoot? xamlRoot,
        ElementTheme requestedTheme,
        string title,
        string message,
        string primaryButtonText,
        string closeButtonText,
        ContentDialogButton defaultButton = ContentDialogButton.Primary)
    {
        if (xamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = defaultButton,
            XamlRoot = xamlRoot,
            RequestedTheme = requestedTheme
        };

        return await ShowContentDialogAsync(dialog, nameof(WindowInteractionHelper)) == ContentDialogResult.Primary;
    }

    public static async Task ShowMessageAsync(
        XamlRoot? xamlRoot,
        ElementTheme requestedTheme,
        string closeButtonText,
        string title,
        string message)
    {
        if (xamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = closeButtonText,
            XamlRoot = xamlRoot,
            RequestedTheme = requestedTheme
        };

        await ShowContentDialogAsync(dialog, nameof(WindowInteractionHelper));
    }

    public static nint GetMainWindowHandle()
    {
        return WindowNative.GetWindowHandle(App.GetService<MainWindow>());
    }

    private static string ResolveInitialFileDialogDirectory(string? currentPath, bool useSharedRecentDirectory)
    {
        var currentDirectory = ResolveExistingDirectoryOrParent(currentPath, writeDiagnostics: true);
        if (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            return currentDirectory;
        }

        if (useSharedRecentDirectory)
        {
            var lastDialogDirectory = ResolveExistingDirectoryOrParent(LoadLastFileDialogDirectory(), writeDiagnostics: false);
            if (!string.IsNullOrWhiteSpace(lastDialogDirectory))
            {
                return lastDialogDirectory;
            }
        }

        var workspaceDirectory = ResolveExistingDirectoryOrParent(GetCurrentWorkspaceRootPath(), writeDiagnostics: false);
        if (!string.IsNullOrWhiteSpace(workspaceDirectory))
        {
            return workspaceDirectory;
        }

        try
        {
            foreach (var candidate in CommonDialogDirectoryCandidates)
            {
                var resolved = ResolveExistingDirectoryOrParent(candidate, writeDiagnostics: false);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }
        }
        catch (Exception ex)
        {
            TryWriteDiagnostic($"Failed to resolve common file dialog directory. {ex.GetType().Name}: {ex.Message}");
        }

        return string.IsNullOrWhiteSpace(Environment.CurrentDirectory)
            ? AppContext.BaseDirectory
            : Environment.CurrentDirectory;
    }

    private static string? GetCurrentWorkspaceRootPath()
    {
        try
        {
            return App.GetService<LocalAppPaths>().RootPath;
        }
        catch (Exception ex)
        {
            TryWriteDiagnostic($"Failed to resolve current workspace root path. {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string? LoadLastFileDialogDirectory()
    {
        try
        {
            return App.GetService<IAppSettingsService>().Load().LastFileDialogDirectory;
        }
        catch (Exception ex)
        {
            TryWriteDiagnostic($"Failed to load last file dialog directory. {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void RememberLastFileDialogDirectory(string? selectedPath)
    {
        var directory = ResolveExistingDirectoryOrParent(selectedPath, writeDiagnostics: false);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            var normalizedDirectory = Path.GetFullPath(directory);
            var settingsService = App.GetService<IAppSettingsService>();
            var settings = settingsService.Load();
            if (string.Equals(settings.LastFileDialogDirectory, normalizedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            settingsService.Save(settings with { LastFileDialogDirectory = normalizedDirectory });
        }
        catch (Exception ex)
        {
            TryWriteDiagnostic($"Failed to remember last file dialog directory. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? ResolveExistingDirectoryOrParent(string? candidatePath, bool writeDiagnostics)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(candidatePath);
            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }

            var directory = Path.GetDirectoryName(fullPath);
            return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
                ? directory
                : null;
        }
        catch (ArgumentException ex) when (writeDiagnostics)
        {
            TryWriteDiagnostic($"Invalid file dialog path '{candidatePath}'. {ex.GetType().Name}: {ex.Message}");
        }
        catch (NotSupportedException ex) when (writeDiagnostics)
        {
            TryWriteDiagnostic($"Unsupported file dialog path '{candidatePath}'. {ex.GetType().Name}: {ex.Message}");
        }
        catch (PathTooLongException ex) when (writeDiagnostics)
        {
            TryWriteDiagnostic($"Overlong file dialog path '{candidatePath}'. {ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex) when (writeDiagnostics)
        {
            TryWriteDiagnostic($"Failed to resolve file dialog path '{candidatePath}'. {ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to resolve file dialog path without diagnostics. {ex}");
        }

        return null;
    }

    private static void TryWriteDiagnostic(string message)
    {
        try
        {
            App.GetService<IAppDiagnostics>().Write(nameof(WindowInteractionHelper), message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write window interaction diagnostic. {ex}");
        }
    }
}
