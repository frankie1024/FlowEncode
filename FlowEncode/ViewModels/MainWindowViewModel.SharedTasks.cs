using System;
using System.IO;
using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;
using System.Linq;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace FlowEncode.ViewModels;

public partial class MainWindowViewModel
{
    private readonly TaskCoordinator _tasks;
    private readonly DispatcherQueue _taskDispatcher = DispatcherQueue.GetForCurrentThread();
    private int _sharedActiveCount;
    private bool _enableMcpAutomation;
    internal bool EnableMcpAutomation { get => _enableMcpAutomation; set => SetProperty(ref _enableMcpAutomation, value); }
    internal string McpConnectionConfig => JsonSerializer.Serialize(new
    {
        mcpServers = new { flowencode = new { command = Path.Combine(AppContext.BaseDirectory, "mcp", "FlowEncode.Mcp.exe") } }
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private void SharedTasksChanged(object? sender, EventArgs e)
    {
        var snapshots = _tasks.List();
        var count = snapshots.Count(s => s.State is EncodingJobState.Queued or EncodingJobState.Running);
        if (Interlocked.Exchange(ref _sharedActiveCount, count) == count) return;
        _taskDispatcher.TryEnqueue(() =>
        {
            if (_isDisposed) return;
            foreach (var job in Jobs.Where(j => j.State == EncodingJobState.Queued))
                if (snapshots.FirstOrDefault(s => s.TaskId == job.Request.JobId)?.State == EncodingJobState.Cancelled)
                    job.MarkCancelled(Texts.JobStateLabel(EncodingJobState.Cancelled), "Cancelled by task ID");
            OnPropertyChanged(nameof(HasRunningAppWork));
            RaiseJobStatePropertyChanges();
            TryScheduleQueueCompletionActionAfterSuccessfulQueueDrain();
        });
    }
}
