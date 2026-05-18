using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Controls.Shared;
using FlowEncode.Domain;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FlowEncode.Controls.Templates;

public sealed partial class TemplatesView : UserControl
{
    private const string TemplateExchangeFileExtension = ".profile";
    private bool _selectionSyncInProgress;

    internal ITemplatesViewHost? Host { get; set; }

    private TemplatesViewModel? ViewModel => DataContext as TemplatesViewModel;
    private TemplateLibraryViewModel? LibraryViewModel => ViewModel?.Library;
    private TemplateEditorViewModel? EditorViewModel => ViewModel?.Editor;

    public TemplatesView()
    {
        InitializeComponent();
    }

    public void ApplyLayout(bool stackedWorkspace, bool compactForms, Thickness contentPadding)
    {
        ContentStack.Padding = contentPadding;
        TemplatesWorkspaceGrid.ColumnSpacing = stackedWorkspace ? 0 : UiTokens.SpacingL;
        TemplatesPrimaryColumn.Width = new GridLength(stackedWorkspace ? 1 : 0.94, GridUnitType.Star);
        TemplatesSecondaryColumn.Width = stackedWorkspace
            ? new GridLength(0)
            : new GridLength(1.06, GridUnitType.Star);

        Grid.SetRow(TemplateEditorPanel, stackedWorkspace ? 1 : 0);
        Grid.SetColumn(TemplateEditorPanel, stackedWorkspace ? 0 : 1);
        Grid.SetColumnSpan(TemplateEditorPanel, stackedWorkspace ? 2 : 1);

        ConfigureTwoItemGrid(TemplateEditorHeaderGrid, TemplateEditorActionsColumn, TemplateEditorCommandBar, compactForms, GridLength.Auto);
        ConfigureTwoItemGrid(TemplateEncoderFormatGrid, TemplateOutputColumn, TemplateOutputComboBox, compactForms, new GridLength(1, GridUnitType.Star));
        ConfigureThreeItemGrid(TemplateRatePresetTuneGrid, TemplatePresetColumn, TemplateTuneColumn, TemplatePresetComboBox, TemplateTuneComboBox, compactForms);
        ConfigureTwoItemGrid(TemplateProfileQualityGrid, TemplateQualityGroupColumn, TemplateQualityBitrateGrid, compactForms, new GridLength(1, GridUnitType.Star));
        ConfigureTwoItemGrid(TemplateQualityBitrateGrid, TemplateBitrateColumn, TemplateBitrateContainer, compactForms, new GridLength(1, GridUnitType.Star));
    }

    public void InitializeSelectionIfLoaded()
    {
        if (TemplateLibraryList.SelectedItem is null)
        {
            RestoreCurrentTemplateSelection();
        }

        if (TemplateLibraryList.Items.Count > 0 && TemplateLibraryList.SelectedIndex < 0)
        {
            TemplateLibraryList.SelectedIndex = 0;
        }
    }

    public void SetTemplateLibrarySelection(TemplateLibraryItemViewModel? templateItem)
    {
        RunWithTemplateSelectionSync(() => TemplateLibraryList.SelectedItem = templateItem);
    }

    public void RestoreCurrentTemplateSelection()
    {
        var libraryViewModel = LibraryViewModel;
        if (libraryViewModel is null)
        {
            return;
        }

        var selectedItem = string.IsNullOrWhiteSpace(libraryViewModel.CurrentTemplateSelectionKey)
            ? null
            : libraryViewModel.TemplateLibraryItems.FirstOrDefault(item =>
                string.Equals(item.Key, libraryViewModel.CurrentTemplateSelectionKey, StringComparison.Ordinal));

        RunWithTemplateSelectionSync(() => TemplateLibraryList.SelectedItem = selectedItem);
        Host?.SetOverviewTemplateSelection(selectedItem);
        Host?.SetSavedTemplateQuickSelection(selectedItem?.UserTemplate);
    }

    private async void TemplateLibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await RunGuardedAsync(
            nameof(TemplateLibraryList_SelectionChanged),
            async () =>
            {
                if (_selectionSyncInProgress || LibraryViewModel is null)
                {
                    return;
                }

                if (TemplateLibraryList.SelectedItem is not TemplateLibraryItemViewModel templateItem)
                {
                    return;
                }

                await SelectTemplateItemAsync(templateItem);
            });
    }

    private async void SaveTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(
            nameof(SaveTemplateButton_Click),
            async () =>
            {
                var editorViewModel = EditorViewModel;
                if (editorViewModel is null)
                {
                    return;
                }

                try
                {
                    await SaveCurrentTemplateAsync();
                }
                catch (Exception ex)
                {
                    await ShowMessageAsync(editorViewModel.Texts.ErrorSaveFailedTitle, ex.Message);
                }
            },
            EditorViewModel?.Texts.ErrorSaveFailedTitle);
    }

    private async void NewTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(
            nameof(NewTemplateButton_Click),
            async () =>
            {
                var editorViewModel = EditorViewModel;
                if (editorViewModel is null)
                {
                    return;
                }

                if (!await ConfirmTemplateChangeAsync())
                {
                    RestoreCurrentTemplateSelection();
                    return;
                }

                RunWithTemplateSelectionSync(() => TemplateLibraryList.SelectedItem = null);
                Host?.SetSavedTemplateQuickSelection(null);
                editorViewModel.BeginNewTemplateDraft();
            });
    }

    private async void ImportTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(
            nameof(ImportTemplateButton_Click),
            async () =>
            {
                var libraryViewModel = LibraryViewModel;
                if (libraryViewModel is null)
                {
                    return;
                }

                if (!await ConfirmTemplateChangeAsync())
                {
                    RestoreCurrentTemplateSelection();
                    return;
                }

                try
                {
                    var filePath = PickTemplateImportFilePath();
                    if (string.IsNullOrWhiteSpace(filePath))
                    {
                        return;
                    }

                    var importedTemplate = await libraryViewModel.ReadTemplateAsync(filePath);
                    var existingTemplate = libraryViewModel.FindUserTemplateByName(importedTemplate.Name);
                    if (existingTemplate?.IsPinned == true)
                    {
                        await ShowMessageAsync(libraryViewModel.Texts.ErrorImportFailedTitle, libraryViewModel.Texts.PinnedTemplateLockedMessage);
                        RestoreCurrentTemplateSelection();
                        return;
                    }

                    if (existingTemplate is not null)
                    {
                        var overwriteConfirmed = await ShowConfirmationAsync(
                            libraryViewModel.Texts.OverwriteTemplateTitle,
                            libraryViewModel.Texts.OverwriteTemplateMessage(importedTemplate.Name),
                            libraryViewModel.Texts.OverwriteButton,
                            libraryViewModel.Texts.CancelButton);

                        if (!overwriteConfirmed)
                        {
                            RestoreCurrentTemplateSelection();
                            return;
                        }
                    }

                    var savedTemplate = await libraryViewModel.ImportTemplateAsync(importedTemplate, existingTemplate?.Id);
                    var templateItem = libraryViewModel.TemplateLibraryItems.FirstOrDefault(item =>
                        string.Equals(item.TemplateId, savedTemplate.Id, StringComparison.OrdinalIgnoreCase));
                    SyncTemplateSelectors(templateItem);
                }
                catch (Exception ex)
                {
                    TryWriteDiagnostic($"Failed to import template. {ex.GetType().Name}: {ex.Message}");
                    await ShowMessageAsync(libraryViewModel.Texts.ErrorImportFailedTitle, ex.Message);
                }
            },
            LibraryViewModel?.Texts.ErrorImportFailedTitle);
    }

    private async void ExportTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(
            nameof(ExportTemplateButton_Click),
            async () =>
            {
                var editorViewModel = EditorViewModel;
                if (editorViewModel is null)
                {
                    return;
                }

                try
                {
                    var filePath = PickTemplateExportFilePath();
                    if (string.IsNullOrWhiteSpace(filePath))
                    {
                        return;
                    }

                    await editorViewModel.ExportCurrentTemplateAsync(filePath);
                }
                catch (Exception ex)
                {
                    TryWriteDiagnostic($"Failed to export template. {ex.GetType().Name}: {ex.Message}");
                    await ShowMessageAsync(editorViewModel.Texts.ErrorExportFailedTitle, ex.Message);
                }
            },
            EditorViewModel?.Texts.ErrorExportFailedTitle);
    }

    private async void DeleteTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(
            nameof(DeleteTemplateButton_Click),
            async () =>
            {
                var libraryViewModel = LibraryViewModel;
                if (libraryViewModel is null
                    || !TryGetTemplateItem(sender, out var templateItem)
                    || templateItem.UserTemplate is not { } template)
                {
                    return;
                }

                try
                {
                    if (template.IsPinned)
                    {
                        await ShowMessageAsync(libraryViewModel.Texts.ErrorDeleteFailedTitle, libraryViewModel.Texts.PinnedTemplateLockedMessage);
                        return;
                    }

                    var confirmed = await ShowConfirmationAsync(
                        libraryViewModel.Texts.ConfirmDeleteTemplateTitle,
                        libraryViewModel.Texts.ConfirmDeleteTemplateMessage(template.Name),
                        libraryViewModel.Texts.DeleteTemplateButton,
                        libraryViewModel.Texts.CancelButton);
                    if (!confirmed)
                    {
                        return;
                    }

                    var deletedCurrentTemplate = string.Equals(
                        libraryViewModel.CurrentTemplateSelectionKey,
                        templateItem.Key,
                        StringComparison.Ordinal);
                    await libraryViewModel.DeleteTemplateAsync(template.Id);

                    if (deletedCurrentTemplate)
                    {
                        SyncTemplateSelectors(null);
                    }
                    else
                    {
                        RestoreCurrentTemplateSelection();
                    }
                }
                catch (Exception ex)
                {
                    TryWriteDiagnostic($"Failed to delete template '{template.Name}'. {ex.GetType().Name}: {ex.Message}");
                    await ShowMessageAsync(libraryViewModel.Texts.ErrorDeleteFailedTitle, ex.Message);
                }
            },
            LibraryViewModel?.Texts.ErrorDeleteFailedTitle);
    }

    private async void PinTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(
            nameof(PinTemplateButton_Click),
            async () =>
            {
                var libraryViewModel = LibraryViewModel;
                if (libraryViewModel is null || !TryGetTemplateItem(sender, out var templateItem))
                {
                    return;
                }

                try
                {
                    var currentTemplate = templateItem.UserTemplate
                        ?? libraryViewModel.FindUserTemplateById(templateItem.TemplateId)
                        ?? throw new InvalidOperationException(libraryViewModel.Texts.TemplateMissingMessage);
                    var updatedTemplate = await libraryViewModel.SetTemplatePinnedAsync(currentTemplate.Id, !currentTemplate.IsPinned);
                    var updatedTemplateItem = libraryViewModel.TemplateLibraryItems.FirstOrDefault(item =>
                        string.Equals(item.TemplateId, updatedTemplate.Id, StringComparison.OrdinalIgnoreCase));
                    SyncTemplateSelectors(updatedTemplateItem);
                }
                catch (Exception ex)
                {
                    TryWriteDiagnostic($"Failed to toggle pinned state for template '{templateItem.TemplateId}'. {ex.GetType().Name}: {ex.Message}");
                    await ShowMessageAsync(libraryViewModel.Texts.ErrorPinFailedTitle, ex.Message);
                }
            },
            LibraryViewModel?.Texts.ErrorPinFailedTitle);
    }

    public async Task<SavedTemplate?> SaveCurrentTemplateAsync()
    {
        var libraryViewModel = LibraryViewModel;
        var editorViewModel = EditorViewModel;
        if (libraryViewModel is null || editorViewModel is null)
        {
            return null;
        }

        var normalizedTemplateName = editorViewModel.DraftTemplateName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedTemplateName))
        {
            await ShowMessageAsync(editorViewModel.Texts.ErrorCannotSaveTemplateTitle, editorViewModel.Texts.EmptyTemplateNameMessage);
            return null;
        }

        var existingTemplate = libraryViewModel.FindUserTemplateByName(normalizedTemplateName);
        if (existingTemplate?.IsPinned == true
            && !string.Equals(existingTemplate.Id, editorViewModel.EditingUserTemplateId, StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessageAsync(editorViewModel.Texts.ErrorCannotSaveTemplateTitle, editorViewModel.Texts.PinnedTemplateLockedMessage);
            return null;
        }

        if (existingTemplate is not null
            && !string.Equals(existingTemplate.Id, editorViewModel.EditingUserTemplateId, StringComparison.OrdinalIgnoreCase))
        {
            var overwriteConfirmed = await ShowConfirmationAsync(
                editorViewModel.Texts.OverwriteTemplateTitle,
                editorViewModel.Texts.OverwriteTemplateMessage(normalizedTemplateName),
                editorViewModel.Texts.OverwriteButton,
                editorViewModel.Texts.CancelButton);

            if (!overwriteConfirmed)
            {
                return null;
            }
        }

        var savedTemplate = await editorViewModel.SaveCurrentTemplateAsync();
        if (savedTemplate is null)
        {
            await ShowMessageAsync(editorViewModel.Texts.ErrorCannotSaveTemplateTitle, editorViewModel.Texts.IncompleteTemplateMessage);
            return null;
        }

        var selectedTemplateItem = libraryViewModel.TemplateLibraryItems.FirstOrDefault(item =>
            string.Equals(item.TemplateId, savedTemplate.Id, StringComparison.OrdinalIgnoreCase));
        SyncTemplateSelectors(selectedTemplateItem);
        return savedTemplate;
    }

    private async Task<bool> ConfirmTemplateChangeAsync()
    {
        var editorViewModel = EditorViewModel;
        if (editorViewModel is null || !editorViewModel.HasUnsavedTemplateChanges)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            Title = editorViewModel.Texts.UnsavedTemplateChangesTitle,
            Content = editorViewModel.Texts.UnsavedTemplateChangesMessage,
            PrimaryButtonText = editorViewModel.Texts.SaveButton,
            SecondaryButtonText = editorViewModel.Texts.DontSaveButton,
            CloseButtonText = editorViewModel.Texts.CancelButton,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };

        var result = await WindowInteractionHelper.ShowContentDialogAsync(dialog, nameof(TemplatesView));
        return result switch
        {
            ContentDialogResult.Primary => await SaveCurrentTemplateAsync() is not null,
            ContentDialogResult.Secondary => true,
            _ => false
        };
    }

    private async Task SelectTemplateItemAsync(TemplateLibraryItemViewModel templateItem)
    {
        var libraryViewModel = LibraryViewModel;
        if (libraryViewModel is null)
        {
            return;
        }

        if (string.Equals(templateItem.Key, libraryViewModel.CurrentTemplateSelectionKey, StringComparison.Ordinal))
        {
            SyncTemplateSelectors(templateItem);
            return;
        }

        if (!await ConfirmTemplateChangeAsync())
        {
            RestoreCurrentTemplateSelection();
            return;
        }

        SyncTemplateSelectors(templateItem);

        if (templateItem.UserTemplate is not null)
        {
            await libraryViewModel.SelectUserTemplateAsync(templateItem.UserTemplate);
        }
    }

    private void SyncTemplateSelectors(TemplateLibraryItemViewModel? templateItem)
    {
        RunWithTemplateSelectionSync(() => TemplateLibraryList.SelectedItem = templateItem);
        Host?.SetOverviewTemplateSelection(templateItem);
        Host?.SetSavedTemplateQuickSelection(templateItem?.UserTemplate);
    }

    private void RunWithTemplateSelectionSync(Action action)
    {
        _selectionSyncInProgress = true;

        try
        {
            action();
        }
        finally
        {
            _selectionSyncInProgress = false;
        }
    }

    private string? PickTemplateImportFilePath()
    {
        var libraryViewModel = LibraryViewModel;
        if (libraryViewModel is null)
        {
            return null;
        }

        var initialDirectory = EnsureTemplateFilesRootPath();
        return WindowInteractionHelper.PickOpenFilePath(
            WindowInteractionHelper.GetMainWindowHandle(),
            libraryViewModel.Texts.LoadTemplateDialogTitle,
            initialDirectory,
            false,
            new NativeFileDialogHelper.FileDialogFilter(
                libraryViewModel.Texts.TemplateFileTypeDescription,
                $"*{TemplateExchangeFileExtension}"));
    }

    private string? PickTemplateExportFilePath()
    {
        var editorViewModel = EditorViewModel;
        if (editorViewModel is null)
        {
            return null;
        }

        var initialDirectory = EnsureTemplateFilesRootPath();
        var result = WindowInteractionHelper.PickSaveFilePath(
            WindowInteractionHelper.GetMainWindowHandle(),
            editorViewModel.Texts.ExportButton,
            initialDirectory,
            BuildTemplateExportFileName(),
            TemplateExchangeFileExtension,
            false,
            new NativeFileDialogHelper.FileDialogFilter(
                editorViewModel.Texts.TemplateFileTypeDescription,
                $"*{TemplateExchangeFileExtension}"));
        return result is null ? null : Path.ChangeExtension(result.Value.Path, TemplateExchangeFileExtension);
    }

    private string BuildTemplateExportFileName()
    {
        var preferredName = EditorViewModel?.DraftTemplateName?.Trim();
        return SanitizeFileName(string.IsNullOrWhiteSpace(preferredName) ? "template" : preferredName);
    }

    private string EnsureTemplateFilesRootPath()
    {
        var rootPath = EditorViewModel?.TemplateFilesRootPath ?? string.Empty;
        Directory.CreateDirectory(rootPath);
        return rootPath;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        await WindowInteractionHelper.ShowMessageAsync(
            XamlRoot,
            ActualTheme,
            viewModel.Texts.OkButton,
            title,
            message);
    }

    private Task RunGuardedAsync(string actionName, Func<Task> action, string? errorTitle = null)
    {
        var texts = ViewModel?.Texts;
        return UiActionGuard.RunAsync(
            this,
            nameof(TemplatesView),
            actionName,
            errorTitle ?? texts?.ErrorSelectionFailedTitle ?? "Operation failed",
            texts?.OkButton ?? "OK",
            action);
    }

    private async Task<bool> ShowConfirmationAsync(
        string title,
        string message,
        string primaryButtonText,
        string closeButtonText,
        ContentDialogButton defaultButton = ContentDialogButton.Primary)
    {
        return await WindowInteractionHelper.ShowConfirmationAsync(
            XamlRoot,
            ActualTheme,
            title,
            message,
            primaryButtonText,
            closeButtonText,
            defaultButton);
    }

    private bool TryGetTemplateItem(object sender, out TemplateLibraryItemViewModel templateItem)
    {
        var viewModel = ViewModel;
        templateItem = null!;

        if (viewModel is null || sender is not Button button)
        {
            return false;
        }

        if (button.CommandParameter is TemplateLibraryItemViewModel commandItem)
        {
            templateItem = commandItem;
            return true;
        }

        if (button.DataContext is TemplateLibraryItemViewModel dataContextItem)
        {
            templateItem = dataContextItem;
            return true;
        }

        if (FindAncestor<ListViewItem>(button)?.DataContext is TemplateLibraryItemViewModel containerItem)
        {
            templateItem = containerItem;
            return true;
        }

        if (button.Tag is not string templateId || string.IsNullOrWhiteSpace(templateId))
        {
            return false;
        }

        var matchedItem = LibraryViewModel?.TemplateLibraryItems.FirstOrDefault(item =>
            string.Equals(item.TemplateId, templateId, StringComparison.OrdinalIgnoreCase));
        if (matchedItem is null)
        {
            return false;
        }

        templateItem = matchedItem;
        return true;
    }

    private void TryWriteDiagnostic(string message)
    {
        try
        {
            App.GetService<IAppDiagnostics>().Write(nameof(TemplatesView), message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write templates diagnostic. {ex}");
        }
    }

    private static void ConfigureTwoItemGrid(
        Grid grid,
        ColumnDefinition secondColumn,
        FrameworkElement secondItem,
        bool stacked,
        GridLength expandedSecondColumnWidth)
    {
        grid.ColumnSpacing = stacked ? 0 : UiTokens.SpacingM;
        secondColumn.Width = stacked ? new GridLength(0) : expandedSecondColumnWidth;
        Grid.SetRow(secondItem, stacked ? 1 : 0);
        Grid.SetColumn(secondItem, stacked ? 0 : 1);
    }

    private static void ConfigureThreeItemGrid(
        Grid grid,
        ColumnDefinition secondColumn,
        ColumnDefinition thirdColumn,
        FrameworkElement secondItem,
        FrameworkElement thirdItem,
        bool stacked)
    {
        grid.ColumnSpacing = stacked ? 0 : UiTokens.SpacingM;
        secondColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        thirdColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(secondItem, stacked ? 1 : 0);
        Grid.SetColumn(secondItem, stacked ? 0 : 1);
        Grid.SetRow(thirdItem, stacked ? 2 : 0);
        Grid.SetColumn(thirdItem, stacked ? 0 : 2);
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(sanitized) ? "template" : sanitized;
    }
}
