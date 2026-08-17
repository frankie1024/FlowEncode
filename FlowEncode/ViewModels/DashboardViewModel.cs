using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FlowEncode.ViewModels;

public sealed class DashboardViewModel : ModuleViewModelBase
{
    public DashboardViewModel(MainWindowViewModel owner)
        : base(owner)
    {
    }

    public AppText Texts => Owner.Texts;

    public Visibility DashboardBluRayDemuxActivityVisibility => Owner.DashboardBluRayDemuxActivityVisibility;

    public Visibility DashboardBluRayDemuxStatusVisibility => Owner.DashboardBluRayDemuxStatusVisibility;

    public Visibility DashboardBluRayDemuxProgressVisibility => Owner.DashboardBluRayDemuxProgressVisibility;

    public string DashboardBluRayDemuxStatusText => Owner.DashboardBluRayDemuxStatusText;

    public Brush DashboardBluRayDemuxStatusBrush => Owner.DashboardBluRayDemuxStatusBrush;

    public double DashboardBluRayDemuxProgressValue => Owner.DashboardBluRayDemuxProgressValue;

    public bool DashboardBluRayDemuxProgressIsIndeterminate => Owner.DashboardBluRayDemuxProgressIsIndeterminate;

    public Visibility DashboardOverviewActivityVisibility => Owner.DashboardOverviewActivityVisibility;

    public Visibility DashboardOverviewStatusVisibility => Owner.DashboardOverviewStatusVisibility;

    public Visibility DashboardOverviewProgressVisibility => Owner.DashboardOverviewProgressVisibility;

    public string DashboardOverviewStatusText => Owner.DashboardOverviewStatusText;

    public Brush DashboardOverviewStatusBrush => Owner.DashboardOverviewStatusBrush;

    public double DashboardOverviewProgressValue => Owner.DashboardOverviewProgressValue;

    public bool DashboardOverviewProgressIsIndeterminate => Owner.DashboardOverviewProgressIsIndeterminate;

    public Visibility DashboardAudioProcessingActivityVisibility => Owner.DashboardAudioProcessingActivityVisibility;

    public Visibility DashboardAudioProcessingStatusVisibility => Owner.DashboardAudioProcessingStatusVisibility;

    public Visibility DashboardAudioProcessingProgressVisibility => Owner.DashboardAudioProcessingProgressVisibility;

    public string DashboardAudioProcessingStatusText => Owner.DashboardAudioProcessingStatusText;

    public Brush DashboardAudioProcessingStatusBrush => Owner.DashboardAudioProcessingStatusBrush;

    public double DashboardAudioProcessingProgressValue => Owner.DashboardAudioProcessingProgressValue;

    public bool DashboardAudioProcessingProgressIsIndeterminate => Owner.DashboardAudioProcessingProgressIsIndeterminate;

    public Visibility DashboardAutoCompressionActivityVisibility => Owner.DashboardAutoCompressionActivityVisibility;

    public Visibility DashboardAutoCompressionStatusVisibility => Owner.DashboardAutoCompressionStatusVisibility;

    public Visibility DashboardAutoCompressionProgressVisibility => Owner.DashboardAutoCompressionProgressVisibility;

    public string DashboardAutoCompressionStatusText => Owner.DashboardAutoCompressionStatusText;

    public Brush DashboardAutoCompressionStatusBrush => Owner.DashboardAutoCompressionStatusBrush;

    public double DashboardAutoCompressionProgressValue => Owner.DashboardAutoCompressionProgressValue;

    public bool DashboardAutoCompressionProgressIsIndeterminate => Owner.DashboardAutoCompressionProgressIsIndeterminate;
}
