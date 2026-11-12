using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowEncode.Controls.Shared;

// Functional shared task view; visual redesign remains a separate release task.
public sealed class TaskCenterView : UserControl
{
    private readonly TaskCoordinator _tasks = App.GetService<TaskCoordinator>();
    private readonly MainWindowViewModel _owner;
    private readonly ObservableCollection<TaskRow> _rows = [];
    private readonly ListView _list = new() { DisplayMemberPath = nameof(TaskRow.Title), SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Header = "" };
    private readonly Button _cancel = new();
    private readonly TextBlock _heading = new() { FontSize = 24 };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherQueueTimer _timer;
    private Guid? _shownId;
    private TaskSnapshot? _shown;

    public TaskCenterView(MainWindowViewModel owner)
    {
        _owner = owner;
        var grid = new Grid { Padding = new Thickness(24), RowSpacing = 12 };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(_heading);
        Grid.SetRow(_hint, 1); grid.Children.Add(_hint);
        var content = new Grid { ColumnSpacing = 16, RowSpacing = 12 };
        content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new() { Height = new GridLength(0) });
        _list.ItemsSource = _rows;
        _list.SelectionChanged += (_, _) => RefreshSelection();
        content.Children.Add(_list); Grid.SetColumn(_details, 1); content.Children.Add(_details);
        content.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 760;
            content.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            content.RowDefinitions[1].Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetRow(_details, narrow ? 1 : 0); Grid.SetColumn(_details, narrow ? 0 : 1);
        };
        Grid.SetRow(content, 2); grid.Children.Add(content);
        _cancel.Click += (_, _) =>
        {
            if (_list.SelectedItem is TaskRow selected) _tasks.Cancel(selected.Snapshot.TaskId);
            Refresh();
        };
        Grid.SetRow(_cancel, 3); grid.Children.Add(_cancel);
        Content = grid;
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(300);
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { _timer.Start(); Refresh(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        _heading.Text = _owner.Texts.NavTaskCenter;
        _hint.Text = _owner.Texts.Pick("手动任务和 AI 任务共用并发上限。选择任务可查看 ID、日志和产物；取消只影响所选任务。", "Manual and AI tasks share the concurrency limit. Select a task to inspect its ID, logs and artifacts. Cancel affects only the selected task.");
        _cancel.Content = _owner.Texts.Pick("取消所选任务", "Cancel selected task");
        var snapshots = _tasks.List();
        foreach (var snapshot in snapshots.Reverse())
        {
            var existing = _rows.FirstOrDefault(row => row.Snapshot.TaskId == snapshot.TaskId);
            if (existing is null) _rows.Insert(0, new(snapshot, _owner.Texts));
            else existing.Update(snapshot, _owner.Texts);
        }
        if (_list.SelectedItem is null && _rows.Count > 0) _list.SelectedIndex = 0;
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (_list.SelectedItem is not TaskRow row) { _cancel.IsEnabled = false; return; }
        var s = row.Snapshot;
        _cancel.IsEnabled = s.State is EncodingJobState.Queued or EncodingJobState.Running;
        if (_shownId == s.TaskId && ReferenceEquals(_shown, s)) return;
        _shownId = s.TaskId; _shown = s;
        _details.Header = s.TaskId.ToString();
        _details.Text = $"{row.Title}\n\n{s.SourcePath}\n→ {s.OutputPath}\n\n{s.Summary}\n\n"
            + string.Join(Environment.NewLine, s.Artifacts.Select(a => $"{a.Kind}: {a.Path}"))
            + "\n\n" + s.Log;
    }

    private sealed class TaskRow(TaskSnapshot snapshot, AppText texts) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public TaskSnapshot Snapshot { get; private set; } = snapshot;
        private AppText _texts = texts;
        public string Title => $"{Snapshot.Kind} · {Snapshot.Origin} · {_texts.JobStateLabel(Snapshot.State)}"
            + (Snapshot.ProgressFraction is double p ? $" · {p:P0}" : "")
            + $"\n{Path.GetFileName(Snapshot.SourcePath)}\n{Snapshot.Summary}";
        public void Update(TaskSnapshot value, AppText text)
        {
            if (ReferenceEquals(value, Snapshot) && ReferenceEquals(text, _texts)) return;
            Snapshot = value; _texts = text; OnPropertyChanged(nameof(Title));
        }
    }
}
