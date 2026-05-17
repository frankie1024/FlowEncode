namespace FlowEncode.Domain;

public sealed record EncoderUpdatePackage(
    EncoderKind Kind,
    EncoderArchitecture Architecture,
    string ReleaseName,
    string AssetName,
    string ReleaseUrl,
    string DownloadUrl,
    DateTimeOffset PublishedAt,
    string Notes,
    string? Sha256,
    bool IsAutomatic)
{
    public string EncoderLabel => Kind.ToDisplayName();

    public string ArchitectureLabel => Architecture.ToDisplayName();

    public string PublishedLabel => PublishedAt.ToPublishedLabel();
}
