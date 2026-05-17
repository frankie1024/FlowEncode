using System.Collections.ObjectModel;
using System.Threading.Tasks;
using FlowEncode.Domain;
using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public sealed class OverviewComposerViewModel : ModuleViewModelBase
{
    public OverviewComposerViewModel(MainWindowViewModel owner)
        : base(owner)
    {
    }

    public AppText Texts => Owner.Texts;

    public string SourcePath
    {
        get => Owner.SourcePath;
        set => Owner.SourcePath = value;
    }

    public string OutputPath
    {
        get => Owner.OutputPath;
        set => Owner.OutputPath = value;
    }

    public string DraftOutputPreviewText => Owner.DraftOutputPreviewText;

    public bool CanQueueJob => Owner.CanQueueJob;

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

    public string QualityInputLabel => Owner.QualityInputLabel;

    public Visibility DraftQualityVisibility => Owner.DraftQualityVisibility;

    public string BitrateInputLabel => Owner.BitrateInputLabel;

    public Visibility DraftBitrateVisibility => Owner.DraftBitrateVisibility;

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

    public ObservableCollection<TemplateLibraryItemViewModel> TemplateLibraryItems => Owner.TemplatesModule.Library.TemplateLibraryItems;

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

    public string DraftAdditionalArguments
    {
        get => Owner.DraftAdditionalArguments;
        set => Owner.DraftAdditionalArguments = value;
    }

    public string DraftConstraintWarningText => Owner.DraftConstraintWarningText;

    public Visibility DraftConstraintWarningVisibility => Owner.DraftConstraintWarningVisibility;

    public string DraftUhdParameters
    {
        get => Owner.DraftUhdParameters;
        set => Owner.DraftUhdParameters = value;
    }

    public Visibility X265UhdVisibility => Owner.X265UhdVisibility;

    public ObservableCollection<SavedTemplate> UserTemplates => Owner.TemplatesModule.Library.UserTemplates;

    public Task SelectUserTemplateAsync(SavedTemplate? template)
    {
        return Owner.TemplatesModule.Library.SelectUserTemplateAsync(template);
    }

    public Task ApplyUserTemplateToEncodingDraftAsync(SavedTemplate? template)
    {
        return Owner.ApplyUserTemplateToEncodingDraftAsync(template);
    }

    public QueueJobPreflightResult AnalyzeCurrentJobForQueue(bool requireSourceExists = true)
    {
        return Owner.AnalyzeCurrentJobForQueue(requireSourceExists);
    }

    public Task<string?> QueueCurrentJobAsync(bool startImmediately = false, QueueJobPreflightResult? preflight = null)
    {
        return Owner.QueueCurrentJobAsync(startImmediately, preflight);
    }

    public string? ImportHdrParametersFromText(string rawText)
    {
        return Owner.ImportHdrParametersFromText(rawText);
    }
}
