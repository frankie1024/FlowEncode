using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using FlowEncode.Domain;
using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public sealed class TemplateEditorViewModel : ModuleViewModelBase
{
    private readonly TemplateLibraryViewModel _library;
    private bool _isEditorDisposed;

    public TemplateEditorViewModel(MainWindowViewModel owner, TemplateLibraryViewModel library)
        : base(owner)
    {
        _library = library;
        _library.PropertyChanged += Library_PropertyChanged;
    }

    public AppText Texts => Owner.Texts;

    public string PreviewCommandLine => Owner.PreviewCommandLine;

    public string SelectedProfileCaption => Owner.SelectedProfileCaption;

    public string DraftTemplateName
    {
        get => Owner.DraftTemplateName;
        set => Owner.DraftTemplateName = value;
    }

    public string DraftTemplateNotes
    {
        get => Owner.DraftTemplateNotes;
        set => Owner.DraftTemplateNotes = value;
    }

    public ObservableCollection<EncoderOption> EncoderOptions => Owner.EncoderOptions;

    public ObservableCollection<RateControlOption> AvailableRateControlModes => Owner.AvailableRateControlModes;

    public ObservableCollection<StringChoiceOption> AvailablePresets => Owner.AvailablePresets;

    public ObservableCollection<StringChoiceOption> AvailableTunes => Owner.AvailableTunes;

    public ObservableCollection<StringChoiceOption> AvailableProfiles => Owner.AvailableProfiles;

    public ObservableCollection<StringChoiceOption> AvailableOutputFormats => Owner.AvailableOutputFormats;

    public EncoderOption? SelectedEncoder
    {
        get => Owner.SelectedEncoder;
        set => Owner.SelectedEncoder = value;
    }

    public RateControlOption? SelectedRateControl
    {
        get => Owner.SelectedRateControl;
        set => Owner.SelectedRateControl = value;
    }

    public StringChoiceOption? SelectedPreset
    {
        get => Owner.SelectedPreset;
        set => Owner.SelectedPreset = value;
    }

    public StringChoiceOption? SelectedTune
    {
        get => Owner.SelectedTune;
        set => Owner.SelectedTune = value;
    }

    public StringChoiceOption? SelectedProfileOption
    {
        get => Owner.SelectedProfileOption;
        set => Owner.SelectedProfileOption = value;
    }

    public StringChoiceOption? SelectedOutputFormat
    {
        get => Owner.SelectedOutputFormat;
        set => Owner.SelectedOutputFormat = value;
    }

    public double DraftQuality
    {
        get => Owner.DraftQuality;
        set => Owner.DraftQuality = value;
    }

    public double DraftBitrate
    {
        get => Owner.DraftBitrate;
        set => Owner.DraftBitrate = value;
    }

    public string DraftAdditionalArguments
    {
        get => Owner.DraftAdditionalArguments;
        set => Owner.DraftAdditionalArguments = value;
    }

    public string DraftUhdParameters
    {
        get => Owner.DraftUhdParameters;
        set => Owner.DraftUhdParameters = value;
    }

    public string TemplateFilesRootPath => Owner.TemplateFilesRootPath;

    public string QualityInputLabel => Owner.QualityInputLabel;

    public string BitrateInputLabel => Owner.BitrateInputLabel;

    public Visibility DraftQualityVisibility => Owner.DraftQualityVisibility;

    public Visibility DraftBitrateVisibility => Owner.DraftBitrateVisibility;

    public Visibility X265UhdVisibility => Owner.X265UhdVisibility;

    public string DraftConstraintWarningText => Owner.DraftConstraintWarningText;

    public Visibility DraftConstraintWarningVisibility => Owner.DraftConstraintWarningVisibility;

    public string? EditingUserTemplateId => _library.EditingUserTemplateId;

    public bool CanEditTemplateDraft => _library.CanEditTemplateDraft;

    public bool HasUnsavedTemplateChanges => _library.HasUnsavedTemplateChanges;

    public void BeginNewTemplateDraft()
    {
        Owner.BeginNewTemplateDraft();
    }

    public Task<SavedTemplate?> SaveCurrentTemplateAsync()
    {
        return _library.SaveCurrentTemplateAsync();
    }

    public Task ExportCurrentTemplateAsync(string filePath)
    {
        return _library.ExportCurrentTemplateAsync(filePath);
    }

    public override void Dispose()
    {
        if (!_isEditorDisposed)
        {
            _isEditorDisposed = true;
            _library.PropertyChanged -= Library_PropertyChanged;
        }

        base.Dispose();
    }

    private void Library_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isEditorDisposed)
        {
            return;
        }

        if (string.IsNullOrEmpty(e.PropertyName))
        {
            OnPropertyChanged(nameof(EditingUserTemplateId));
            OnPropertyChanged(nameof(CanEditTemplateDraft));
            OnPropertyChanged(nameof(HasUnsavedTemplateChanges));
            return;
        }

        if (e.PropertyName is nameof(EditingUserTemplateId)
            or nameof(CanEditTemplateDraft)
            or nameof(HasUnsavedTemplateChanges))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }
}
