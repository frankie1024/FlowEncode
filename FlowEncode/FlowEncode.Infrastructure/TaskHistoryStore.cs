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
                if (record?.Snapshot is not { } snapshot || record.Workload is not { } workload)
                    throw new InvalidDataException("Task history is missing its receipt or request.");
                workload.Validate();
                if (snapshot.TaskId != workload.TaskId || snapshot.Kind != workload.Kind
                    || !Enum.IsDefined(snapshot.State) || !Enum.IsDefined(snapshot.Origin)
                    || snapshot.Log is null || snapshot.Summary is null || snapshot.Stage is null
                    || snapshot.Artifacts is null || snapshot.Artifacts.Any(a => a is null || string.IsNullOrWhiteSpace(a.Path))
                    || !string.Equals(snapshot.SourcePath, workload.SourcePath, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(snapshot.OutputPath, workload.OutputPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Task history receipt does not match its request.");
                records.Add(record);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                or ArgumentException or InvalidOperationException or NullReferenceException)
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
