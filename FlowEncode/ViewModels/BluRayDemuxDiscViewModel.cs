using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using FlowEncode.Domain;

namespace FlowEncode.ViewModels;

public sealed class BluRayDemuxDiscViewModel : ModuleViewModelBase
{
    public BluRayDemuxDiscViewModel(MainWindowViewModel owner)
        : base(owner)
    {
    }

    public AppText Texts => Owner.Texts;

    public ObservableCollection<BluRayDemuxBackendOption> BluRayDemuxBackendOptions => Owner.BluRayDemuxBackendOptions;

    public ObservableCollection<BluRayPlaylistItem> BluRayPlaylists => Owner.BluRayPlaylists;

    public string BluRayDemuxSourcePath
    {
        get => Owner.BluRayDemuxSourcePath;
        set => Owner.BluRayDemuxSourcePath = value;
    }

    public string BluRayDemuxOutputPath
    {
        get => Owner.BluRayDemuxOutputPath;
        set => Owner.BluRayDemuxOutputPath = value;
    }

    public BluRayDemuxBackendOption? SelectedBluRayDemuxBackend
    {
        get => Owner.SelectedBluRayDemuxBackend;
        set => Owner.SelectedBluRayDemuxBackend = value;
    }

    public BluRayPlaylistItem? SelectedBluRayPlaylist
    {
        get => Owner.SelectedBluRayPlaylist;
        set => Owner.SelectedBluRayPlaylist = value;
    }

    public bool CanScanBluRayDisc => Owner.CanScanBluRayDisc;

    public string BluRayDiscSummaryText => Owner.BluRayDiscSummaryText;

    public string BluRayDemuxOutputPreviewText => Owner.BluRayDemuxOutputPreviewText;

    public void InitializeState()
    {
        ReplaceItems(BluRayDemuxBackendOptions, BuildBluRayDemuxBackendOptions());
        SelectedBluRayDemuxBackend = BluRayDemuxBackendOptions.FirstOrDefault();
        Owner.BluRayDemuxStatusText = Texts.BluRayDemuxIdleStatus;
    }

    public void HandleEnvironmentReadinessApplied()
    {
        Owner.RaiseBluRayDemuxEnvironmentPropertyChanges();
    }

    public void ApplyLanguageState()
    {
        var backend = SelectedBluRayDemuxBackend?.Value ?? BluRayDemuxBackend.DgDemux;
        Owner.BeginBluRayDemuxLanguageStateApplication();
        try
        {
            ReplaceItems(BluRayDemuxBackendOptions, BuildBluRayDemuxBackendOptions());
            SelectedBluRayDemuxBackend = BluRayDemuxBackendOptions.FirstOrDefault(option => option.Value == backend) ?? BluRayDemuxBackendOptions.FirstOrDefault();

            if (!Owner.IsBluRayDemuxRunning)
            {
                Owner.SetBluRayDemuxDisplayState(null);
                Owner.BluRayDemuxStatusText = Texts.BluRayDemuxIdleStatus;
            }

            Owner.RaiseBluRayDemuxLanguagePropertyChanges();
        }
        finally
        {
            Owner.EndBluRayDemuxLanguageStateApplication();
        }

        Owner.RefreshBluRayTrackOutputPreviews();
        Owner.RefreshBluRayDemuxCommandPreview();
    }

    public Task ScanBluRayDiscAsync()
    {
        return Owner.ScanBluRayDiscAsync();
    }

    public Task LoadSelectedBluRayPlaylistAsync()
    {
        return Owner.LoadSelectedBluRayPlaylistAsync();
    }

    private IEnumerable<BluRayDemuxBackendOption> BuildBluRayDemuxBackendOptions()
    {
        return
        [
            new BluRayDemuxBackendOption(BluRayDemuxBackend.DgDemux, Texts.BluRayBackendLabel(BluRayDemuxBackend.DgDemux)),
            new BluRayDemuxBackendOption(BluRayDemuxBackend.Eac3To, Texts.BluRayBackendLabel(BluRayDemuxBackend.Eac3To))
        ];
    }
}
