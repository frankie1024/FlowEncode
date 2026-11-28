using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class DeewComponentInstallerTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ComponentDownload_OnlyReplacesVerifiedCompanionTools(bool corruptLastPackage)
    {
        using var root = new SharedTaskCoordinatorTests.TestRoot();
        var components = new[]
        {
            (Tool: "truehdd", Repo: "truehdd/truehdd", Asset: "truehdd-0.6.3-x86_64-pc-windows-msvc.zip"),
            (Tool: "harletty", Repo: "pcrlnd/harletty-bridge", Asset: "harletty-cli-v0.9.0-windows-x86_64.zip"),
            (Tool: "dee_shim", Repo: "truehdd/dee-shim", Asset: "dee-shim-0.5.0-windows-x86_64.zip")
        };
        var binaries = components.Select(component => component.Tool + ".exe").ToArray();
        var untouched = new[] { "deew.exe", "ffmpeg.exe", "ffprobe.exe", "config.toml" };
        foreach (var file in binaries.Concat(untouched))
            File.WriteAllText(Path.Combine(root.Path, file), "original " + file);

        var releaseByUrl = new Dictionary<string, GitHubRelease>();
        var archiveByUrl = new Dictionary<string, byte[]>();
        foreach (var component in components)
        {
            var bytes = CreateArchive(component.Tool + ".exe");
            var hash = corruptLastPackage && component.Tool == "dee_shim"
                ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes));
            var downloadUrl = $"https://github.com/{component.Repo}/releases/download/stable/{component.Asset}";
            archiveByUrl.Add(downloadUrl, bytes);
            releaseByUrl.Add($"https://api.github.com/repos/{component.Repo}/releases/latest",
                new("stable", null, "", DateTimeOffset.UnixEpoch, false, false,
                    [new(component.Asset, downloadUrl, "sha256:" + hash)]));
        }

        using var apiHandler = new ResponseHandler(url => JsonContent.Create(releaseByUrl[url]));
        using var downloadHandler = new ResponseHandler(url => new ByteArrayContent(archiveByUrl[url]));
        using var api = new HttpClient(apiHandler);
        using var downloads = new HttpClient(downloadHandler);
        var install = DeewComponentInstaller.InstallAsync(Path.Combine(root.Path, "deew.exe"), api, downloads, null, default);
        if (corruptLastPackage)
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => install);
        else
            await install;

        Assert.AreEqual(components.Length, apiHandler.RequestCount);
        Assert.AreEqual(components.Length, downloadHandler.RequestCount);
        foreach (var file in binaries)
            Assert.AreEqual((corruptLastPackage ? "original " : "downloaded ") + file, File.ReadAllText(Path.Combine(root.Path, file)));
        foreach (var file in untouched)
            Assert.AreEqual("original " + file, File.ReadAllText(Path.Combine(root.Path, file)));
        Assert.IsFalse(Directory.EnumerateDirectories(root.Path, ".flowencode-deew-tools-*").Any());
    }

    private static byte[] CreateArchive(string file)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("portable/" + file).Open()))
            writer.Write("downloaded " + file);
        return stream.ToArray();
    }

    private sealed class ResponseHandler(Func<string, HttpContent> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = response(request.RequestUri!.AbsoluteUri) });
        }
    }
}
