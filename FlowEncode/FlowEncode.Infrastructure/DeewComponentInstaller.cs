using System.IO.Compression;
using System.Net.Http.Json;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal static class DeewComponentInstaller
{
    // The Windows x64 assets used by deew -gt for these three components.
    private static readonly (string Name, string Repository, string Prefix, string Suffix)[] Components =
    [
        ("truehdd", "truehdd/truehdd", "truehdd-", "-x86_64-pc-windows-msvc.zip"),
        ("harletty", "pcrlnd/harletty-bridge", "harletty-cli-", "-windows-x86_64.zip"),
        ("dee_shim", "truehdd/dee-shim", "dee-shim-", "-windows-x86_64.zip")
    ];

    internal static async Task InstallAsync(string deew, HttpClient api, HttpClient downloads,
        IProgress<SetupInstallProgress>? progress, CancellationToken token)
    {
        if (!File.Exists(deew)) throw new FileNotFoundException("Select a deew executable first.", deew);
        var directory = Path.GetDirectoryName(Path.GetFullPath(deew))!;
        var staging = Path.Combine(directory, $".flowencode-deew-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var files = new List<(string Source, string Target)>();
            foreach (var (name, repository, prefix, suffix) in Components)
            {
                progress?.Report(new(SetupDependencyKind.Deew, 100.0 * files.Count / Components.Length, name, false));
                var release = await api.GetFromJsonAsync<GitHubRelease>($"https://api.github.com/repos/{repository}/releases/latest", token);
                var asset = release?.Assets?.FirstOrDefault(a => a.Name.StartsWith(prefix, StringComparison.Ordinal)
                    && a.Name.EndsWith(suffix, StringComparison.Ordinal));
                if (asset?.BrowserDownloadUrl is not { } url
                    || !url.StartsWith($"https://github.com/{repository}/releases/download/", StringComparison.Ordinal)
                    || PackageIntegrityVerifier.NormalizeSha256Digest(asset.Digest) is not { Length: 64 } digest)
                    throw new InvalidDataException($"Could not find a verifiable Windows x64 package for {name}.");

                var archivePath = Path.Combine(staging, name + ".zip");
                using (var response = await downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    response.EnsureSuccessStatusCode();
                    await using var output = File.Create(archivePath);
                    await response.Content.CopyToAsync(output, token);
                }
                await PackageIntegrityVerifier.VerifySha256Async(archivePath, digest, token, name);

                var executable = name + ".exe";
                var stagedExecutable = Path.Combine(staging, executable);
                using var archive = ZipFile.OpenRead(archivePath);
                var entry = archive.Entries.SingleOrDefault(e => e.Name.Equals(executable, StringComparison.OrdinalIgnoreCase));
                if (entry is null || entry.Length == 0)
                    throw new InvalidDataException($"The {name} archive does not contain {executable}.");
                await using var input = entry.Open();
                await using var target = File.Create(stagedExecutable);
                await input.CopyToAsync(target, token);
                files.Add((stagedExecutable, Path.Combine(directory, executable)));
            }

            using var toolchainLock = await DeewConfigLease.LockToolchainAsync(directory, token);
            token.ThrowIfCancellationRequested();
            ExecutionOutputStaging.FinalizeFiles(files, overwrite: true);
        }
        finally
        {
            ExecutionOutputStaging.CleanupStagedDirectory(staging);
        }
    }
}
