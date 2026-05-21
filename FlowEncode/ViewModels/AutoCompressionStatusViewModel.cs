using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FlowEncode.ViewModels;

public sealed class AutoCompressionStatusViewModel : ModuleViewModelBase
{
    public AutoCompressionStatusViewModel(MainWindowViewModel owner)
        : base(owner)
    {
    }

    public AppText Texts => Owner.Texts;

    public Brush AutoCompressionStatusPanelBorderBrush => Owner.AutoCompressionStatusPanelBorderBrush;

    public Brush AutoCompressionProgressTrackBrush => Owner.AutoCompressionProgressTrackBrush;

    public Brush AutoCompressionProgressBorderBrush => Owner.AutoCompressionProgressBorderBrush;

    public Brush AutoCompressionProgressFillBrush => Owner.AutoCompressionProgressFillBrush;

    public string AutoCompressionStatusText => Owner.AutoCompressionStatusText;

    public double AutoCompressionProgressPercent => Owner.AutoCompressionProgressPercent;

    public double AutoCompressionProgressValue => Owner.AutoCompressionProgressValue;

    public bool AutoCompressionProgressIsIndeterminate => Owner.AutoCompressionProgressIsIndeterminate;

    public string AutoCompressionProgressLabel => Owner.AutoCompressionProgressLabel;

    public string AutoCompressionProgressPercentText => Owner.AutoCompressionProgressPercentText;

    public string AutoCompressionProgressHint => Owner.AutoCompressionProgressHint;

    public Visibility AutoCompressionProgressHintVisibility => Owner.AutoCompressionProgressHintVisibility;

    public string AutoCompressionCommandLine => Owner.AutoCompressionCommandLine;

    public string AutoCompressionLog => Owner.AutoCompressionLog;
}
