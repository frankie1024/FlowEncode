using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class VapourSynthPreviewService : IVapourSynthPreviewService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly LocalAppPaths _appPaths;
    private readonly IPreviewFrameTransportFactory _frameTransportFactory;
    private readonly IVapourSynthPreviewHostFactory _hostFactory;
    private readonly string _sessionRootPath;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly TimeSpan _closeTimeout = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _killWaitTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _responseTimeout;
    private static readonly string[] WarningTokens = ["warning", "warn"];
    private static readonly string[] ErrorTokens = ["error", "failed", "exception", "fatal"];
    private readonly StringBuilder _stderrBuffer = new();
    private IPreviewFrameTransportSession? _frameTransportSession;
    private IVapourSynthPreviewHostSession? _hostSession;
    private string? _activeSessionPath;
    private int _commandCounter;
    private int _sessionOpenRequestCount;
    private int _activeSessionState;
    private int _disposeStarted;
    private bool _stderrTracebackActive;
    private bool _disposed;

    public event EventHandler<VapourSynthPreviewLogEventArgs>? LogEmitted;

    public bool HasActiveSession => Volatile.Read(ref _sessionOpenRequestCount) > 0
        || Volatile.Read(ref _activeSessionState) != 0;

    public VapourSynthPreviewService(
        IToolProbeService toolProbeService,
        LocalAppPaths appPaths)
        : this(appPaths, new ProcessVapourSynthPreviewHostFactory(toolProbeService), new SharedMemoryPreviewFrameTransportFactory())
    {
    }

    internal VapourSynthPreviewService(
        LocalAppPaths appPaths,
        IVapourSynthPreviewHostFactory hostFactory,
        IPreviewFrameTransportFactory? frameTransportFactory = null,
        string? sessionRootPath = null,
        TimeSpan? responseTimeout = null)
    {
        _appPaths = appPaths;
        _hostFactory = hostFactory;
        _responseTimeout = responseTimeout ?? TimeSpan.FromMinutes(2);
        _frameTransportFactory = frameTransportFactory ?? new SharedMemoryPreviewFrameTransportFactory();
        _sessionRootPath = string.IsNullOrWhiteSpace(sessionRootPath)
            ? Path.Combine(appPaths.DataRootPath, "vapoursynth-preview")
            : sessionRootPath;
        Directory.CreateDirectory(_sessionRootPath);
    }

    public async Task<VapourSynthPreviewSessionInfo> OpenSessionAsync(
        VapourSynthPreviewOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Interlocked.Increment(ref _sessionOpenRequestCount);

        try
        {
            return await OpenSessionCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _sessionOpenRequestCount);
        }
    }

    private async Task<VapourSynthPreviewSessionInfo> OpenSessionCoreAsync(
        VapourSynthPreviewOpenRequest request,
        CancellationToken cancellationToken)
    {
        // The host factory may run tool probes while this lock is held.
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        try
        {
            ThrowIfDisposed();
            var normalizedSourceFilePath = NormalizeAbsolutePath(request.SourceFilePath);
            var normalizedWorkingDirectory = NormalizeWorkingDirectory(request.WorkingDirectory, normalizedSourceFilePath);

            await CloseHostCoreAsync().ConfigureAwait(false);

            var sessionPath = CreateSessionDirectory();
            _activeSessionPath = sessionPath;

            var startupPath = Path.Combine(sessionPath, "startup.json");
            var startupPayload = new StartupRequestDto(
                normalizedSourceFilePath,
                request.DisplayName,
                request.Content ?? string.Empty,
                normalizedWorkingDirectory);
            var startupJson = JsonSerializer.Serialize(startupPayload, JsonOptions);
            await File.WriteAllTextAsync(startupPath, startupJson, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            _hostSession = await _hostFactory.StartAsync(
                normalizedWorkingDirectory,
                startupPath,
                HostSession_StderrLineReceived,
                cancellationToken).ConfigureAwait(false);

            var response = await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(response.Type, "ready", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(GetResponseErrorMessage(response)
                    ?? "Preview helper did not finish initialization.");
            }

            var outputs = response.Outputs?
                .Select(static output => new VapourSynthPreviewOutputInfo(
                    output.Index,
                    string.IsNullOrWhiteSpace(output.Name) ? $"Output {output.Index}" : output.Name,
                    output.Width,
                    output.Height,
                    output.TotalFrames,
                    output.FpsNumerator,
                    output.FpsDenominator,
                    string.IsNullOrWhiteSpace(output.FormatName) ? "Unknown" : output.FormatName,
                    output.BitsPerSample,
                    output.IsHdr,
                    string.IsNullOrWhiteSpace(output.HdrTransferName) ? null : output.HdrTransferName))
                .OrderBy(static output => output.Index)
                .ToArray()
                ?? [];

            if (outputs.Length == 0)
            {
                throw new InvalidOperationException("The script did not expose any video outputs.");
            }

            _frameTransportSession = _frameTransportFactory.CreateSession(GetMaximumFrameByteSize(outputs));
            Volatile.Write(ref _activeSessionState, 1);
            return new VapourSynthPreviewSessionInfo(
                outputs,
                response.HasVsAlign,
                response.HasCambi,
                response.HasNativeRes);
        }
        catch (Exception)
        {
            await CloseHostCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public Task<VapourSynthPreviewFrameData> RenderFrameAsync(
        int outputIndex,
        int frameNumber,
        byte[]? reusablePixelBuffer = null,
        CancellationToken cancellationToken = default) =>
        RenderFrameAsync(outputIndex, frameNumber, null, reusablePixelBuffer, cancellationToken);

    public async Task<VapourSynthPreviewFrameData> RenderFrameAsync(
        int outputIndex,
        int frameNumber,
        PreviewRenderOptions? renderOptions,
        byte[]? reusablePixelBuffer = null,
        CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            if (_hostSession is null || string.IsNullOrWhiteSpace(_activeSessionPath))
            {
                throw new InvalidOperationException("Preview session is not open.");
            }

            if (_frameTransportSession is null)
            {
                throw new InvalidOperationException("Preview frame transport is not ready.");
            }

            if (_hostSession.HasExited)
            {
                throw new InvalidOperationException(BuildHostFailureMessage(
                    "Preview helper exited unexpectedly before rendering a frame."));
            }

            var requestId = Interlocked.Increment(ref _commandCounter);
            var transportDescriptor = _frameTransportSession.Descriptor;
            var helperRenderStopwatch = Stopwatch.StartNew();
            var effectiveOptions = renderOptions ?? PreviewRenderOptions.Default;
            var command = new FrameCommandDto(
                "renderFrame",
                requestId,
                outputIndex,
                frameNumber,
                transportDescriptor.Kind,
                transportDescriptor.SharedMemoryName,
                transportDescriptor.CapacityBytes,
                effectiveOptions.IsDefault ? null : effectiveOptions.ToPlaneModeWireValue(),
                effectiveOptions.ToneMapHdr ? true : null,
                effectiveOptions.ToneMapHdr ? effectiveOptions.ToneMapTargetNits : null,
                effectiveOptions.SeparateFields ? true : null,
                effectiveOptions.FftSpectrum ? true : null,
                Math.Abs(effectiveOptions.SubpixelOffsetX) >= 0.0001 ? effectiveOptions.SubpixelOffsetX : null,
                Math.Abs(effectiveOptions.SubpixelOffsetY) >= 0.0001 ? effectiveOptions.SubpixelOffsetY : null);
            var commandJson = JsonSerializer.Serialize(command, JsonOptions);
            await _hostSession.WriteLineAsync(commandJson, cancellationToken).ConfigureAwait(false);

            var response = await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
            helperRenderStopwatch.Stop();
            if (!string.Equals(response.Type, "frame", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(GetResponseErrorMessage(response)
                    ?? "Preview helper returned an invalid frame response.");
            }

            if (response.RequestId != requestId)
            {
                throw new InvalidOperationException("Preview helper returned a mismatched frame response.");
            }

            var expectedPixelBytes = checked(response.Width * response.Height * 4);
            if (response.PixelBytes <= 0 || response.PixelBytes != expectedPixelBytes)
            {
                throw new InvalidOperationException("Preview helper did not produce a frame buffer.");
            }

            var transportReadStopwatch = Stopwatch.StartNew();
            var pixels = await _frameTransportSession.ReadFrameAsync(
                response.PixelBytes,
                reusablePixelBuffer,
                cancellationToken).ConfigureAwait(false);
            transportReadStopwatch.Stop();

            var properties = response.Properties?
                .Select(static item => new VapourSynthPreviewFrameProperty(
                    item.Key ?? string.Empty,
                    item.Value ?? string.Empty))
                .ToArray()
                ?? [];

            return new VapourSynthPreviewFrameData(
                response.OutputIndex,
                response.FrameNumber,
                response.Width,
                response.Height,
                pixels,
                response.FrameType,
                properties,
                helperRenderStopwatch.Elapsed,
                transportReadStopwatch.Elapsed);
        }
        catch
        {
            await CloseHostCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<PreviewTemporalAlignmentResult> AutoAlignTemporalAsync(
        int outputIndexA,
        int outputIndexB,
        int anchorFrameA,
        int baseFrameB,
        int initialOffset = 0,
        int searchRadius = 120,
        bool verifyMultiAnchor = true,
        CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureActiveHost();
            var requestId = Interlocked.Increment(ref _commandCounter);
            var command = new TemporalAlignCommandDto(
                "autoAlignTemporal",
                requestId,
                outputIndexA,
                outputIndexB,
                anchorFrameA,
                baseFrameB,
                initialOffset,
                searchRadius,
                verifyMultiAnchor);
            var response = await SendCommandAndReadResponseAsync(command, requestId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(response.Type, "temporalAlignment", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(GetResponseErrorMessage(response)
                    ?? "Preview helper failed to compute temporal alignment.");
            }

            return new PreviewTemporalAlignmentResult(
                response.FrameOffset,
                response.Confidence,
                response.HasDrift,
                response.DriftSummary,
                response.ElapsedMs);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<PreviewSpatialAlignmentResult> AutoAlignSpatialAsync(
        int outputIndexA,
        int outputIndexB,
        int frameNumberA,
        int frameNumberB,
        CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureActiveHost();
            var requestId = Interlocked.Increment(ref _commandCounter);
            var command = new SpatialAlignCommandDto(
                "autoAlignSpatial",
                requestId,
                outputIndexA,
                outputIndexB,
                frameNumberA,
                frameNumberB);
            var response = await SendCommandAndReadResponseAsync(command, requestId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(response.Type, "spatialAlignment", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(GetResponseErrorMessage(response)
                    ?? "Preview helper failed to compute spatial alignment.");
            }

            return new PreviewSpatialAlignmentResult(
                response.OffsetX,
                response.OffsetY,
                response.Confidence,
                response.ElapsedMs);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<PreviewPixelSampleResult> SamplePixelAsync(
        int outputIndex,
        int frameNumber,
        int x,
        int y,
        CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureActiveHost();
            var requestId = Interlocked.Increment(ref _commandCounter);
            var command = new SamplePixelCommandDto(
                "samplePixel",
                requestId,
                outputIndex,
                frameNumber,
                x,
                y);
            var response = await SendCommandAndReadResponseAsync(command, requestId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(response.Type, "pixelSample", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(GetResponseErrorMessage(response)
                    ?? "Preview helper failed to sample pixel.");
            }

            return new PreviewPixelSampleResult(
                response.X,
                response.Y,
                string.IsNullOrWhiteSpace(response.ColorFamily) ? "UNKNOWN" : response.ColorFamily,
                response.BitsPerSample,
                string.IsNullOrWhiteSpace(response.Plane0Name) ? "P0" : response.Plane0Name,
                response.Plane0Raw,
                response.Plane0Normalized,
                response.Plane1Name,
                response.Plane1Raw,
                response.Plane1Normalized,
                response.Plane2Name,
                response.Plane2Raw,
                response.Plane2Normalized);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<PreviewExportFramesResult> ExportFramesAsync(
        IReadOnlyList<PreviewExportFrameRequestItem> items,
        PreviewRenderOptions? renderOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return new PreviewExportFramesResult(0, 0, "none");
        }

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureActiveHost();
            var requestId = Interlocked.Increment(ref _commandCounter);
            var opts = renderOptions ?? PreviewRenderOptions.Default;
            var command = new ExportFramesCommandDto(
                "exportFrames",
                requestId,
                items.Select(static i => new ExportFrameItemDto(
                    i.OutputIndex,
                    i.TargetFrameNumber,
                    i.FilePath,
                    i.SubpixelOffsetX,
                    i.SubpixelOffsetY)).ToArray(),
                opts.ToPlaneModeWireValue(),
                opts.ToneMapHdr,
                opts.ToneMapTargetNits,
                opts.SeparateFields);
            var response = await SendCommandAndReadResponseAsync(command, requestId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(response.Type, "exportFrames", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(GetResponseErrorMessage(response)
                    ?? "Preview helper failed to export frames.");
            }

            return new PreviewExportFramesResult(
                response.ExportedCount,
                response.ElapsedMs,
                string.IsNullOrWhiteSpace(response.EncoderUsed) ? "png" : response.EncoderUsed);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<PreviewWorstFrameScanResult> ScanWorstFramesAsync(
        int outputIndexA,
        int outputIndexB,
        IReadOnlyList<int> sampleFramesA,
        IReadOnlyList<int> sampleFramesB,
        string metric = "xpsnr",
        int topN = 15,
        double subpixelOffsetX = 0.0,
        double subpixelOffsetY = 0.0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sampleFramesA);
        ArgumentNullException.ThrowIfNull(sampleFramesB);
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureActiveHost();
            var requestId = Interlocked.Increment(ref _commandCounter);
            var command = new ScanWorstFramesCommandDto(
                "scanWorstFrames",
                requestId,
                outputIndexA,
                outputIndexB,
                sampleFramesA.ToArray(),
                sampleFramesB.ToArray(),
                string.IsNullOrWhiteSpace(metric) ? "xpsnr" : metric,
                Math.Clamp(topN, 1, 50),
                subpixelOffsetX,
                subpixelOffsetY);
            var response = await SendCommandAndReadResponseAsync(command, requestId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(response.Type, "worstFrames", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(GetResponseErrorMessage(response)
                    ?? "Preview helper failed to scan worst frames.");
            }

            var entries = response.WorstFrames?
                .Select(static item => new PreviewWorstFrameEntry(
                    item.FrameNumber,
                    item.SecondaryFrameNumber,
                    item.Score,
                    item.FrameType))
                .ToArray()
                ?? [];

            return new PreviewWorstFrameScanResult(
                string.IsNullOrWhiteSpace(response.MetricUsed) ? metric : response.MetricUsed,
                string.IsNullOrWhiteSpace(response.BackendUsed) ? "cpu" : response.BackendUsed,
                response.SampledCount,
                response.MedianScore,
                response.P10Score,
                response.MinScore,
                response.ElapsedMs,
                entries);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<PreviewAutoCropResult> AutoDetectCropAsync(
        int outputIndex,
        int sampleCount = 12,
        CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureActiveHost();
            var requestId = Interlocked.Increment(ref _commandCounter);
            var command = new AutoCropCommandDto(
                "autoDetectCrop",
                requestId,
                outputIndex,
                Math.Clamp(sampleCount, 3, 30));
            var response = await SendCommandAndReadResponseAsync(command, requestId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(response.Type, "autoCrop", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(GetResponseErrorMessage(response)
                    ?? "Preview helper failed to detect crop bounds.");
            }

            return new PreviewAutoCropResult(
                response.CropLeft,
                response.CropTop,
                response.CropRight,
                response.CropBottom,
                response.SampledCount,
                response.ElapsedMs);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<HostResponseDto> SendCommandAndReadResponseAsync<TCommand>(
        TCommand command,
        int requestId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _hostSession!.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions), cancellationToken).ConfigureAwait(false);
            var response = await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
            if (response.RequestId != requestId)
            {
                throw new InvalidOperationException($"Preview helper returned a mismatched response (expected {requestId}, got {response.RequestId}).");
            }

            return response;
        }
        catch
        {
            await CloseHostCoreAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void EnsureActiveHost()
    {
        if (_hostSession is null || string.IsNullOrWhiteSpace(_activeSessionPath))
        {
            throw new InvalidOperationException("Preview session is not open.");
        }

        if (_hostSession.HasExited)
        {
            throw new InvalidOperationException(BuildHostFailureMessage(
                "Preview helper exited unexpectedly."));
        }
    }

    public async Task CloseSessionAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await CloseHostCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _stateLock.Wait();
        try
        {
            _disposed = true;
            CloseHostCoreAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _stateLock.Release();
            _stateLock.Dispose();
        }
    }

    private void HostSession_StderrLineReceived(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_stderrBuffer)
        {
            if (_stderrBuffer.Length > 0)
            {
                _stderrBuffer.AppendLine();
            }

            _stderrBuffer.Append(line);
        }

        EmitLog(
            ClassifyHelperStderrLine(line),
            "helper",
            line);
    }

    private async Task<HostResponseDto> ReadResponseAsync(CancellationToken cancellationToken)
    {
        using var responseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseCancellation.CancelAfter(_responseTimeout);
        try
        {
            return await ReadResponseCoreAsync(responseCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(BuildHostFailureMessage("Preview helper response timed out / 预览辅助进程响应超时。"));
        }
    }

    private async Task<HostResponseDto> ReadResponseCoreAsync(CancellationToken cancellationToken)
    {
        if (_hostSession is null)
        {
            throw new InvalidOperationException("Preview helper output is unavailable.");
        }

        while (true)
        {
            var line = await _hostSession.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidOperationException(BuildHostFailureMessage(
                    "Preview helper returned no response."));
            }

            var response = JsonSerializer.Deserialize<HostResponseDto>(line, JsonOptions);
            if (response is null)
            {
                throw new InvalidOperationException("Preview helper returned invalid JSON.");
            }

            if (string.Equals(response.Type, "log", StringComparison.OrdinalIgnoreCase))
            {
                EmitLog(
                    MapLogLevel(response.Level),
                    string.IsNullOrWhiteSpace(response.Source) ? "helper" : response.Source,
                    response.Message ?? string.Empty);
                continue;
            }

            return response;
        }
    }

    private async Task CloseHostCoreAsync()
    {
        var session = _hostSession;
        if (session is not null)
        {
            try
            {
                if (!session.HasExited)
                {
                    var closeJson = JsonSerializer.Serialize(new CloseCommandDto("close"), JsonOptions);
                    using var timeoutCancellationTokenSource = new CancellationTokenSource(_closeTimeout);
                    await session.WriteLineAsync(closeJson, timeoutCancellationTokenSource.Token).ConfigureAwait(false);
                    await session.WaitForExitAsync(timeoutCancellationTokenSource.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                WriteDiagnostic($"Preview helper graceful shutdown failed for PID {session.ProcessId}. {ex.GetType().Name}: {ex.Message}");

                try
                {
                    if (!session.HasExited)
                    {
                        session.Kill(entireProcessTree: true);
                        using var killTimeoutCancellationTokenSource = new CancellationTokenSource(_killWaitTimeout);
                        await session.WaitForExitAsync(killTimeoutCancellationTokenSource.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception killEx)
                {
                    WriteDiagnostic($"Preview helper forced shutdown failed for PID {session.ProcessId}. {killEx.GetType().Name}: {killEx.Message}");
                }
            }

            session.Dispose();
        }

        _hostSession = null;
        Volatile.Write(ref _activeSessionState, 0);
        _frameTransportSession?.Dispose();
        _frameTransportSession = null;
        _commandCounter = 0;
        _stderrTracebackActive = false;

        if (!string.IsNullOrWhiteSpace(_activeSessionPath))
        {
            BestEffortCleanup.DeleteDirectoryRecursively(
                _activeSessionPath,
                $"preview session directory '{_activeSessionPath}'",
                WriteDiagnostic);
            _activeSessionPath = null;
        }

        lock (_stderrBuffer)
        {
            _stderrBuffer.Clear();
        }
    }

    private string CreateSessionDirectory()
    {
        var sessionPath = Path.Combine(_sessionRootPath, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionPath);
        return sessionPath;
    }

    private static string NormalizeWorkingDirectory(string? workingDirectory, string? sourceFilePath)
    {
        var normalizedWorkingDirectory = NormalizeAbsolutePath(workingDirectory);
        if (!string.IsNullOrWhiteSpace(normalizedWorkingDirectory) && Directory.Exists(normalizedWorkingDirectory))
        {
            return normalizedWorkingDirectory;
        }

        var sourceDirectory = !string.IsNullOrWhiteSpace(sourceFilePath)
            ? Path.GetDirectoryName(sourceFilePath)
            : null;
        if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory))
        {
            return sourceDirectory;
        }

        return AppContext.BaseDirectory;
    }

    private static string? NormalizeAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private void WriteDiagnostic(string message)
    {
        AppDiagnosticsLog.Write(_appPaths, nameof(VapourSynthPreviewService), message);
    }

    private string BuildHostFailureMessage(string fallbackMessage)
    {
        lock (_stderrBuffer)
        {
            return _stderrBuffer.Length == 0
                ? fallbackMessage
                : $"{fallbackMessage} {_stderrBuffer}";
        }
    }

    private static string? GetResponseErrorMessage(HostResponseDto response)
    {
        return string.IsNullOrWhiteSpace(response.Message)
            ? null
            : response.Message;
    }

    private void EmitLog(
        VapourSynthPreviewLogLevel level,
        string source,
        string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var normalizedMessage = message
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "preview" : source.Trim();

        foreach (var line in normalizedMessage.Split('\n'))
        {
            var trimmedLine = ConsoleOutputLineNormalizer.Normalize(line);
            if (string.IsNullOrWhiteSpace(trimmedLine))
            {
                continue;
            }

            LogEmitted?.Invoke(
                this,
                new VapourSynthPreviewLogEventArgs(
                    new VapourSynthPreviewLogEntry(
                        DateTimeOffset.Now,
                        level,
                        normalizedSource,
                        trimmedLine)));
        }
    }

    private static VapourSynthPreviewLogLevel MapLogLevel(string? level)
    {
        return level?.Trim().ToLowerInvariant() switch
        {
            "warning" or "warn" => VapourSynthPreviewLogLevel.Warning,
            "error" => VapourSynthPreviewLogLevel.Error,
            _ => VapourSynthPreviewLogLevel.Information
        };
    }

    private VapourSynthPreviewLogLevel ClassifyHelperStderrLine(string line)
    {
        var normalizedLine = line.Trim();
        if (normalizedLine.Length == 0)
        {
            return VapourSynthPreviewLogLevel.Information;
        }

        if (normalizedLine.Contains("traceback", StringComparison.OrdinalIgnoreCase))
        {
            _stderrTracebackActive = true;
            return VapourSynthPreviewLogLevel.Error;
        }

        if (_stderrTracebackActive)
        {
            return VapourSynthPreviewLogLevel.Error;
        }

        if (ContainsAny(normalizedLine, WarningTokens))
        {
            return VapourSynthPreviewLogLevel.Warning;
        }

        if (ContainsAny(normalizedLine, ErrorTokens))
        {
            return VapourSynthPreviewLogLevel.Error;
        }

        return VapourSynthPreviewLogLevel.Information;
    }

    private static bool ContainsAny(string line, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (line.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed || Volatile.Read(ref _disposeStarted) != 0, nameof(VapourSynthPreviewService));
    }

    private static int GetMaximumFrameByteSize(IReadOnlyList<VapourSynthPreviewOutputInfo> outputs)
    {
        var maxSize = 0;
        foreach (var output in outputs)
        {
            var outputBytes = checked(output.Width * output.Height * 4);
            if (outputBytes > maxSize)
            {
                maxSize = outputBytes;
            }
        }

        return Math.Max(1, maxSize);
    }

    private sealed record StartupRequestDto(
        string? SourceFilePath,
        string DisplayName,
        string Content,
        string WorkingDirectory);

    private sealed record FrameCommandDto(
        string Command,
        int RequestId,
        int OutputIndex,
        int FrameNumber,
        string TransportKind,
        string SharedMemoryName,
        int SharedMemoryCapacity,
        string? PlaneMode = null,
        bool? ToneMapHdr = null,
        double? ToneMapTargetNits = null,
        bool? SeparateFields = null,
        bool? FftSpectrum = null,
        double? SubpixelOffsetX = null,
        double? SubpixelOffsetY = null);

    private sealed record TemporalAlignCommandDto(
        string Command,
        int RequestId,
        int OutputIndexA,
        int OutputIndexB,
        int AnchorFrameA,
        int BaseFrameB,
        int InitialOffset,
        int SearchRadius,
        bool VerifyMultiAnchor);

    private sealed record SpatialAlignCommandDto(
        string Command,
        int RequestId,
        int OutputIndexA,
        int OutputIndexB,
        int FrameNumberA,
        int FrameNumberB);

    private sealed record SamplePixelCommandDto(
        string Command,
        int RequestId,
        int OutputIndex,
        int FrameNumber,
        int X,
        int Y);

    private sealed record ExportFrameItemDto(
        int OutputIndex,
        int TargetFrameNumber,
        string FilePath,
        double SubpixelOffsetX,
        double SubpixelOffsetY);

    private sealed record ExportFramesCommandDto(
        string Command,
        int RequestId,
        ExportFrameItemDto[] Items,
        string PlaneMode,
        bool ToneMapHdr,
        double ToneMapTargetNits,
        bool SeparateFields);

    private sealed record ScanWorstFramesCommandDto(
        string Command,
        int RequestId,
        int OutputIndexA,
        int OutputIndexB,
        int[] SampleFramesA,
        int[] SampleFramesB,
        string Metric,
        int TopN,
        double SubpixelOffsetX,
        double SubpixelOffsetY);

    private sealed record AutoCropCommandDto(
        string Command,
        int RequestId,
        int OutputIndex,
        int SampleCount);

    private sealed record CloseCommandDto(string Command);

    private sealed class HostResponseDto
    {
        public string? Type { get; set; }

        public string? Level { get; set; }

        public string? Source { get; set; }

        public int RequestId { get; set; }

        public string? Message { get; set; }

        public int OutputIndex { get; set; }

        public int FrameNumber { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public int PixelBytes { get; set; }

        public string? FrameType { get; set; }

        public List<HostOutputDto>? Outputs { get; set; }

        public List<HostPropertyDto>? Properties { get; set; }

        public bool HasVsAlign { get; set; }

        public bool HasCambi { get; set; }

        public bool HasNativeRes { get; set; }

        public int FrameOffset { get; set; }

        public double Confidence { get; set; }

        public bool HasDrift { get; set; }

        public string? DriftSummary { get; set; }

        public double ElapsedMs { get; set; }

        public double OffsetX { get; set; }

        public double OffsetY { get; set; }

        public int X { get; set; }

        public int Y { get; set; }

        public string? ColorFamily { get; set; }

        public int BitsPerSample { get; set; }

        public string? Plane0Name { get; set; }

        public double Plane0Raw { get; set; }

        public double Plane0Normalized { get; set; }

        public string? Plane1Name { get; set; }

        public double? Plane1Raw { get; set; }

        public double? Plane1Normalized { get; set; }

        public string? Plane2Name { get; set; }

        public double? Plane2Raw { get; set; }

        public double? Plane2Normalized { get; set; }

        public int ExportedCount { get; set; }

        public string? EncoderUsed { get; set; }

        public string? MetricUsed { get; set; }

        public string? BackendUsed { get; set; }

        public int SampledCount { get; set; }

        public double MedianScore { get; set; }

        public double P10Score { get; set; }

        public double MinScore { get; set; }

        public List<HostWorstFrameDto>? WorstFrames { get; set; }

        public int CropLeft { get; set; }

        public int CropTop { get; set; }

        public int CropRight { get; set; }

        public int CropBottom { get; set; }
    }

    private sealed class HostWorstFrameDto
    {
        public int FrameNumber { get; set; }

        public int SecondaryFrameNumber { get; set; }

        public double Score { get; set; }

        public string? FrameType { get; set; }
    }

    private sealed class HostOutputDto
    {
        public int Index { get; set; }

        public string? Name { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public int TotalFrames { get; set; }

        public int FpsNumerator { get; set; }

        public int FpsDenominator { get; set; }

        public string? FormatName { get; set; }

        public int BitsPerSample { get; set; }

        public bool IsHdr { get; set; }

        public string? HdrTransferName { get; set; }
    }

    private sealed class HostPropertyDto
    {
        public string? Key { get; set; }

        public string? Value { get; set; }
    }
}
