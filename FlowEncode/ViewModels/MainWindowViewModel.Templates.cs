using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;

namespace FlowEncode.ViewModels;

public partial class MainWindowViewModel : ITemplateLibraryHost
{
    internal IProfileLibraryService ProfileLibraryService => _profileLibraryService;

    internal LocalAppPaths AppPaths => _appPaths;

    EncodingProfile? ITemplateLibraryHost.ActiveProfile => _activeProfile;

    string ITemplateLibraryHost.DraftTemplateName => DraftTemplateName;

    string ITemplateLibraryHost.DraftTemplateNotes => DraftTemplateNotes;

    AppText ITemplateLibraryHost.Texts => Texts;

    bool ITemplateLibraryHost.TryBeginTemplateLibraryMutation()
    {
        lock (_workspaceOperationGate)
        {
            if (_isChangingWorkspaceRoot)
            {
                return false;
            }

            _templateLibraryMutationCount++;
            return true;
        }
    }

    void ITemplateLibraryHost.EndTemplateLibraryMutation()
    {
        lock (_workspaceOperationGate)
        {
            _templateLibraryMutationCount--;
        }
    }

    async Task ITemplateLibraryHost.ApplyUserTemplateToDraftAsync(SavedTemplate template)
    {
        ApplyProfileToDraft(
            template.Profile,
            Texts.UserCaption(template.Name),
            template.Name,
            template.Notes);

        if (_activeProfile is not null)
        {
            await RefreshPreviewNowAsync(_activeProfile);
        }
    }

    void ITemplateLibraryHost.ApplySavedTemplateToDraft(SavedTemplate savedTemplate)
    {
        _isSynchronizingDraft = true;

        try
        {
            _draftProfileName = savedTemplate.Profile.Name;
            _draftProfileDescription = savedTemplate.Profile.Description;
            _activeProfile = savedTemplate.Profile;
            DraftTemplateName = savedTemplate.Name;
            DraftTemplateNotes = savedTemplate.Notes;
        }
        finally
        {
            _isSynchronizingDraft = false;
        }

        SelectedProfileCaption = Texts.UserCaption(savedTemplate.Name);
    }

    void ITemplateLibraryHost.BeginNewTemplateDraft() => BeginNewTemplateDraft();

    void ITemplateLibraryHost.RaiseSummaryPropertyChanges() => RaiseSummaryPropertyChanges();

    void ITemplateLibraryHost.NotifyDraftChanged() => TemplatesModule.Library.NotifyDraftChanged();

    string ITemplateLibraryHost.StatusText { set => StatusText = value; }

    internal void RefreshTemplateLibraryView() => TemplatesModule.Library.RefreshLibraryView();

    internal async Task ApplyUserTemplateToEncodingDraftAsync(SavedTemplate? template)
    {
        if (template is null)
        {
            return;
        }

        ApplyProfileToDraft(
            template.Profile,
            Texts.UserCaption(template.Name),
            template.Name,
            template.Notes);

        if (_activeProfile is not null)
        {
            await RefreshPreviewNowAsync(_activeProfile);
        }

        TemplatesModule.Library.CaptureTemplateEditingBaseline(
            null,
            null,
            template.Name,
            template.Notes,
            _activeProfile);
    }
}
