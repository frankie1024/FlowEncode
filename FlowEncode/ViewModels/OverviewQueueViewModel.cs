using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using FlowEncode.Domain;
using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public sealed class OverviewQueueViewModel : ModuleViewModelBase
{
    public OverviewQueueViewModel(MainWindowViewModel owner)
        : base(owner)
    {
    }

    public AppText Texts => Owner.Texts;

    public string QueueSummary => Owner.QueueSummary;

    public ObservableCollection<StringChoiceOption> ConcurrentEncodingJobOptions => Owner.ConcurrentEncodingJobOptions;

    public StringChoiceOption? SelectedConcurrentEncodingJobOption
    {
        get => Owner.SelectedConcurrentEncodingJobOption;
        set => Owner.SelectedConcurrentEncodingJobOption = value;
    }

    public ObservableCollection<StringChoiceOption> QueueCompletionActionOptions => Owner.QueueCompletionActionOptions;

    public StringChoiceOption? SelectedQueueCompletionActionOption
    {
        get => Owner.SelectedQueueCompletionActionOption;
        set => Owner.SelectedQueueCompletionActionOption = value;
    }

    public Visibility QueueSelectionCommandBarVisibility => Owner.QueueSelectionCommandBarVisibility;

    public bool CanSelectAllQueueJobs => Owner.CanSelectAllQueueJobs;

    public bool CanInvertQueueSelection => Owner.CanInvertQueueSelection;

    public bool CanStartSelectedJobs => Owner.CanStartSelectedJobs;

    public bool CanCancelSelectedJobs => Owner.CanCancelSelectedJobs;

    public bool CanDeleteSelectedJobs => Owner.CanDeleteSelectedJobs;

    public string QueueSelectionStatusText => Owner.QueueSelectionStatusText;

    public ObservableCollection<EncodingJobItemViewModel> Jobs => Owner.Jobs;

    public Visibility EmptyQueueVisibility => Owner.EmptyQueueVisibility;

    public EncodingJobItemViewModel? SelectedJob => Owner.SelectedJob;

    public string SelectedJobSummary => Owner.SelectedJobSummary;

    public Visibility SelectedJobSourcePreparationVisibility => Owner.SelectedJobSourcePreparationVisibility;

    public string SelectedJobSourcePreparationText => Owner.SelectedJobSourcePreparationText;

    public double SelectedJobProgressValue => Owner.SelectedJobProgressValue;

    public string SelectedJobProgressPercentText => Owner.SelectedJobProgressPercentText;

    public string SelectedJobFramesText => Owner.SelectedJobFramesText;

    public string SelectedJobFpsText => Owner.SelectedJobFpsText;

    public string SelectedJobBitrateText => Owner.SelectedJobBitrateText;

    public string SelectedJobEtaText => Owner.SelectedJobEtaText;

    public string SelectedJobEstimatedSizeText => Owner.SelectedJobEstimatedSizeText;

    public string SelectedJobCommandText => Owner.SelectedJobCommandText;

    public bool CanCopySelectedJobCommand => Owner.CanCopySelectedJobCommand;

    public string SelectedJobLogText => Owner.SelectedJobLogText;

    public int SelectedQueueJobCount => Owner.SelectedQueueJobCount;

    public int SelectedQueuedJobCount => Owner.SelectedQueuedJobCount;

    public int SelectedRunningJobCount => Owner.SelectedRunningJobCount;

    public int SelectedCancelableQueueJobCount => Owner.SelectedCancelableQueueJobCount;

    public int SelectedRemovableQueueJobCount => Owner.SelectedRemovableQueueJobCount;

    public Task CancelJobAsync(EncodingJobItemViewModel? job)
    {
        return Owner.CancelJobAsync(job);
    }

    public string? StartSelectedJobsNow()
    {
        return Owner.StartSelectedJobsNow();
    }

    public string? CancelSelectedJobs()
    {
        return Owner.CancelSelectedJobs();
    }

    public string? RemoveSelectedJobs()
    {
        return Owner.RemoveSelectedJobs();
    }

    public Task<string?> RestartJobAsync(EncodingJobItemViewModel? job)
    {
        return Owner.RestartJobAsync(job);
    }

    public string? RemoveJob(EncodingJobItemViewModel? job)
    {
        return Owner.RemoveJob(job);
    }

    public string? PrioritizeJob(EncodingJobItemViewModel? job)
    {
        return Owner.PrioritizeJob(job);
    }

    public string? StartJobNow(EncodingJobItemViewModel? job)
    {
        return Owner.StartJobNow(job);
    }

    public string? MoveJobUp(EncodingJobItemViewModel? job)
    {
        return Owner.MoveJobUp(job);
    }

    public string? MoveJobDown(EncodingJobItemViewModel? job)
    {
        return Owner.MoveJobDown(job);
    }

    public string? MoveJobToTop(EncodingJobItemViewModel? job)
    {
        return Owner.MoveJobToTop(job);
    }

    public string? MoveJobToBottom(EncodingJobItemViewModel? job)
    {
        return Owner.MoveJobToBottom(job);
    }

    public void UpdateSelectedQueueJobs(IEnumerable<EncodingJobItemViewModel> selectedJobs)
    {
        Owner.UpdateSelectedQueueJobs(selectedJobs);
    }

    public void SelectJob(EncodingJobItemViewModel? job)
    {
        Owner.SelectJob(job);
    }
}
