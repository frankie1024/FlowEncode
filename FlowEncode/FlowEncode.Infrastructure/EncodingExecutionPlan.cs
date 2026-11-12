using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal sealed record ProcessCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string DisplayCommand);

internal sealed record EncodingExecutionStep(
    ProcessCommand EncoderCommand,
    ProcessCommand? SourceCommand,
    string DisplayCommand,
    int StageIndex,
    int StageCount);

internal sealed record EncodingExecutionPlan(
    IReadOnlyList<EncodingExecutionStep> Steps,
    string DisplayCommand,
    EncoderKind Kind,
    long? TotalFrames,
    double? SourceFramesPerSecond,
    IReadOnlyList<string>? CleanupPaths = null,
    VideoOutputContract? OutputContract = null);
