using System.Collections.ObjectModel;
using System.Threading.Tasks;
using FlowEncode.Domain;

namespace FlowEncode.ViewModels;

public sealed class AutoCompressionFormViewModel : ModuleViewModelBase
{
    public AutoCompressionFormViewModel(MainWindowViewModel owner)
        : base(owner)
    {
    }

    public AppText Texts => Owner.Texts;

    public ObservableCollection<EncoderOption> EncoderOptions => Owner.EncoderOptions;

    public string AutoCompressionSourcePath
    {
        get => Owner.AutoCompressionSourcePath;
        set => Owner.AutoCompressionSourcePath = value;
    }

    public string AutoCompressionOutputPath
    {
        get => Owner.AutoCompressionOutputPath;
        set => Owner.AutoCompressionOutputPath = value;
    }

    public EncoderOption? SelectedAutoEncoder
    {
        get => Owner.SelectedAutoEncoder;
        set => Owner.SelectedAutoEncoder = value;
    }

    public ObservableCollection<AutoCompressionMetricOption> AutoCompressionMetricOptions => Owner.AutoCompressionMetricOptions;

    public AutoCompressionMetricOption? SelectedAutoCompressionMetricOption
    {
        get => Owner.SelectedAutoCompressionMetricOption;
        set => Owner.SelectedAutoCompressionMetricOption = value;
    }

    public string AutoCompressionVideoParameters
    {
        get => Owner.AutoCompressionVideoParameters;
        set => Owner.AutoCompressionVideoParameters = value;
    }

    public string AutoCompressionBackendArguments
    {
        get => Owner.AutoCompressionBackendArguments;
        set => Owner.AutoCompressionBackendArguments = value;
    }

    public double AutoCompressionTargetVmaf
    {
        get => Owner.AutoCompressionTargetVmaf;
        set => Owner.AutoCompressionTargetVmaf = value;
    }

    public double AutoCompressionTargetScoreMinimum => Owner.AutoCompressionTargetScoreMinimum;

    public double AutoCompressionTargetScoreMaximum => Owner.AutoCompressionTargetScoreMaximum;

    public AutoCompressionMetric AutoCompressionMetric
    {
        get => Owner.AutoCompressionMetric;
        set => Owner.AutoCompressionMetric = value;
    }

    public double AutoCompressionProbes
    {
        get => Owner.AutoCompressionProbes;
        set => Owner.AutoCompressionProbes = value;
    }

    public double AutoCompressionWorkers
    {
        get => Owner.AutoCompressionWorkers;
        set => Owner.AutoCompressionWorkers = value;
    }

    public double AutoCompressionProbingRate
    {
        get => Owner.AutoCompressionProbingRate;
        set => Owner.AutoCompressionProbingRate = value;
    }

    public string AutoCompressionProbeResolution
    {
        get => Owner.AutoCompressionProbeResolution;
        set => Owner.AutoCompressionProbeResolution = value;
    }

    public ObservableCollection<StringChoiceOption> AutoCompressionProbingStatisticOptions => Owner.AutoCompressionProbingStatisticOptions;

    public StringChoiceOption? SelectedAutoCompressionProbingStatisticOption
    {
        get => Owner.SelectedAutoCompressionProbingStatisticOption;
        set => Owner.SelectedAutoCompressionProbingStatisticOption = value;
    }

    public ObservableCollection<StringChoiceOption> AutoCompressionInterpolationMethodOptions => Owner.AutoCompressionInterpolationMethodOptions;

    public StringChoiceOption? SelectedAutoCompressionInterpolationMethodOption
    {
        get => Owner.SelectedAutoCompressionInterpolationMethodOption;
        set => Owner.SelectedAutoCompressionInterpolationMethodOption = value;
    }

    public string AutoCompressionOutputPreviewText => Owner.AutoCompressionOutputPreviewText;

    public string AutoCompressionMetricGuidanceText => Owner.AutoCompressionMetricGuidanceText;

    public bool CanStartAutoCompression => Owner.CanStartAutoCompression;

    public bool CanCancelAutoCompression => Owner.CanCancelAutoCompression;

    public bool IsAutoCompressionAdvancedOptionsExpanded
    {
        get => Owner.IsAutoCompressionAdvancedOptionsExpanded;
        set => Owner.IsAutoCompressionAdvancedOptionsExpanded = value;
    }

    public string? ValidateAutoCompressionForStart(out string? existingOutputPath)
    {
        return Owner.ValidateAutoCompressionForStart(out existingOutputPath);
    }

    public Task<string?> StartAutoCompressionAsync()
    {
        return Owner.StartAutoCompressionAsync();
    }

    public void CancelAutoCompression()
    {
        Owner.CancelAutoCompression();
    }
}
