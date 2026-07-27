using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public sealed class TemplateLibraryViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject, IDisposable
{
    private readonly IProfileLibraryService _profileLibraryService;
    private readonly ITemplateLibraryHost _host;
    private readonly LocalAppPaths _appPaths;

    private string _templateSearchText = string.Empty;
    private string? _editingTemplateId;
    private string? _currentTemplateSelectionKey;
    private string _templateBaselineName = string.Empty;
    private string _templateBaselineNotes = string.Empty;
    private EncodingProfile? _templateBaselineProfile;

    public TemplateLibraryViewModel(
        IProfileLibraryService profileLibraryService,
        ITemplateLibraryHost host,
        LocalAppPaths appPaths)
    {
        _profileLibraryService = profileLibraryService;
        _host = host;
        _appPaths = appPaths;
    }

    public AppText Texts => _host.Texts;

    public ObservableCollection<SavedTemplate> UserTemplates { get; } = [];

    public ObservableCollection<TemplateLibraryItemViewModel> TemplateLibraryItems { get; } = [];

    public string TemplateFilesRootPath => _appPaths.WorkspaceTemplatesRootPath;

    public string TemplateSearchText
    {
        get => _templateSearchText;
        set
        {
            if (SetProperty(ref _templateSearchText, value))
            {
                RefreshTemplateLibraryItems();
            }
        }
    }

    public Visibility TemplateLibraryEmptyVisibility => TemplateLibraryItems.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string? EditingUserTemplateId => _editingTemplateId;

    public string? CurrentTemplateSelectionKey => _currentTemplateSelectionKey;

    private bool IsEditingPinnedTemplate => GetEditingUserTemplate()?.IsPinned == true;

    public bool CanEditTemplateDraft => !IsEditingPinnedTemplate;

    public bool HasUnsavedTemplateChanges => !MatchesTemplateEditingBaseline();

    public void ApplyLoadedTemplates(IEnumerable<SavedTemplate> templates)
    {
        ReplaceItems(UserTemplates, templates);
        RefreshTemplateLibraryItems();
    }

    public void RefreshLibraryView()
    {
        RefreshTemplateLibraryItems();
        OnPropertyChanged(nameof(TemplateLibraryItems));
    }

    public async Task SelectUserTemplateAsync(SavedTemplate? template)
    {
        if (template is null)
        {
            return;
        }

        await _host.ApplyUserTemplateToDraftAsync(template);

        CaptureTemplateEditingBaseline(
            template.Id,
            $"user:{template.Id}",
            template.Name,
            template.Notes,
            _host.ActiveProfile);
    }

    public Task<SavedTemplate> ReadTemplateAsync(string filePath)
    {
        return _profileLibraryService.ReadTemplateAsync(filePath);
    }

    public async Task<SavedTemplate?> SaveCurrentTemplateAsync()
    {
        if (_host.ActiveProfile is null || string.IsNullOrWhiteSpace(_host.DraftTemplateName))
        {
            return null;
        }

        var normalizedTemplateName = _host.DraftTemplateName?.Trim() ?? string.Empty;
        var normalizedTemplateNotes = _host.DraftTemplateNotes?.Trim() ?? string.Empty;
        var editingTemplate = GetEditingUserTemplate();
        if (editingTemplate?.IsPinned == true
            && string.Equals(editingTemplate.Name, normalizedTemplateName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Texts.PinnedTemplateLockedMessage);
        }

        var profileToSave = _host.ActiveProfile with
        {
            Name = normalizedTemplateName,
            Description = normalizedTemplateNotes
        };

        return await PersistUserTemplateAsync(
            normalizedTemplateName,
            normalizedTemplateNotes,
            profileToSave,
            editingTemplate?.IsPinned == true ? null : _editingTemplateId,
            isPinned: false,
            Texts.TemplateSavedStatus(normalizedTemplateName));
    }

    public async Task<SavedTemplate> ImportTemplateAsync(SavedTemplate template, string? overwriteTemplateId = null)
    {
        ArgumentNullException.ThrowIfNull(template);

        var normalizedTemplateName = template.Name?.Trim() ?? string.Empty;
        var normalizedTemplateNotes = template.Notes?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedTemplateName))
        {
            throw new InvalidOperationException(Texts.EmptyTemplateNameMessage);
        }

        var profileToSave = template.Profile with
        {
            Name = normalizedTemplateName,
            Description = normalizedTemplateNotes
        };

        return await PersistUserTemplateAsync(
            normalizedTemplateName,
            normalizedTemplateNotes,
            profileToSave,
            overwriteTemplateId,
            template.IsPinned,
            Texts.TemplateImportedStatus(normalizedTemplateName));
    }

    public async Task ExportCurrentTemplateAsync(string filePath)
    {
        var exportTemplate = BuildDraftTemplateForExchange();
        if (exportTemplate is null)
        {
            throw new InvalidOperationException(Texts.TemplateExportUnavailableMessage);
        }

        await _profileLibraryService.ExportTemplateAsync(exportTemplate, filePath);
        _host.StatusText = Texts.TemplateExportedStatus(filePath);
    }

    public SavedTemplate? FindUserTemplateByName(string? templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName))
        {
            return null;
        }

        var normalizedTemplateName = templateName.Trim();
        return UserTemplates.FirstOrDefault(template =>
            string.Equals(template.Name, normalizedTemplateName, StringComparison.OrdinalIgnoreCase));
    }

    public SavedTemplate? FindUserTemplateById(string? templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return null;
        }

        return UserTemplates.FirstOrDefault(template =>
            string.Equals(template.Id, templateId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task DeleteTemplateAsync(string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return;
        }

        var currentTemplate = FindUserTemplateById(templateId);
        if (currentTemplate?.IsPinned == true)
        {
            throw new InvalidOperationException(Texts.PinnedTemplateLockedMessage);
        }

        BeginWorkspaceMutation();
        try
        {
            await _profileLibraryService.DeleteTemplateAsync(templateId);
            ReplaceItems(UserTemplates, await _profileLibraryService.GetUserTemplatesAsync());
            RefreshTemplateLibraryItems();

            if (string.Equals(_editingTemplateId, templateId, StringComparison.OrdinalIgnoreCase))
            {
                _host.BeginNewTemplateDraft();
            }

            _host.RaiseSummaryPropertyChanges();
            _host.StatusText = Texts.UserTemplateDeletedStatus;
        }
        finally
        {
            _host.EndTemplateLibraryMutation();
        }
    }

    public async Task<SavedTemplate> SetTemplatePinnedAsync(string templateId, bool isPinned)
    {
        BeginWorkspaceMutation();
        try
        {
            var template = FindUserTemplateById(templateId);
            if (template is null)
            {
                ReplaceItems(UserTemplates, await _profileLibraryService.GetUserTemplatesAsync());
                RefreshTemplateLibraryItems();
                template = FindUserTemplateById(templateId);
            }

            if (template is null)
            {
                throw new InvalidOperationException(Texts.TemplateMissingMessage);
            }

            var updatedTemplate = await _profileLibraryService.SetTemplatePinnedAsync(template.Id, isPinned);
            ReplaceItems(UserTemplates, await _profileLibraryService.GetUserTemplatesAsync());
            RefreshTemplateLibraryItems();
            RaiseTemplateLockPropertyChanges();
            _host.RaiseSummaryPropertyChanges();
            _host.StatusText = isPinned
                ? Texts.TemplatePinnedStatus(updatedTemplate.Name)
                : Texts.TemplateUnpinnedStatus(updatedTemplate.Name);
            return updatedTemplate;
        }
        finally
        {
            _host.EndTemplateLibraryMutation();
        }
    }

    internal void CaptureTemplateEditingBaseline(
        string? editingTemplateId,
        string? selectionKey,
        string templateName,
        string templateNotes,
        EncodingProfile? profile)
    {
        _editingTemplateId = editingTemplateId;
        _currentTemplateSelectionKey = selectionKey;
        _templateBaselineName = templateName?.Trim() ?? string.Empty;
        _templateBaselineNotes = templateNotes?.Trim() ?? string.Empty;
        _templateBaselineProfile = profile;

        OnPropertyChanged(nameof(EditingUserTemplateId));
        OnPropertyChanged(nameof(CurrentTemplateSelectionKey));
        OnPropertyChanged(nameof(HasUnsavedTemplateChanges));
        RaiseTemplateLockPropertyChanges();
    }

    internal void NotifyDraftChanged()
    {
        OnPropertyChanged(nameof(HasUnsavedTemplateChanges));
    }

    public void Dispose()
    {
    }

    private void RefreshTemplateLibraryItems()
    {
        IEnumerable<TemplateLibraryItemViewModel> items =
            UserTemplates.Select(BuildUserTemplateLibraryItem);

        var search = TemplateSearchText?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            items = items.Where(item => item.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        ReplaceItems(TemplateLibraryItems, items);
        OnPropertyChanged(nameof(TemplateLibraryEmptyVisibility));
    }

    private SavedTemplate? BuildDraftTemplateForExchange()
    {
        if (_host.ActiveProfile is null)
        {
            return null;
        }

        var normalizedTemplateName = _host.DraftTemplateName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedTemplateName))
        {
            return null;
        }

        var normalizedTemplateNotes = _host.DraftTemplateNotes?.Trim() ?? string.Empty;
        return new SavedTemplate(
            _editingTemplateId ?? Guid.NewGuid().ToString("N"),
            normalizedTemplateName,
            normalizedTemplateNotes,
            _host.ActiveProfile with
            {
                Name = normalizedTemplateName,
                Description = normalizedTemplateNotes
            },
            DateTimeOffset.Now);
    }

    private async Task<SavedTemplate> PersistUserTemplateAsync(
        string templateName,
        string templateNotes,
        EncodingProfile profile,
        string? templateId,
        bool isPinned,
        string statusText)
    {
        BeginWorkspaceMutation();
        try
        {
            var savedTemplate = await _profileLibraryService.SaveTemplateAsync(
                templateName,
                templateNotes,
                profile,
                templateId,
                isPinned);

            ReplaceItems(UserTemplates, await _profileLibraryService.GetUserTemplatesAsync());
            RefreshTemplateLibraryItems();
            CaptureTemplateEditingBaseline(
                savedTemplate.Id,
                $"user:{savedTemplate.Id}",
                savedTemplate.Name,
                savedTemplate.Notes,
                savedTemplate.Profile);

            _host.ApplySavedTemplateToDraft(savedTemplate);
            _host.RaiseSummaryPropertyChanges();
            _host.StatusText = statusText;
            return savedTemplate;
        }
        finally
        {
            _host.EndTemplateLibraryMutation();
        }
    }

    private void BeginWorkspaceMutation()
    {
        if (!_host.TryBeginTemplateLibraryMutation())
        {
            throw new InvalidOperationException(Texts.WorkspaceDirectoryChangeInProgressMessage);
        }
    }

    private bool MatchesTemplateEditingBaseline()
    {
        var currentName = _host.DraftTemplateName?.Trim() ?? string.Empty;
        var currentNotes = _host.DraftTemplateNotes?.Trim() ?? string.Empty;

        return string.Equals(currentName, _templateBaselineName, StringComparison.Ordinal)
            && string.Equals(currentNotes, _templateBaselineNotes, StringComparison.Ordinal)
            && EqualityComparer<EncodingProfile?>.Default.Equals(_host.ActiveProfile, _templateBaselineProfile);
    }

    private TemplateLibraryItemViewModel BuildUserTemplateLibraryItem(SavedTemplate template)
    {
        return new TemplateLibraryItemViewModel(
            $"user:{template.Id}",
            template.Name,
            template.IsPinned ? Texts.TemplateSourcePinned : Texts.TemplateSourceUser,
            $"{template.Profile.EncoderLabel} · {template.Profile.QualitySummary}",
            template.UpdatedLabel,
            template.Id,
            template.IsPinned,
            template.IsPinned ? Texts.UnpinTemplateButton : Texts.PinTemplateButton,
            Texts.DeleteTemplateButton,
            template);
    }

    private SavedTemplate? GetEditingUserTemplate()
    {
        return FindUserTemplateById(_editingTemplateId);
    }

    private void RaiseTemplateLockPropertyChanges()
    {
        OnPropertyChanged(nameof(IsEditingPinnedTemplate));
        OnPropertyChanged(nameof(CanEditTemplateDraft));
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();

        foreach (var item in source)
        {
            target.Add(item);
        }
    }

}
