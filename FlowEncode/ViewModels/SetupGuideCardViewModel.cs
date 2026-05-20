using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FlowEncode.Domain;

namespace FlowEncode.ViewModels;

public sealed class SetupGuideCardViewModel : ObservableObject
{
    private string _title;
    private string _description;
    private string _summary;
    private int _readyCount;
    private int _totalCount;
    private bool _hasWarning;
    private bool _isExpanded;

    public SetupGuideCardViewModel(
        AppText texts,
        string title,
        string description,
        string summary,
        IEnumerable<SetupGuideDependencyItemViewModel> items,
        bool isExpanded = false)
    {
        Texts = texts;
        _title = title;
        _description = description;
        _summary = summary;
        _isExpanded = isExpanded;
        Items = [];
        Update(title, description, summary, items);
    }

    public AppText Texts { get; }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Description
    {
        get => _description;
        private set => SetProperty(ref _description, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public ObservableCollection<SetupGuideDependencyItemViewModel> Items { get; }

    public int ReadyCount
    {
        get => _readyCount;
        private set => SetProperty(ref _readyCount, value);
    }

    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    public bool HasWarning
    {
        get => _hasWarning;
        private set => SetProperty(ref _hasWarning, value);
    }

    public string ReadySummary => $"{ReadyCount}/{TotalCount}";

    public void Update(
        string title,
        string description,
        string summary,
        IEnumerable<SetupGuideDependencyItemViewModel> items)
    {
        Title = title;
        Description = description;
        Summary = summary;

        var itemArray = items as SetupGuideDependencyItemViewModel[] ?? items.ToArray();
        ReplaceItems(Items, itemArray);

        ReadyCount = itemArray.Count(item => item.State == ReadinessState.Ready);
        TotalCount = itemArray.Length;
        HasWarning = itemArray.Any(item => item.State != ReadinessState.Ready);
        OnPropertyChanged(nameof(ReadySummary));
    }

    public void UpdateFrom(SetupGuideCardViewModel source)
    {
        Update(source.Title, source.Description, source.Summary, source.Items);
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();

        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
