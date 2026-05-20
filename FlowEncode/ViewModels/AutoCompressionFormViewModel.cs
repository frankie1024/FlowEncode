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

    public double AutoCompressionTargetVmaf
    {
        get => Owner.AutoCompressionTargetVmaf;
        set => Owner.AutoCompressionTargetVmaf = value;
    }

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

    public string AutoCompressionOutputPreviewText => Owner.AutoCompressionOutputPreviewText;

    public bool CanStartAutoCompression => Owner.CanStartAutoCompression;

    public bool CanCancelAutoCompression => Owner.CanCancelAutoCompression;

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
