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
        Deew = new(owner.LoadDeewOptions(), owner.DeewOptionsChanged, () => owner.Texts);
        Deew.PropertyChanged += (_, _) =>
        {
            NotifyDeewModeVisibility();
            NotifySummaryCards();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AudioDeewOptionsVisibility))
            {
                NotifyDeewModeVisibility();
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName is nameof(SelectedAudioWorkflow)
                    or nameof(SelectedAudioEac3ToOutputFormat)
                    or nameof(SelectedAudioOpusBitrate)
                    or nameof(AudioOpusUseMappingFamily1)
                    or nameof(AudioProcessingAdditionalArguments)
                    or nameof(AudioDeewOptionsVisibility)
                    or nameof(Texts))
            {
                NotifySummaryCards();
            }
        };
    }

    public DeewOptionsViewModel Deew { get; }
    public Visibility AudioDeewOptionsVisibility => Owner.AudioDeewOptionsVisibility;

    public Visibility DeewDdpModeVisibility => AudioDeewOptionsVisibility == Visibility.Visible ? Deew.DdpModeVisibility : Visibility.Collapsed;
    public Visibility DeewAtmosModeVisibility => AudioDeewOptionsVisibility == Visibility.Visible ? Deew.AtmosModeVisibility : Visibility.Collapsed;
    public Visibility DeewAc4ImsModeVisibility => AudioDeewOptionsVisibility == Visibility.Visible ? Deew.Ac4ImsModeVisibility : Visibility.Collapsed;
    public Visibility DeewWarningVisibility => AudioDeewOptionsVisibility == Visibility.Visible ? Deew.WarningVisibility : Visibility.Collapsed;

    public string SummaryCard1Label => Texts.AudioWorkflowHeader;

    public string SummaryCard1Value => SelectedAudioWorkflow?.Label ?? "deew";

    public string SummaryCard2Label => (SelectedAudioWorkflow?.Value ?? AudioProcessingMode.Ddp) switch
    {
        AudioProcessingMode.Ddp when Deew.MeasureOnly => Texts.Pick("运行模式", "Mode"),
        AudioProcessingMode.Ddp => Texts.DeewFormatHeader,
        AudioProcessingMode.Opus => Texts.Pick("编码后端", "Backend"),
        _ => Texts.AudioEac3ToOutputFormatHeader
    };

    public string SummaryCard2Value => (SelectedAudioWorkflow?.Value ?? AudioProcessingMode.Ddp) switch
    {
        AudioProcessingMode.Ddp when Deew.MeasureOnly => Texts.Pick("仅测响度", "Measure Only"),
        AudioProcessingMode.Ddp => Deew.EffectiveProfile is { } profile
            ? $"{profile.Options.Format} ({profile.Extension})"
            : Deew.Format,
        AudioProcessingMode.Opus => AudioOpusUseMappingFamily1 ? "FFmpeg libopus" : "opusenc",
        _ => SelectedAudioEac3ToOutputFormat?.Label ?? ".flac"
    };

    public string SummaryCard3Label => (SelectedAudioWorkflow?.Value ?? AudioProcessingMode.Ddp) switch
    {
        AudioProcessingMode.Ddp when Deew.MeasureOnly => Texts.Pick("媒体输出", "Media Output"),
        AudioProcessingMode.Ddp => Texts.Pick("码率", "Bitrate"),
        AudioProcessingMode.Opus => Texts.AudioOpusBitrateHeader,
        _ => Texts.Pick("编码类型", "Encoding")
    };

    public string SummaryCard3Value => (SelectedAudioWorkflow?.Value ?? AudioProcessingMode.Ddp) switch
    {
        AudioProcessingMode.Ddp when Deew.MeasureOnly => Texts.Pick("不生成", "None"),
        AudioProcessingMode.Ddp => Deew.EffectiveProfile?.EffectiveBitrateKbps is { } bitrate
            ? $"{bitrate} kbps"
            : Texts.Pick("无损", "Lossless"),
        AudioProcessingMode.Opus => SelectedAudioOpusBitrate?.Label ?? Texts.Pick("未选择", "Not set"),
        _ => SelectedAudioEac3ToOutputFormat?.Value == AudioEac3ToOutputFormat.Ac3
            ? Texts.Pick("有损", "Lossy")
            : Texts.Pick("无损", "Lossless")
    };

    public string SummaryCard4Label => (SelectedAudioWorkflow?.Value ?? AudioProcessingMode.Ddp) switch
    {
        AudioProcessingMode.Ddp when Deew.MeasureOnly => Texts.Pick("分析目标", "Target"),
        AudioProcessingMode.Ddp => Texts.Pick("模式 / DRC", "Mode / DRC"),
        AudioProcessingMode.Opus => Texts.AudioOpusMappingFamilyHeader,
        _ => Texts.AudioEac3ToAdditionalArgumentsHeader
    };

    public string SummaryCard4Value => (SelectedAudioWorkflow?.Value ?? AudioProcessingMode.Ddp) switch
    {
        AudioProcessingMode.Ddp when Deew.MeasureOnly => Texts.Pick("原始输入", "Original Source"),
        AudioProcessingMode.Ddp => Deew.EffectiveProfile is { } profile
            ? $"{profile.Mode} · {profile.Drc}"
            : "--",
        AudioProcessingMode.Opus => AudioOpusUseMappingFamily1 ? "Family 1" : Texts.Pick("标准", "Standard"),
        _ => string.IsNullOrWhiteSpace(AudioProcessingAdditionalArguments)
            ? Texts.Pick("默认", "Default")
            : AudioProcessingAdditionalArguments.Trim()
    };

    public string SummaryCard4Tooltip => (SelectedAudioWorkflow?.Value ?? AudioProcessingMode.Ddp) == AudioProcessingMode.Ddp
        && !string.IsNullOrWhiteSpace(Deew.EffectiveDescription)
        ? Deew.EffectiveDescription
        : SummaryCard4Value;

    private void NotifyDeewModeVisibility()
    {
        OnPropertyChanged(nameof(DeewDdpModeVisibility));
        OnPropertyChanged(nameof(DeewAtmosModeVisibility));
        OnPropertyChanged(nameof(DeewAc4ImsModeVisibility));
        OnPropertyChanged(nameof(DeewWarningVisibility));
    }

    private void NotifySummaryCards()
    {
        OnPropertyChanged(nameof(SummaryCard1Label));
        OnPropertyChanged(nameof(SummaryCard1Value));
        OnPropertyChanged(nameof(SummaryCard2Label));
        OnPropertyChanged(nameof(SummaryCard2Value));
        OnPropertyChanged(nameof(SummaryCard3Label));
        OnPropertyChanged(nameof(SummaryCard3Value));
        OnPropertyChanged(nameof(SummaryCard4Label));
        OnPropertyChanged(nameof(SummaryCard4Value));
        OnPropertyChanged(nameof(SummaryCard4Tooltip));
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
        Deew.Refresh();
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
