using System.Diagnostics;

namespace FlowEncode.Infrastructure;

internal static class ProcessExecutionMonitor
{
    // A failed output reader must interrupt a process that may now be blocked writing
    // to that pipe. Successful EOF alone does not mean the process has exited.
    internal static async Task WaitForExitAsync(Process process, CancellationToken token, params Task[] readers)
    {
        var exited = process.WaitForExitAsync(token);
        var pending = readers.Append(exited).ToList();
        while (true)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            if (ReferenceEquals(completed, exited)) return;
            pending.Remove(completed);
        }
    }

    internal static Task DrainAsync(CancellationToken token, params Task[] readers) =>
        Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10), token);
}
