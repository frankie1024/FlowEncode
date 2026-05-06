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
    private static readonly string[] WarningTokens = ["warning", "warn"];
    private static readonly string[] ErrorTokens = ["error", "failed", "exception", "fatal"];
    private readonly StringBuilder _stderrBuffer = new();
    private IPreviewFrameTransportSession? _frameTransportSession;
    private IVapourSynthPreviewHostSession? _hostSession;
    private string? _activeSessionPath;
    private int _commandCounter;
    private bool _stderrTracebackActive;
    private bool _disposed;

    public event EventHandler<VapourSynthPreviewLogEventArgs>? LogEmitted;

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
        string? sessionRootPath = null)
    {
        _appPaths = appPaths;
        _hostFactory = hostFactory;
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

        await _stateLock.WaitAsync(cancellationToken);

        try
        {
            ThrowIfDisposed();
            var normalizedSourceFilePath = NormalizeAbsolutePath(request.SourceFilePath);
            var normalizedWorkingDirectory = NormalizeWorkingDirectory(request.WorkingDirectory, normalizedSourceFilePath);

            await CloseHostCoreAsync();

            var sessionPath = CreateSessionDirectory();
            _activeSessionPath = sessionPath;

            var startupPath = Path.Combine(sessionPath, "startup.json");
            var startupPayload = new StartupRequestDto(
                normalizedSourceFilePath,
                request.DisplayName,
                request.Content ?? string.Empty,
                normalizedWorkingDirectory);
            var startupJson = JsonSerializer.Serialize(startupPayload, JsonOptions);
            await File.WriteAllTextAsync(startupPath, startupJson, new UTF8Encoding(false), cancellationToken);

            _hostSession = await _hostFactory.StartAsync(
                normalizedWorkingDirectory,
                startupPath,
                HostSession_StderrLineReceived,
                cancellationToken);

            var response = await ReadResponseAsync(cancellationToken);
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
                    output.BitsPerSample))
                .OrderBy(static output => output.Index)
                .ToArray()
                ?? [];

            if (outputs.Length == 0)
            {
                throw new InvalidOperationException("The script did not expose any video outputs.");
            }

            _frameTransportSession = _frameTransportFactory.CreateSession(GetMaximumFrameByteSize(outputs));
            return new VapourSynthPreviewSessionInfo(outputs);
        }
        catch (Exception)
        {
            await CloseHostCoreAsync();
            throw;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<VapourSynthPreviewFrameData> RenderFrameAsync(
        int outputIndex,
        int frameNumber,
        byte[]? reusablePixelBuffer = null,
        CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);

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
            var command = new FrameCommandDto(
                "renderFrame",
                requestId,
                outputIndex,
                frameNumber,
                transportDescriptor.Kind,
                transportDescriptor.SharedMemoryName,
                transportDescriptor.CapacityBytes);
            var commandJson = JsonSerializer.Serialize(command, JsonOptions);
            await _hostSession.WriteLineAsync(commandJson, cancellationToken);

            var response = await ReadResponseAsync(cancellationToken);
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
                cancellationToken);
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
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task CloseSessionAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);

        try
        {
            await CloseHostCoreAsync();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseHostCoreAsync().GetAwaiter().GetResult();
        _stateLock.Dispose();
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
        if (_hostSession is null)
        {
            throw new InvalidOperationException("Preview helper output is unavailable.");
        }

        while (true)
        {
            var line = await _hostSession.ReadLineAsync(cancellationToken);
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
                    await session.WriteLineAsync(closeJson);

                    using var timeoutCancellationTokenSource = new CancellationTokenSource(_closeTimeout);
                    await session.WaitForExitAsync(timeoutCancellationTokenSource.Token);
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
                        await session.WaitForExitAsync(killTimeoutCancellationTokenSource.Token);
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
        ObjectDisposedException.ThrowIf(_disposed, nameof(VapourSynthPreviewService));
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
        int SharedMemoryCapacity);

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
    }

    private sealed class HostPropertyDto
    {
        public string? Key { get; set; }

        public string? Value { get; set; }
    }
}
