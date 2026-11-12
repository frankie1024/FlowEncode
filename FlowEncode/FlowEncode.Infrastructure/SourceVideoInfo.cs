namespace FlowEncode.Infrastructure;

internal sealed record SourceVideoInfo(
    int Width,
    int Height,
    long? TotalFrames,
    int BitDepth,
    int? FpsNumerator,
    int? FpsDenominator,
    string PixelFormat,
    string? ColorRange = null,
    string? ColorPrimaries = null,
    string? ColorTransfer = null,
    string? ColorMatrix = null,
    string? ChromaLocation = null,
    string? MasteringDisplay = null,
    string? ContentLightLevel = null,
    bool IsFrameCountEstimated = false)
{
    public double? FramesPerSecond =>
        FpsNumerator is > 0 && FpsDenominator is > 0
            ? FpsNumerator.Value / (double)FpsDenominator.Value
            : null;
}
