using FlowEncode.Domain;

namespace FlowEncode.Infrastructure.Automation;

/// <summary>
/// Admission rules for jobs submitted through automation. Unlike the desktop form, which renames
/// the output when it collides, an automation caller named an exact path, so collisions are
/// rejected instead of silently writing somewhere else.
/// </summary>
public static class AutomationQueuePolicy
{
    /// <param name="request">The job to admit.</param>
    /// <param name="activeRequests">Requests of jobs that are queued or running.</param>
    /// <param name="outputExists">Checks whether a file or directory already exists at a path.</param>
    public static void ValidateEnqueue(
        EncodingJobRequest request,
        IEnumerable<EncodingJobRequest> activeRequests,
        Func<string, bool> outputExists)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(activeRequests);
        ArgumentNullException.ThrowIfNull(outputExists);

        try
        {
            RequestValidation.ValidateEncodingJobRequest(request);
        }
        catch (ArgumentException ex)
        {
            throw new DesktopAutomationException(ex.Message);
        }

        var outputPath = Path.GetFullPath(request.OutputPath);
        if (string.Equals(outputPath, Path.GetFullPath(request.SourcePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new DesktopAutomationException("The output path must differ from the source path.");
        }

        foreach (var active in activeRequests)
        {
            if (active.JobId == request.JobId)
            {
                throw new DesktopAutomationException($"A job with id {request.JobId} already exists.");
            }

            if (string.Equals(Path.GetFullPath(active.OutputPath), outputPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new DesktopAutomationException($"Another active job is already writing to {request.OutputPath}.");
            }
        }

        if (!request.OverwriteOutput && outputExists(outputPath))
        {
            throw new DesktopAutomationException(
                $"Output file already exists: {request.OutputPath}. Pass overwrite=true or choose another outputPath.");
        }
    }
}
