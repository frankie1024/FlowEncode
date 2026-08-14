using System.Collections.ObjectModel;
using System.Threading.Tasks;
using FlowEncode.Controls.Shared;
using FlowEncode.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FlowEncode.ViewModels;

public sealed class BluRayDemuxTaskViewModel : ModuleViewModelBase
{
    public BluRayDemuxTaskViewModel(MainWindowViewModel owner)
        : base(owner)
    {
    }

    public AppText Texts => Owner.Texts;

    public ObservableCollection<BluRayTrackItemViewModel> BluRayTracks => Owner.BluRayTracks;

    public string BluRayPlaylistSummaryText => Owner.BluRayPlaylistSummaryText;

    public string BluRaySelectedTrackSummary => Owner.BluRaySelectedTrackSummary;

    public bool CanSelectAllBluRayTracks => Owner.CanSelectAllBluRayTracks;

    public bool CanInvertBluRayTrackSelection => Owner.CanInvertBluRayTrackSelection;

    public bool CanStartBluRayDemux => Owner.CanStartBluRayDemux;

    public bool CanCancelBluRayDemux => Owner.CanCancelBluRayDemux;

    public bool CanClearBluRayDemuxTask => Owner.CanClearBluRayDemuxTask;

    public Brush BluRayDemuxStatusPanelBorderBrush => Owner.BluRayDemuxStatusPanelBorderBrush;

    public TaskPresentationState BluRayDemuxPresentationState => Owner.BluRayDemuxPresentationState;

    public Brush BluRayDemuxProgressTrackBrush => Owner.BluRayDemuxProgressTrackBrush;

    public Brush BluRayDemuxProgressBorderBrush => Owner.BluRayDemuxProgressBorderBrush;

    public Brush BluRayDemuxProgressFillBrush => Owner.BluRayDemuxProgressFillBrush;

    public double BluRayDemuxProgressValue => Owner.BluRayDemuxProgressValue;

    public string BluRayDemuxProgressPercentText => Owner.BluRayDemuxProgressPercentText;

    public string BluRayDemuxProgressSecondaryText => Owner.BluRayDemuxProgressSecondaryText;

    public Visibility BluRayDemuxProgressSecondaryVisibility => Owner.BluRayDemuxProgressSecondaryVisibility;

    public string BluRayDemuxCommandLine => Owner.BluRayDemuxCommandLine;

    public string BluRayDemuxLog => Owner.BluRayDemuxLog;

    public string? ValidateBluRayDemuxForStart()
    {
        return Owner.ValidateBluRayDemuxForStart();
    }

    public Task<string?> StartBluRayDemuxAsync()
    {
        return Owner.StartBluRayDemuxAsync();
    }

    public void CancelBluRayDemux()
    {
        Owner.CancelBluRayDemux();
    }

    public void ClearBluRayDemuxTask()
    {
        Owner.ClearBluRayDemuxTask();
    }

    public void SelectAllBluRayTracks()
    {
        Owner.SelectAllBluRayTracks();
    }

    public void InvertBluRayTrackSelection()
    {
        Owner.InvertBluRayTrackSelection();
    }

    public void ToggleBluRayTrackSelection(BluRayTrackItemViewModel? track)
    {
        Owner.ToggleBluRayTrackSelection(track);
    }
}
