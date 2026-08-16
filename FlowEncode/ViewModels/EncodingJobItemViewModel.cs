using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FlowEncode.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public sealed class EncodingJobItemViewModel : ObservableObject
{
    private const int MaxVisibleLogLength = 200_000;
    private const int RetainedVisibleLogLength = 120_000;

    private EncodingJobState _state;
    private AppLanguage _language;
    private double? _progressFraction;
    private long? _currentFrame;
    private long? _totalFrames;
    private double? _framesPerSecond;
    private double? _bitrateKbps;
    private TimeSpan? _eta;
    private long? _estimatedFileSizeBytes;
    private string _summary;
    private string _detailLine;
    private bool _isSourcePreparation;
    private string _sourcePreparationText;
    private string _log;
    private string _logFilePath;
    private string _displayCommand;
    private bool _isDisplayCommandResolved;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isCancellationRequested;
    private readonly StringBuilder _logBuilder = new();
    private string _lastMeaningfulLogLine;

    public EncodingJobItemViewModel(
        EncodingJobRequest request,
        string displayCommand,
        AppLanguage language = AppLanguage.Chinese)
    {
        Request = request;
        _displayCommand = displayCommand;
        _language = language;
        _state = EncodingJobState.Queued;
        _summary = T("等待编码器空闲", "Waiting for the encoder");
        _detailLine = T("作业已加入队列。", "Job queued.");
        _sourcePreparationText = string.Empty;
        _log = string.Empty;
        _logFilePath = string.Empty;
        _lastMeaningfulLogLine = GetNoMeaningfulLogMessage();
    }

    public EncodingJobRequest Request { get; }

    public Guid JobId => Request.JobId;

    public string Title => Request.Profile.Name;

    public string EncoderLabel => Request.Profile.Kind.ToDisplayName();

    public string SourcePath => Request.SourcePath;

    public string SourceFileName => Path.GetFileName(Request.SourcePath);

    public string OutputPath => Request.OutputPath;

    public string CodecAndCrfLabel => Request.Profile.RateControl == RateControlMode.Crf
        ? $"{Request.Profile.Kind.ToShortName()} crf{Request.Profile.Quality.ToString("0.0##", CultureInfo.InvariantCulture)}"
        : Request.Profile.Kind.ToShortName();

    public string PipelineLabel => Request.PipelineKind switch
    {
        InputPipelineKind.Auto => T("自动", "Auto"),
        InputPipelineKind.Y4mFile => "Y4M",
        InputPipelineKind.RawYuvFile => "Raw YUV",
        InputPipelineKind.VapourSynth => "VapourSynth",
        InputPipelineKind.AviSynth => "AviSynth",
        InputPipelineKind.FfmpegPipe => "FFmpeg",
        _ => T("未知", "Unknown")
    };

    public string DisplayCommand
    {
        get => _displayCommand;
        private set => SetProperty(ref _displayCommand, value);
    }

    public bool IsDisplayCommandResolved
    {
        get => _isDisplayCommandResolved;
        private set => SetProperty(ref _isDisplayCommandResolved, value);
    }

    public EncodingJobState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanRestart));
                OnPropertyChanged(nameof(CanRemove));
                OnPropertyChanged(nameof(IsProgressIndeterminate));
                OnPropertyChanged(nameof(ProgressPercentLabel));
                OnPropertyChanged(nameof(ProgressTelemetryPrimaryLine));
                OnPropertyChanged(nameof(ProgressTelemetrySecondaryLine));
                OnPropertyChanged(nameof(ActiveJobAccentVisibility));
            }
        }
    }

    public double? ProgressFraction
    {
        get => _progressFraction;
        private set
        {
            if (SetProperty(ref _progressFraction, value))
            {
                OnPropertyChanged(nameof(ProgressValue));
                OnPropertyChanged(nameof(ProgressPercentLabel));
                OnPropertyChanged(nameof(ProgressTelemetryLabel));
                OnPropertyChanged(nameof(ProgressTelemetryPrimaryLine));
                OnPropertyChanged(nameof(ProgressTelemetrySecondaryLine));
                OnPropertyChanged(nameof(IsProgressIndeterminate));
            }
        }
    }

    public double ProgressValue => ProgressFraction ?? 0.0;

    public string ProgressPercentLabel => ProgressFraction.HasValue
        ? FormatPercent(ProgressFraction.Value)
        : State == EncodingJobState.Running
            ? "0%"
            : T("未开始", "Not started");

    public string ProgressTelemetryLabel
        => $"{ProgressTelemetryPrimaryLine}{Environment.NewLine}{ProgressTelemetrySecondaryLine}";

    public string ProgressTelemetryPrimaryLine
    {
        get
        {
            var segments = new List<string> { ProgressPercentLabel };

            if (CurrentFrame.HasValue || TotalFrames.HasValue)
            {
                segments.Add($"{CurrentFrame ?? 0}/{TotalFrames?.ToString() ?? "?"} frames");
            }

            if (FramesPerSecond.HasValue)
            {
                segments.Add($"{FramesPerSecond.Value:0.00} fps");
            }

            if (BitrateKbps.HasValue)
            {
                segments.Add($"{BitrateKbps.Value:0.00} kb/s");
            }

            return string.Join("   ", segments);
        }
    }

    public string ProgressTelemetrySecondaryLine
    {
        get
        {
            var etaLabel = $"{T("预计剩余", "eta")} {FormatEta(Eta)}";
            var estimatedSizeLabel = $"{T("预计大小", "est. size")} {FormatByteSize(EstimatedFileSizeBytes)}";

            return $"{etaLabel}   {estimatedSizeLabel}";
        }
    }

    public long? CurrentFrame
    {
        get => _currentFrame;
        private set => SetProgressValue(ref _currentFrame, value);
    }

    public long? TotalFrames
    {
        get => _totalFrames;
        private set => SetProgressValue(ref _totalFrames, value);
    }

    public double? FramesPerSecond
    {
        get => _framesPerSecond;
        private set => SetProgressValue(ref _framesPerSecond, value);
    }

    public double? BitrateKbps
    {
        get => _bitrateKbps;
        private set => SetProgressValue(ref _bitrateKbps, value);
    }

    public TimeSpan? Eta
    {
        get => _eta;
        private set => SetProgressValue(ref _eta, value);
    }

    public long? EstimatedFileSizeBytes
    {
        get => _estimatedFileSizeBytes;
        private set => SetProgressValue(ref _estimatedFileSizeBytes, value);
    }

    public string Summary
    {
        get => _summary;
        private set
        {
            if (SetProperty(ref _summary, value))
            {
                OnPropertyChanged(nameof(ProgressTelemetryLabel));
                OnPropertyChanged(nameof(ProgressTelemetryPrimaryLine));
            }
        }
    }

    public string DetailLine
    {
        get => _detailLine;
        private set
        {
            if (SetProperty(ref _detailLine, value))
            {
                OnPropertyChanged(nameof(ProgressTelemetryLabel));
                OnPropertyChanged(nameof(ProgressTelemetrySecondaryLine));
            }
        }
    }

    public bool IsSourcePreparation
    {
        get => _isSourcePreparation;
        private set
        {
            if (SetProperty(ref _isSourcePreparation, value))
            {
                OnPropertyChanged(nameof(ProgressTelemetryLabel));
                OnPropertyChanged(nameof(ProgressTelemetryPrimaryLine));
                OnPropertyChanged(nameof(ProgressTelemetrySecondaryLine));
                OnPropertyChanged(nameof(IsProgressIndeterminate));
                OnPropertyChanged(nameof(HasSourcePreparationText));
            }
        }
    }

    public string SourcePreparationText
    {
        get => _sourcePreparationText;
        private set
        {
            if (SetProperty(ref _sourcePreparationText, value))
            {
                OnPropertyChanged(nameof(HasSourcePreparationText));
            }
        }
    }

    public bool HasSourcePreparationText => State == EncodingJobState.Running
        && IsSourcePreparation
        && !string.IsNullOrWhiteSpace(SourcePreparationText);

    public string Log
    {
        get => _log;
        private set => SetProperty(ref _log, value);
    }

    public string LogFilePath
    {
        get => _logFilePath;
        private set => SetProperty(ref _logFilePath, value);
    }

    public bool CanStart => State == EncodingJobState.Queued;

    public bool CanCancel => (State is EncodingJobState.Queued or EncodingJobState.Running)
        && !_isCancellationRequested;

    public bool CanRestart => State is EncodingJobState.Completed or EncodingJobState.Failed or EncodingJobState.Cancelled;

    public bool CanRemove => State != EncodingJobState.Running;

    private bool _canMoveUp;
    private bool _canMoveDown;
    private bool _canMoveToTop;
    private bool _canMoveToBottom;

    public bool CanMoveUp
    {
        get => _canMoveUp;
        private set => SetProperty(ref _canMoveUp, value);
    }

    public bool CanMoveDown
    {
        get => _canMoveDown;
        private set => SetProperty(ref _canMoveDown, value);
    }

    public bool CanMoveToTop
    {
        get => _canMoveToTop;
        private set => SetProperty(ref _canMoveToTop, value);
    }

    public bool CanMoveToBottom
    {
        get => _canMoveToBottom;
        private set => SetProperty(ref _canMoveToBottom, value);
    }

    internal void UpdatePositionFlags(bool canMoveUp, bool canMoveDown, bool canMoveToTop, bool canMoveToBottom)
    {
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;
        CanMoveToTop = canMoveToTop;
        CanMoveToBottom = canMoveToBottom;
    }

    public bool IsProgressIndeterminate => State == EncodingJobState.Running
        && !ProgressFraction.HasValue
        && !CurrentFrame.HasValue;

    public Visibility ActiveJobAccentVisibility => State == EncodingJobState.Running
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string StateLabel => State switch
    {
        EncodingJobState.Queued => T("排队中", "Queued"),
        EncodingJobState.Running => T("编码中", "Running"),
        EncodingJobState.Completed => T("已完成", "Completed"),
        EncodingJobState.Failed => T("失败", "Failed"),
        EncodingJobState.Cancelled => T("已取消", "Cancelled"),
        _ => T("未知", "Unknown")
    };

    public void SetLanguage(AppLanguage language)
    {
        if (_language == language)
        {
            return;
        }

        _language = language;
        OnPropertyChanged(nameof(PipelineLabel));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ProgressPercentLabel));
        OnPropertyChanged(nameof(ProgressTelemetryLabel));
        OnPropertyChanged(nameof(ProgressTelemetryPrimaryLine));
        OnPropertyChanged(nameof(ProgressTelemetrySecondaryLine));
    }

    internal void AttachCancellation(CancellationTokenSource cancellationTokenSource)
    {
        _cancellationTokenSource = cancellationTokenSource;
    }

    internal void DetachCancellation()
    {
        _cancellationTokenSource = null;
    }

    public void UpdateDisplayCommand(string displayCommand)
    {
        if (!string.IsNullOrWhiteSpace(displayCommand))
        {
            DisplayCommand = displayCommand;
            IsDisplayCommandResolved = true;
        }
    }

    public bool RequestCancellation()
    {
        if (State != EncodingJobState.Running || _isCancellationRequested)
        {
            return false;
        }

        _isCancellationRequested = true;
        OnPropertyChanged(nameof(CanCancel));
        _cancellationTokenSource?.Cancel();
        return true;
    }

    public void MarkRunning()
    {
        _isCancellationRequested = false;
        OnPropertyChanged(nameof(CanCancel));
        State = EncodingJobState.Running;
        Summary = T("编码器已启动", "Encoder started");
        DetailLine = T("正在等待第一批编码日志...", "Waiting for the first encoder log...");
    }

    public void ApplyProgress(EncodingJobProgress progress)
    {
        if (_isCancellationRequested && progress.State == EncodingJobState.Running)
        {
            return;
        }

        State = progress.State;
        IsSourcePreparation = progress.IsSourcePreparation;
        if (progress.IsSourcePreparation)
        {
            SourcePreparationText = string.IsNullOrWhiteSpace(progress.Summary)
                ? T("源处理中...", "Preparing source...")
                : progress.Summary;
            if (progress.ShouldShowInLog)
            {
                AppendLogLine(progress.DetailLine);
            }

            return;
        }

        SourcePreparationText = string.Empty;
        ProgressFraction = progress.ProgressFraction;
        ApplySnapshot(progress.Snapshot);
        Summary = progress.Summary;
        DetailLine = progress.DetailLine;

        if (progress.ShouldShowInLog)
        {
            AppendLogLine(progress.DetailLine);
        }
    }

    public void ApplyResult(EncodingJobResult result)
    {
        State = result.State;
        IsSourcePreparation = false;
        SourcePreparationText = string.Empty;
        ProgressFraction = result.State == EncodingJobState.Completed ? 1.0 : ProgressFraction;
        Eta = result.State == EncodingJobState.Completed ? TimeSpan.Zero : null;

        if (result.State == EncodingJobState.Completed && TotalFrames.HasValue)
        {
            CurrentFrame = TotalFrames;
        }

        Summary = result.Summary;
        DetailLine = LastMeaningfulLine(result.Log);
        ReplaceLog(result.Log);
        LogFilePath = result.LogFilePath;
    }

    public void MarkCancelled(string summary, string detail)
    {
        State = EncodingJobState.Cancelled;
        IsSourcePreparation = false;
        SourcePreparationText = string.Empty;
        Eta = null;
        Summary = summary;
        DetailLine = detail;
        AppendLogLine(detail);
    }

    public void MarkFailed(string summary, string detail)
    {
        State = EncodingJobState.Failed;
        IsSourcePreparation = false;
        SourcePreparationText = string.Empty;
        Eta = null;
        Summary = summary;
        DetailLine = detail;
        ReplaceLog(detail);
    }

    private void ApplySnapshot(EncodingProgressSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        CurrentFrame = snapshot.CurrentFrame ?? CurrentFrame;
        TotalFrames = snapshot.TotalFrames ?? TotalFrames;
        FramesPerSecond = snapshot.FramesPerSecond ?? FramesPerSecond;
        BitrateKbps = snapshot.BitrateKbps ?? BitrateKbps;
        Eta = snapshot.Eta ?? Eta;
        EstimatedFileSizeBytes = snapshot.EstimatedFileSizeBytes ?? EstimatedFileSizeBytes;
    }

    private void AppendLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var normalized = line.TrimEnd();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (string.Equals(_lastMeaningfulLogLine, normalized, StringComparison.Ordinal))
        {
            return;
        }

        if (_logBuilder.Length > 0)
        {
            _logBuilder.AppendLine();
        }

        _logBuilder.Append(normalized);
        TrimVisibleLogIfNeeded();
        _lastMeaningfulLogLine = normalized;
        Log = _logBuilder.ToString();
    }

    private void ReplaceLog(string log)
    {
        _logBuilder.Clear();

        if (!string.IsNullOrWhiteSpace(log))
        {
            _logBuilder.Append(log);
            TrimVisibleLogIfNeeded();
        }

        Log = _logBuilder.ToString();
        _lastMeaningfulLogLine = LastMeaningfulLine(Log);
    }

    private void TrimVisibleLogIfNeeded()
    {
        if (_logBuilder.Length <= MaxVisibleLogLength)
        {
            return;
        }

        var removeCount = Math.Max(0, _logBuilder.Length - RetainedVisibleLogLength);
        if (removeCount > 0)
        {
            _logBuilder.Remove(0, removeCount);
        }

        var retainedText = _logBuilder.ToString();
        var firstLineBreak = retainedText.IndexOfAny(['\r', '\n']);
        if (firstLineBreak >= 0 && firstLineBreak + 1 < _logBuilder.Length)
        {
            _logBuilder.Remove(0, firstLineBreak + 1);
        }

        var marker = T("[日志过长，已只保留最近输出]", "[Log truncated; only latest output is kept]");
        if (!_logBuilder.ToString().StartsWith(marker, StringComparison.Ordinal))
        {
            _logBuilder.Insert(0, $"{marker}{Environment.NewLine}");
        }
    }

    private string LastMeaningfulLine(string log)
    {
        return log
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(static line => !string.IsNullOrWhiteSpace(line))
            ?? GetNoMeaningfulLogMessage();
    }

    private bool SetProgressValue<T>(ref T field, T value)
    {
        if (SetProperty(ref field, value))
        {
            OnPropertyChanged(nameof(ProgressTelemetryLabel));
            OnPropertyChanged(nameof(ProgressTelemetryPrimaryLine));
            OnPropertyChanged(nameof(ProgressTelemetrySecondaryLine));
            OnPropertyChanged(nameof(IsProgressIndeterminate));
            return true;
        }

        return false;
    }

    private static string FormatPercent(double value)
    {
        return value >= 0.9995 ? "100%" : $"{value * 100.0:0.#}%";
    }

    private static string FormatEta(TimeSpan? eta)
    {
        if (!eta.HasValue)
        {
            return "--:--:--";
        }

        var totalHours = Math.Max(0, (int)Math.Floor(eta.Value.TotalHours));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{totalHours:00}:{eta.Value.Minutes:00}:{eta.Value.Seconds:00}");
    }

    private static string FormatByteSize(long? bytes)
    {
        if (!bytes.HasValue)
        {
            return "------ --";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes.Value;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        var numericPart = unitIndex == 0
            ? $"{size,6:0}"
            : $"{size,6:0.0}";

        return $"{numericPart} {units[unitIndex],-2}";
    }

    private string T(string zh, string en) => _language == AppLanguage.English ? en : zh;

    private string GetNoMeaningfulLogMessage() => T("没有收到编码器日志输出。", "No encoder log output was received.");
}
