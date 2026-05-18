using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FlowEncode.Domain;

namespace FlowEncode.ViewModels;

public sealed class SetupGuideCardViewModel
{
    public SetupGuideCardViewModel(
        string title,
        string description,
        string summary,
        IEnumerable<SetupGuideDependencyItemViewModel> items)
    {
        Title = title;
        Description = description;
        Summary = summary;
        Items = new ObservableCollection<SetupGuideDependencyItemViewModel>(items);
        var itemList = Items.ToList();
        var readyCount = itemList.Count(i => i.State == ReadinessState.Ready);
        ReadyCount = readyCount;
        TotalCount = itemList.Count;
        HasWarning = itemList.Any(i => i.State != ReadinessState.Ready);
    }

    public string Title { get; }

    public string Description { get; }

    public string Summary { get; }

    public ObservableCollection<SetupGuideDependencyItemViewModel> Items { get; }

    public int ReadyCount { get; }

    public int TotalCount { get; }

    public bool HasWarning { get; }

    public string ReadySummary => $"{ReadyCount}/{TotalCount}";
}
