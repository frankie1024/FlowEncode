namespace FlowEncode.Domain;

public sealed record DeewReadinessSnapshot(
    AudioSourceInfo Source, DeewEffectiveOutputProfile Profile,
    string DefaultDdpMode, string DefaultAtmosMode, IReadOnlyDictionary<string, int> DefaultBitrates,
    IReadOnlyList<string> Diagnostics);
