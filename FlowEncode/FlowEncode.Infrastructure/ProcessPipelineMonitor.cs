namespace FlowEncode.Infrastructure;

internal enum PipeCopyCompletion
{
    Completed,
    BrokenPipe
}

internal enum PipelineFirstCompletion
{
    ProducerExited,
    ConsumerExited,
    PipeBroken
}

internal static class ProcessPipelineMonitor
{
    public static async Task<PipeCopyCompletion> ObservePipeCopyAsync(Task copyTask)
    {
        try
        {
            await copyTask;
            return PipeCopyCompletion.Completed;
        }
        catch (IOException)
        {
            return PipeCopyCompletion.BrokenPipe;
        }
        catch (ObjectDisposedException)
        {
            return PipeCopyCompletion.BrokenPipe;
        }
    }

    public static async Task<PipelineFirstCompletion> WaitForFirstCompletionAsync(
        Task producerExitTask,
        Task consumerExitTask,
        Task<PipeCopyCompletion> pipeCopyTask)
    {
        var firstCompletedTask = await Task.WhenAny(
            producerExitTask,
            consumerExitTask,
            pipeCopyTask);

        if (ReferenceEquals(firstCompletedTask, pipeCopyTask))
        {
            if (await pipeCopyTask == PipeCopyCompletion.BrokenPipe)
                return PipelineFirstCompletion.PipeBroken;

            // EOF can arrive before the producer's failing exit is observed. Keep
            // watching both processes so the caller can terminate a waiting encoder.
            firstCompletedTask = await Task.WhenAny(producerExitTask, consumerExitTask);
        }

        if (ReferenceEquals(firstCompletedTask, producerExitTask))
        {
            return PipelineFirstCompletion.ProducerExited;
        }

        return PipelineFirstCompletion.ConsumerExited;
    }
}
