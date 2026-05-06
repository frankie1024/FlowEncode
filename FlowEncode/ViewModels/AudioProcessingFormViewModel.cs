using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using FlowEncode.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FlowEncode.ViewModels;

public sealed class AudioProcessingFormViewModel : ModuleViewModelBase
{
    public AudioProcessingFormViewModel(MainWindowViewModel owner)
        : base(owner)
    {
    }

    public AppText Texts => Owner.Texts;

    public ObservableCollection<AudioWorkflowOption> AudioWorkflowOptions => Owner.AudioWorkflowOptions;

    public ObservableCollection<AudioEac3ToOutputFormatOption> AudioEac3ToOutputFormatOptions => Owner.AudioEac3ToOutputFormatOptions;

    public ObservableCollection<AudioOpusBitrateOption> AudioOpusBitrateOptions => Owner.AudioOpusBitrateOptions;

    public string AudioProcessingSourcePath
    {
        get => Owner.AudioProcessingSourcePath;
        set => Owner.AudioProcessingSourcePath = value;
    }

    public string AudioProcessingOutputPath
    {
        get => Owner.AudioProcessingOutputPath;
        set => Owner.AudioProcessingOutputPath = value;
    }

    public AudioWorkflowOption? SelectedAudioWorkflow
    {
        get => Owner.SelectedAudioWorkflow;
        set => Owner.SelectedAudioWorkflow = value;
    }

    public AudioEac3ToOutputFormatOption? SelectedAudioEac3ToOutputFormat
    {
        get => Owner.SelectedAudioEac3ToOutputFormat;
        set => Owner.SelectedAudioEac3ToOutputFormat = value;
    }

    public AudioOpusBitrateOption? SelectedAudioOpusBitrate
    {
        get => Owner.SelectedAudioOpusBitrate;
        set => Owner.SelectedAudioOpusBitrate = value;
    }

    public bool AudioOpusUseMappingFamily1
    {
        get => Owner.AudioOpusUseMappingFamily1;
        set => Owner.AudioOpusUseMappingFamily1 = value;
    }

    public string AudioProcessingAdditionalArguments
    {
        get => Owner.AudioProcessingAdditionalArguments;
        set => Owner.AudioProcessingAdditionalArguments = value;
    }

    public string AudioSourceInfoText => Owner.AudioSourceInfoText;

    public string AudioProcessingOutputHeader => Owner.AudioProcessingOutputHeader;

    public string AudioProcessingOutputBrowseButtonText => Owner.AudioProcessingOutputBrowseButtonText;

    public string AudioProcessingOutputPreviewText => Owner.AudioProcessingOutputPreviewText;

    public Visibility AudioEac3ToOptionsVisibility => Owner.AudioEac3ToOptionsVisibility;

    public Visibility AudioOpusOptionsVisibility => Owner.AudioOpusOptionsVisibility;

    public Brush AudioProcessingStatusPanelBorderBrush => Owner.AudioProcessingStatusPanelBorderBrush;

    public string AudioCapabilitySummary => Owner.AudioCapabilitySummary;

    public bool CanStartAudioProcessing => Owner.CanStartAudioProcessing;

    public bool CanCancelAudioProcessing => Owner.CanCancelAudioProcessing;

    public bool CanClearAudioProcessingTask => Owner.CanClearAudioProcessingTask;

    public void InitializeState()
    {
        ReplaceItems(AudioWorkflowOptions, BuildAudioWorkflowOptions());
        ReplaceItems(AudioEac3ToOutputFormatOptions, BuildAudioEac3ToOutputFormatOptions());
        ReplaceItems(AudioOpusBitrateOptions, BuildAudioOpusBitrateOptions());
        SelectedAudioWorkflow = AudioWorkflowOptions.FirstOrDefault();
        SelectedAudioEac3ToOutputFormat = AudioEac3ToOutputFormatOptions.FirstOrDefault();
        SelectedAudioOpusBitrate = null;
        AudioOpusUseMappingFamily1 = false;
        Owner.SetAudioProcessingDisplayState(null);
        Owner.AudioProcessingStatusText = Texts.AudioProcessingIdleStatus;
    }

    public void HandleEnvironmentReadinessApplied()
    {
        Owner.RaiseAudioProcessingEnvironmentPropertyChanges();
    }

    public void ApplyLanguageState()
    {
        var workflow = SelectedAudioWorkflow?.Value ?? AudioProcessingMode.Ddp;
        var eac3ToOutputFormat = SelectedAudioEac3ToOutputFormat?.Value ?? AudioEac3ToOutputFormat.Flac;
        var opusBitrate = SelectedAudioOpusBitrate?.Value;
        var useOpusMappingFamily1 = AudioOpusUseMappingFamily1;

        Owner.BeginAudioProcessingLanguageStateApplication();
        try
        {
            ReplaceItems(AudioWorkflowOptions, BuildAudioWorkflowOptions());
            ReplaceItems(AudioEac3ToOutputFormatOptions, BuildAudioEac3ToOutputFormatOptions());
            ReplaceItems(AudioOpusBitrateOptions, BuildAudioOpusBitrateOptions());
            SelectedAudioWorkflow = AudioWorkflowOptions.FirstOrDefault(option => option.Value == workflow) ?? AudioWorkflowOptions.FirstOrDefault();
            SelectedAudioEac3ToOutputFormat = AudioEac3ToOutputFormatOptions.FirstOrDefault(option => option.Value == eac3ToOutputFormat) ?? AudioEac3ToOutputFormatOptions.FirstOrDefault();
            SelectedAudioOpusBitrate = opusBitrate.HasValue
                ? AudioOpusBitrateOptions.FirstOrDefault(option => option.Value == opusBitrate.Value)
                : null;
            AudioOpusUseMappingFamily1 = useOpusMappingFamily1;

            if (!Owner.IsAudioProcessingRunning)
            {
                Owner.SetAudioProcessingDisplayState(null);
                Owner.AudioProcessingStatusText = Texts.AudioProcessingIdleStatus;
            }

            Owner.RaiseAudioProcessingLanguagePropertyChanges();
        }
        finally
        {
            Owner.EndAudioProcessingLanguageStateApplication();
        }

        Owner.RefreshAudioProcessingCommandPreview();
    }

    public string? ValidateAudioProcessingForStart(out string? existingOutputPath)
    {
        return Owner.ValidateAudioProcessingForStart(out existingOutputPath);
    }

    public Task<string?> StartAudioProcessingAsync()
    {
        return Owner.StartAudioProcessingAsync();
    }

    public void CancelAudioProcessing()
    {
        Owner.CancelAudioProcessing();
    }

    public void ClearAudioProcessingTask()
    {
        Owner.ClearAudioProcessingTask();
    }

    private IEnumerable<AudioWorkflowOption> BuildAudioWorkflowOptions()
    {
        return
        [
            new AudioWorkflowOption(AudioProcessingMode.Ddp, Texts.AudioWorkflowLabel(AudioProcessingMode.Ddp)),
            new AudioWorkflowOption(AudioProcessingMode.Opus, Texts.AudioWorkflowLabel(AudioProcessingMode.Opus)),
            new AudioWorkflowOption(AudioProcessingMode.Eac3To, Texts.AudioWorkflowLabel(AudioProcessingMode.Eac3To))
        ];
    }

    private IEnumerable<AudioEac3ToOutputFormatOption> BuildAudioEac3ToOutputFormatOptions()
    {
        return
        [
            new AudioEac3ToOutputFormatOption(AudioEac3ToOutputFormat.Flac, Texts.AudioEac3ToOutputFormatLabel(AudioEac3ToOutputFormat.Flac)),
            new AudioEac3ToOutputFormatOption(AudioEac3ToOutputFormat.Ac3, Texts.AudioEac3ToOutputFormatLabel(AudioEac3ToOutputFormat.Ac3))
        ];
    }

    private IEnumerable<AudioOpusBitrateOption> BuildAudioOpusBitrateOptions()
    {
        return
        [
            new AudioOpusBitrateOption(510, Texts.AudioOpusBitrateLabel(510)),
            new AudioOpusBitrateOption(384, Texts.AudioOpusBitrateLabel(384)),
            new AudioOpusBitrateOption(192, Texts.AudioOpusBitrateLabel(192)),
            new AudioOpusBitrateOption(96, Texts.AudioOpusBitrateLabel(96))
        ];
    }
}
