using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class StructuredAv1anRunner : IAutoCompressionRunner
{
    private readonly LegacyAv1anCliFallbackRunner _legacyRunner;

    public StructuredAv1anRunner(LocalAppPaths paths, IAppSettingsService settingsService)
    {
        _legacyRunner = new LegacyAv1anCliFallbackRunner(paths, settingsService);
    }

    public Task<AutoCompressionResult> RunAsync(
        AutoCompressionRequest request,
        IProgress<AutoCompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Phase 2 scaffold: preserve current behavior through the legacy runner
        // until the protocol-capable fork is wired in.
        return _legacyRunner.RunAsync(request, progress, cancellationToken);
    }

    public string BuildDisplayCommand(AutoCompressionRequest request)
    {
        return _legacyRunner.BuildDisplayCommand(request);
    }

    public void Abort(Guid jobId)
    {
        _legacyRunner.Abort(jobId);
    }
}
