using FlowEncode.Domain;

namespace FlowEncode.Application;

public interface IEncodingJobRunner
{
    Task<EncodingJobResult> RunAsync(
        EncodingJobRequest request,
        IProgress<EncodingJobProgress>? progress = null,
        CancellationToken cancellationToken = default);

    string BuildDisplayCommand(EncodingJobRequest request);

    Task ValidateSourceAsync(
        string sourcePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    void AbortJob(Guid jobId);
}
