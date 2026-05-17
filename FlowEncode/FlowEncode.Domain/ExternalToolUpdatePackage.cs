namespace FlowEncode.Domain;

public sealed record ExternalToolUpdatePackage(
    ExternalToolKind Kind,
    string ReleaseName,
    string AssetName,
    string ReleaseUrl,
    string DownloadUrl,
    DateTimeOffset PublishedAt,
    string Notes,
    string? Sha256,
    bool IsAutomatic)
{
    public string ToolLabel => Kind.ToDisplayName();

    public string PublishedLabel => PublishedAt.ToPublishedLabel();
}

