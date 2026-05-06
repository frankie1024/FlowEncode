using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FlowEncode.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FlowEncode;

internal static class WindowInteractionHelper
{
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

    public static async Task<string?> PickFolderPathAsync(nint windowHandle)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };

        picker.FileTypeFilter.Add("*");
        try
        {
            InitializeWithWindow.Initialize(picker, windowHandle);
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex)
        {
            TryWriteDiagnostic($"Failed to pick folder path. {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static string? PickFilteredFilePath(
        nint windowHandle,
        string dialogTitle,
        string currentPath,
        string primaryFilterLabel,
        string primaryFilterPattern,
        string allFilesFilterLabel)
    {
        var initialDirectory = ResolveInitialFileDialogDirectory(currentPath);
        var result = NativeFileDialogHelper.ShowOpenFileDialog(
            windowHandle,
            dialogTitle,
            initialDirectory,
            new NativeFileDialogHelper.FileDialogFilter(primaryFilterLabel, primaryFilterPattern),
            new NativeFileDialogHelper.FileDialogFilter(allFilesFilterLabel, "*.*"));
        return result?.Path;
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

    private static string ResolveInitialFileDialogDirectory(string? currentPath)
    {
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            try
            {
                if (Directory.Exists(currentPath))
                {
                    return currentPath;
                }

                var directory = Path.GetDirectoryName(currentPath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    return directory;
                }
            }
            catch (ArgumentException ex)
            {
                TryWriteDiagnostic($"Invalid file dialog path '{currentPath}'. {ex.GetType().Name}: {ex.Message}");
            }
            catch (NotSupportedException ex)
            {
                TryWriteDiagnostic($"Unsupported file dialog path '{currentPath}'. {ex.GetType().Name}: {ex.Message}");
            }
            catch (PathTooLongException ex)
            {
                TryWriteDiagnostic($"Overlong file dialog path '{currentPath}'. {ex.GetType().Name}: {ex.Message}");
            }
        }

        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documentsPath) ? Environment.CurrentDirectory : documentsPath;
    }

    private static void TryWriteDiagnostic(string message)
    {
        try
        {
            var paths = App.GetService<LocalAppPaths>();
            AppDiagnosticsLog.Write(paths, nameof(WindowInteractionHelper), message);
        }
        catch
        {
        }
    }
}
