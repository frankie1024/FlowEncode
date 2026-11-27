using Microsoft.UI.Xaml;
using FlowEncode.Controls.Shared;

namespace FlowEncode.ViewModels;

public sealed class AutoCompressionStatusViewModel : ModuleViewModelBase
{
    public AutoCompressionStatusViewModel(MainWindowViewModel owner)
        : base(owner)
    {
    }

    public AppText Texts => Owner.Texts;

    public string AutoCompressionStatusText => Owner.AutoCompressionStatusText;

    public TaskPresentationState AutoCompressionPresentationState => Owner.AutoCompressionPresentationState;

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
