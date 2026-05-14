using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public sealed class DashboardViewModel : ModuleViewModelBase
{
    public DashboardViewModel(MainWindowViewModel owner)
        : base(owner)
    {
    }

    public AppText Texts => Owner.Texts;

    public Visibility DashboardBluRayDemuxActivityVisibility => Owner.DashboardBluRayDemuxActivityVisibility;

    public double DashboardBluRayDemuxProgressValue => Owner.DashboardBluRayDemuxProgressValue;

    public bool DashboardBluRayDemuxProgressIsIndeterminate => Owner.DashboardBluRayDemuxProgressIsIndeterminate;

    public Visibility DashboardOverviewActivityVisibility => Owner.DashboardOverviewActivityVisibility;

    public double DashboardOverviewProgressValue => Owner.DashboardOverviewProgressValue;

    public bool DashboardOverviewProgressIsIndeterminate => Owner.DashboardOverviewProgressIsIndeterminate;

    public Visibility DashboardAudioProcessingActivityVisibility => Owner.DashboardAudioProcessingActivityVisibility;

    public double DashboardAudioProcessingProgressValue => Owner.DashboardAudioProcessingProgressValue;

    public bool DashboardAudioProcessingProgressIsIndeterminate => Owner.DashboardAudioProcessingProgressIsIndeterminate;

    public Visibility DashboardAutoCompressionActivityVisibility => Owner.DashboardAutoCompressionActivityVisibility;

    public double DashboardAutoCompressionProgressValue => Owner.DashboardAutoCompressionProgressValue;

    public bool DashboardAutoCompressionProgressIsIndeterminate => Owner.DashboardAutoCompressionProgressIsIndeterminate;
}
