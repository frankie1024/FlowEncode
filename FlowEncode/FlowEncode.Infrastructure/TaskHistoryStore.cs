using System.Diagnostics;
using System.Text.Json;
using FlowEncode.Application;

namespace FlowEncode.Infrastructure;

public sealed class TaskHistoryStore : ITaskHistoryStore
{
    private readonly string _directory;
    private readonly object _gate = new();
    public TaskHistoryStore(LocalAppPaths paths) => _directory = Path.Combine(paths.DataRootPath, "tasks");
    public IReadOnlyList<TaskHistoryRecord> Load()
    {
        if (!Directory.Exists(_directory)) return [];
        var records = new List<TaskHistoryRecord>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
            try
            {
                var record = JsonSerializer.Deserialize<TaskHistoryRecord>(File.ReadAllBytes(path));
                if (record is not null) { record.Workload.Validate(); records.Add(record); }
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException)
            { Debug.WriteLine("Task history preserved but unreadable: " + path + " — " + ex.Message); }
        return records;
    }
    public void Save(TaskHistoryRecord record)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, record.Snapshot.TaskId.ToString("N") + ".json");
            var temporary = path + ".tmp";
            try { File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(record)); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
