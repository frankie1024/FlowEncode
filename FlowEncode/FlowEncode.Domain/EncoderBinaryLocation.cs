namespace FlowEncode.Domain;

public sealed record EncoderBinaryLocation(
    EncoderKind Kind,
    EncoderArchitecture Architecture,
    string LocalPath,
    string ExpectedFileName,
    bool Exists,
    bool CanExecute,
    string DetectedVersion,
    string StatusLabel)
{
    public string ArchitectureLabel => Architecture.ToDisplayName();

    public string ImportToken => $"{Kind}|{Architecture}";
}
