using FlowEncode.Domain;

namespace FlowEncode.Application;

public interface IAudioSourceInfoService
{
    Task<AudioSourceInfo?> ProbeAsync(string sourcePath, CancellationToken cancellationToken = default);

    Task<AudioSourceInfo?> ProbeAsync(string sourcePath, int audioTrackIndex, CancellationToken cancellationToken = default)
        => audioTrackIndex == 0 ? ProbeAsync(sourcePath, cancellationToken) : throw new NotSupportedException("This probe does not support audio track selection.");
}
