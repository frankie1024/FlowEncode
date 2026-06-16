namespace FlowEncode.Domain;

public sealed record ParallelVideoEncodingRequest(
    Guid JobId,
    string SourcePath,
    string OutputPath,
    EncoderKind EncoderKind,
    double Crf,
    string Preset,
    string Tune,
    string Profile,
    string VideoParameters,
    string UhdParameters,
    int? Workers,
    InputPipelineKind PipelineKind,
    EncoderArchitecture PreferredArchitecture);
