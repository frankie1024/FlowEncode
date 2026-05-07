using System;
using System.Globalization;
using FlowEncode.Infrastructure;

namespace FlowEncode;

internal sealed class PreviewRenderDiagnostics
{
    private const string TraceEnvironmentVariableName = "FLOWENCODE_PREVIEW_TRACE";
    private static readonly TimeSpan SlowFrameThreshold = TimeSpan.FromMilliseconds(40);
    private readonly LocalAppPaths _appPaths;
    private readonly bool _traceAllFrames;

    public PreviewRenderDiagnostics(LocalAppPaths appPaths)
    {
        _appPaths = appPaths;
        _traceAllFrames = IsEnabled(Environment.GetEnvironmentVariable(TraceEnvironmentVariableName));
    }

    public void WriteFrameSuccess(
        int outputIndex,
        int frameNumber,
        bool playbackActive,
        bool cropVisible,
        int sourceWidth,
        int sourceHeight,
        int sourceBytes,
        int displayWidth,
        int displayHeight,
        int displayBytes,
        TimeSpan helperRenderElapsed,
        TimeSpan transportReadElapsed,
        TimeSpan composeElapsed,
        TimeSpan surfaceUpdateElapsed)
    {
        var totalElapsed = helperRenderElapsed + transportReadElapsed + composeElapsed + surfaceUpdateElapsed;
        if (!_traceAllFrames && totalElapsed < SlowFrameThreshold)
        {
            return;
        }

        AppDiagnosticsLog.Write(
            _appPaths,
            nameof(VapourSynthPreviewWindow),
            string.Create(
                CultureInfo.InvariantCulture,
                $"Frame render output={outputIndex} frame={frameNumber} playback={playbackActive} crop={cropVisible} " +
                $"source={sourceWidth}x{sourceHeight}/{sourceBytes}B display={displayWidth}x{displayHeight}/{displayBytes}B " +
                $"helperMs={helperRenderElapsed.TotalMilliseconds:0.###} transportMs={transportReadElapsed.TotalMilliseconds:0.###} " +
                $"composeMs={composeElapsed.TotalMilliseconds:0.###} surfaceMs={surfaceUpdateElapsed.TotalMilliseconds:0.###} " +
                $"totalMs={totalElapsed.TotalMilliseconds:0.###}"));
    }

    public void WriteFrameFailure(
        int outputIndex,
        int frameNumber,
        bool playbackActive,
        bool cropVisible,
        TimeSpan helperRenderElapsed,
        TimeSpan transportReadElapsed,
        TimeSpan composeElapsed,
        TimeSpan surfaceUpdateElapsed,
        Exception exception)
    {
        var totalElapsed = helperRenderElapsed + transportReadElapsed + composeElapsed + surfaceUpdateElapsed;

        AppDiagnosticsLog.Write(
            _appPaths,
            nameof(VapourSynthPreviewWindow),
            string.Create(
                CultureInfo.InvariantCulture,
                $"Frame render failed output={outputIndex} frame={frameNumber} playback={playbackActive} crop={cropVisible} " +
                $"helperMs={helperRenderElapsed.TotalMilliseconds:0.###} transportMs={transportReadElapsed.TotalMilliseconds:0.###} " +
                $"composeMs={composeElapsed.TotalMilliseconds:0.###} surfaceMs={surfaceUpdateElapsed.TotalMilliseconds:0.###} " +
                $"totalMs={totalElapsed.TotalMilliseconds:0.###} {exception.GetType().Name}: {exception.Message}"));
    }

    private static bool IsEnabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim() switch
        {
            "1" => true,
            "true" => true,
            "TRUE" => true,
            "yes" => true,
            "YES" => true,
            "on" => true,
            "ON" => true,
            _ => false
        };
    }
}
