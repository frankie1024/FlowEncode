using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FlowEncode.Application;

public interface IVapourSynthPreviewService : IDisposable
{
    event EventHandler<VapourSynthPreviewLogEventArgs>? LogEmitted;

    bool HasActiveSession { get; }

    Task<VapourSynthPreviewSessionInfo> OpenSessionAsync(
        VapourSynthPreviewOpenRequest request,
        CancellationToken cancellationToken = default);

    Task<VapourSynthPreviewFrameData> RenderFrameAsync(
        int outputIndex,
        int frameNumber,
        byte[]? reusablePixelBuffer = null,
        CancellationToken cancellationToken = default);

    Task CloseSessionAsync(CancellationToken cancellationToken = default);
}

public sealed record VapourSynthPreviewOpenRequest(
    string? SourceFilePath,
    string DisplayName,
    string Content,
    string WorkingDirectory);

public sealed record VapourSynthPreviewSessionInfo(
    IReadOnlyList<VapourSynthPreviewOutputInfo> Outputs);

public sealed record VapourSynthPreviewOutputInfo(
    int Index,
    string Name,
    int Width,
    int Height,
    int TotalFrames,
    int FpsNumerator,
    int FpsDenominator,
    string FormatName,
    int BitsPerSample);

public sealed record VapourSynthPreviewFrameData(
    int OutputIndex,
    int FrameNumber,
    int Width,
    int Height,
    byte[] Pixels,
    string? FrameType,
    IReadOnlyList<VapourSynthPreviewFrameProperty> Properties,
    TimeSpan HelperRenderElapsed,
    TimeSpan TransportReadElapsed);

public sealed record VapourSynthPreviewFrameProperty(
    string Key,
    string Value);
