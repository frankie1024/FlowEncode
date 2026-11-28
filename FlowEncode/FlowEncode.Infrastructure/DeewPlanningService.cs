using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

// Preview and MCP use the same rules and actual child-tool resolution as the runner.
// Config initialization and leases remain exclusively on the execution path.
public sealed class DeewPlanningService(IToolProbeService probes) : IDeewPlanningService
{
    public async Task<DeewReadinessSnapshot> InspectAsync(string sourcePath, DeewOptions options, CancellationToken cancellationToken = default)
    {
        var logs = new List<string>();
        var o = DeewArguments.Normalize(options, logs);
        AudioSourceSupport.ValidateDeewInput(sourcePath, o);
        async Task<string> Tool(RegisteredToolKind kind)
        {
            var result = await probes.ProbeAsync(kind, cancellationToken);
            return result.IsReady && !string.IsNullOrWhiteSpace(result.ExecutablePath) ? result.ExecutablePath
                : throw new InvalidOperationException(result.FailureReason.Length > 0 ? result.FailureReason : $"Missing {kind}.");
        }
        var deew = await Tool(RegisteredToolKind.Deew);
        var ffmpeg = await Tool(RegisteredToolKind.Ffmpeg);
        var ffprobe = await Tool(RegisteredToolKind.Ffprobe);
        var info = DeewToolchain.StartInfo(deew, ffmpeg, ffprobe);
        var source = await DeewToolchain.ProbeSourceAsync(info, sourcePath, o, cancellationToken);
        var config = Path.Combine(Path.GetDirectoryName(deew)!, "config.toml");
        if (File.Exists(config + ".flowencode-backup.json"))
            throw new InvalidOperationException("A deew config recovery backup is pending. Complete recovery before previewing a new plan: " + config);
        var snapshot = File.Exists(config) ? new DeewConfigEditor(await File.ReadAllBytesAsync(config, cancellationToken)) : null;
        if (DeewArguments.IsAtmosFormat(o.Format) && !o.MeasureOnly) logs.Add("Atmos output is experimental.");
        var profile = DeewBitrates.Apply(DeewArguments.Resolve(o, source,
            snapshot?.DdpMode ?? "standard", snapshot?.AtmosMode ?? "streaming", logs, snapshot?.Ac4ImsMode ?? "general"), snapshot?.Bitrates);
        var outputTemplate = snapshot?.OutputTemplate ?? DeewOutputNaming.DefaultTemplate;
        var expectedOutputFileName = DeewOutputNaming.ResolveFileName(sourcePath, source, profile, outputTemplate);
        if (snapshot is null) logs.Add("Missing config.toml will be initialized once at execution; existing configs are never reset automatically.");
        // A disposable adjacent file verifies the portable directory without touching config contents.
        var writableProbe = config + "." + Guid.NewGuid().ToString("N") + ".probe";
        try
        {
            if (File.Exists(config) && (File.GetAttributes(config) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("deew config.toml is read-only.");
            await File.WriteAllTextAsync(writableProbe, "", cancellationToken);
            File.Move(writableProbe, writableProbe + ".replace");
        }
        catch (UnauthorizedAccessException ex) { throw new InvalidOperationException("Move deew and its complete toolchain to a writable portable directory. " + ex.Message, ex); }
        finally { File.Delete(writableProbe); File.Delete(writableProbe + ".replace"); }
        var surroundAttenuation = snapshot?.AttenuatesTrueHdSurround(profile)
            ?? (profile.Options.Format == "thd" && profile.OutputSlots >= 6 && profile.Options.Surround3dbAttenuation == true);
        if (surroundAttenuation) logs.Add(DeewConfigEditor.TrueHdSurroundAttenuationWarning);
        await DeewToolchain.ValidateAsync(deew, ffmpeg, ffprobe, profile, logs.Add, cancellationToken);
        return new(source, profile, snapshot?.DdpMode ?? "standard", snapshot?.AtmosMode ?? "streaming",
            snapshot?.Bitrates ?? DeewBitrates.Defaults, logs, surroundAttenuation,
            snapshot?.Ac4ImsMode ?? "general", outputTemplate, expectedOutputFileName);
    }
}
