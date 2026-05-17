using System.Threading.Tasks;
using FlowEncode.Domain;

namespace FlowEncode.ViewModels;

public interface ITemplateLibraryHost
{
    EncodingProfile? ActiveProfile { get; }

    string DraftTemplateName { get; }

    string DraftTemplateNotes { get; }

    AppText Texts { get; }

    Task ApplyUserTemplateToDraftAsync(SavedTemplate template);

    void ApplySavedTemplateToDraft(SavedTemplate savedTemplate);

    void BeginNewTemplateDraft();

    void RaiseSummaryPropertyChanges();

    void NotifyDraftChanged();

    string StatusText { set; }
}
