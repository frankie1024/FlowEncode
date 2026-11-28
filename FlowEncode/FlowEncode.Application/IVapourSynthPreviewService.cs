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

    Task<VapourSynthPreviewFrameData> RenderFrameAsync(
        int outputIndex,
        int frameNumber,
        PreviewRenderOptions? renderOptions,
        byte[]? reusablePixelBuffer = null,
        CancellationToken cancellationToken = default) =>
        RenderFrameAsync(outputIndex, frameNumber, reusablePixelBuffer, cancellationToken);

    Task<PreviewTemporalAlignmentResult> AutoAlignTemporalAsync(
        int outputIndexA,
        int outputIndexB,
        int anchorFrameA,
        int baseFrameB,
        int initialOffset = 0,
        int searchRadius = 120,
        bool verifyMultiAnchor = true,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Temporal auto-alignment is not supported by this preview service.");

    Task<PreviewSpatialAlignmentResult> AutoAlignSpatialAsync(
        int outputIndexA,
        int outputIndexB,
        int frameNumberA,
        int frameNumberB,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Spatial auto-alignment is not supported by this preview service.");

    Task<PreviewPixelSampleResult> SamplePixelAsync(
        int outputIndex,
        int frameNumber,
        int x,
        int y,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Pixel sampling is not supported by this preview service.");

    Task<PreviewExportFramesResult> ExportFramesAsync(
        IReadOnlyList<PreviewExportFrameRequestItem> items,
        PreviewRenderOptions? renderOptions = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Batch frame export is not supported by this preview service.");

    Task<PreviewWorstFrameScanResult> ScanWorstFramesAsync(
        int outputIndexA,
        int outputIndexB,
        IReadOnlyList<int> sampleFramesA,
        IReadOnlyList<int> sampleFramesB,
        string metric = "xpsnr",
        int topN = 15,
        double subpixelOffsetX = 0.0,
        double subpixelOffsetY = 0.0,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Worst-frame scan is not supported by this preview service.");

    Task<PreviewAutoCropResult> AutoDetectCropAsync(
        int outputIndex,
        int sampleCount = 12,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Auto-crop detection is not supported by this preview service.");

    Task<PreviewRepairAnalysisResult> AnalyzeRepairsAsync(
        int outputIndex,
        CancellationToken cancellationToken = default,
        string? mediaSource = null,
        bool deep = false,
        string? playlistPath = null) =>
        throw new NotSupportedException("Repair analysis is not supported by this preview service.");

    Task CloseSessionAsync(CancellationToken cancellationToken = default);
}

public sealed record VapourSynthPreviewOpenRequest(
    string? SourceFilePath,
    string DisplayName,
    string Content,
    string WorkingDirectory);

public sealed record VapourSynthPreviewSessionInfo(
    IReadOnlyList<VapourSynthPreviewOutputInfo> Outputs,
    bool HasVsAlign = false,
    bool HasCambi = false,
    bool HasNativeRes = false);

public sealed record VapourSynthPreviewOutputInfo(
    int Index,
    string Name,
    int Width,
    int Height,
    int TotalFrames,
    int FpsNumerator,
    int FpsDenominator,
    string FormatName,
    int BitsPerSample,
    bool IsHdr = false,
    string? HdrTransferName = null);

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

public sealed record PreviewTemporalAlignmentResult(
    int FrameOffset,
    double Confidence,
    bool HasDrift,
    string? DriftSummary,
    double ElapsedMs);

public sealed record PreviewSpatialAlignmentResult(
    double OffsetX,
    double OffsetY,
    double Confidence,
    double ElapsedMs);

public sealed record PreviewPixelSampleResult(
    int X,
    int Y,
    string ColorFamily,
    int BitsPerSample,
    string Plane0Name,
    double Plane0Raw,
    double Plane0Normalized,
    string? Plane1Name,
    double? Plane1Raw,
    double? Plane1Normalized,
    string? Plane2Name,
    double? Plane2Raw,
    double? Plane2Normalized);
