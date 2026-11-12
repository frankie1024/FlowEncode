namespace FlowEncode.Domain;

public sealed record BluRayDemuxRequest(
    Guid JobId,
    BluRayDemuxBackend Backend,
    string DiscPath,
    string OutputDirectory,
    string OutputPrefixPath,
    BluRayPlaylistItem Playlist,
    IReadOnlyList<BluRayTrackSelection> Selections,
    bool OverwriteOutput = true);
