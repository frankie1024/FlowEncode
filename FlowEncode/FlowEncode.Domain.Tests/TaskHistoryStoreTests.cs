using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class TaskHistoryStoreTests
{
    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"Snapshot\":null,\"Workload\":null}")]
    [DataRow("{\"Snapshot\":{},\"Workload\":{\"Kind\":0,\"Video\":{}}}")]
    public void IncompleteRecord_IsPreservedAndDoesNotPreventStartup(string invalid)
    {
        using var root = new SharedTaskCoordinatorTests.TestRoot();
        var paths = new LocalAppPaths(root.Path);
        var directory = Path.Combine(paths.DataRootPath, "tasks");
        Directory.CreateDirectory(directory);
        var damaged = Path.Combine(directory, "damaged.json");
        File.WriteAllText(damaged, invalid);
        var store = new TaskHistoryStore(paths);
        var workload = root.Video();
        var first = new TaskCoordinator(new SharedTaskCoordinatorTests.Settings());
        first.Reserve(workload);
        store.Save(new(first.Get(workload.TaskId), workload, null));

        var restarted = new TaskCoordinator(new SharedTaskCoordinatorTests.Settings(), store);

        Assert.AreEqual(1, restarted.List().Count);
        Assert.AreEqual(workload.TaskId, restarted.List().Single().TaskId);
        Assert.AreEqual(invalid, File.ReadAllText(damaged));
    }

    [TestMethod]
    public void MismatchedReceipt_IsNotRestoredAsAnotherTask()
    {
        using var root = new SharedTaskCoordinatorTests.TestRoot();
        var paths = new LocalAppPaths(root.Path);
        var store = new TaskHistoryStore(paths);
        var tasks = new TaskCoordinator(new SharedTaskCoordinatorTests.Settings());
        var request = root.Video();
        tasks.Reserve(request);
        store.Save(new(tasks.Get(request.TaskId), request.WithTaskId(Guid.NewGuid()), "mismatched"));

        Assert.AreEqual(0, store.Load().Count);
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(paths.DataRootPath, "tasks")).Length);
    }
}
