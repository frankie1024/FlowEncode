using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using FlowEncode.Controls.Shared;

namespace FlowEncode.ViewModels;

public sealed class AudioProcessingStatusViewModel : ModuleViewModelBase
{
    public AudioProcessingStatusViewModel(MainWindowViewModel owner)
        : base(owner)
    {
    }

    public AppText Texts => Owner.Texts;

    public string AudioProcessingStatusText => Owner.AudioProcessingStatusText;

    public TaskPresentationState AudioProcessingPresentationState => Owner.AudioProcessingPresentationState;

    public Brush AudioProcessingProgressTrackBrush => Owner.AudioProcessingProgressTrackBrush;

    public Brush AudioProcessingProgressBorderBrush => Owner.AudioProcessingProgressBorderBrush;

    public Brush AudioProcessingProgressFillBrush => Owner.AudioProcessingProgressFillBrush;

    public double AudioProcessingProgressValue => Owner.AudioProcessingProgressValue;

    public string AudioProcessingProgressPercentText => Owner.AudioProcessingProgressPercentText;

    public string AudioProcessingProgressSecondaryText => Owner.AudioProcessingProgressSecondaryText;

    public Visibility AudioProcessingProgressSecondaryVisibility => Owner.AudioProcessingProgressSecondaryVisibility;

    public string AudioProcessingCommandLine => Owner.AudioProcessingCommandLine;

    public string AudioProcessingLog => Owner.AudioProcessingLog;
    public string AudioResultPaths => Owner.AudioResultPaths;
    public bool CanOpenAudioMedia => Owner.CanOpenAudioMedia;
    public bool CanOpenAudioDiagnostics => Owner.CanOpenAudioDiagnostics;
    public bool CanOpenAudioRetained => Owner.CanOpenAudioRetained;
    public string? ArtifactPath(string kind) => kind switch { "media" => Owner.AudioMediaPath, "logs" => Owner.AudioDiagnosticsPath, _ => Owner.AudioRetainedPath };
}
