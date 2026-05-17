namespace FlowEncode.ViewModels;

public sealed class TemplatesViewModel : ModuleViewModelBase
{
    public TemplatesViewModel(MainWindowViewModel owner)
        : base(owner)
    {
        Library = TrackDisposable(new TemplateLibraryViewModel(owner.ProfileLibraryService, owner, owner.AppPaths));
        Editor = TrackDisposable(new TemplateEditorViewModel(owner));
    }

    public AppText Texts => Owner.Texts;

    public TemplateLibraryViewModel Library { get; }

    public TemplateEditorViewModel Editor { get; }
}
