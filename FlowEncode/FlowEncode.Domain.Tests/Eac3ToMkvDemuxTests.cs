using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class Eac3ToMkvDemuxTests
{
    [TestMethod]
    public void ParseTracks_MkvOutput_ParsesChaptersVideoAudioSubtitlesLanguagesAndTitles()
    {
        var lines = new[]
        {
            "Fast mode enabled",
            "Keeping dialnorm",
            "analyze: 100%",
            "MKV, 1 video track, 2 audio tracks, 2 subtitle tracks, 0:00:02, 48i",
            "1: Chapters, 12 chapters",
            "2: h264/AVC, 320x240i",
            "3: AC3, [eng], 1.0 channels, 96kbps, 44.1kHz, dialnorm: -31dB",
            "4: FLAC, [jpn], 1.0 channels, 0:00:02, 16 bits, 44.1kHz, dialnorm: 0dB",
            "\"Japanese FLAC\"",
            "5: Subtitle (SRT), [chi]",
            "6: Subtitle (ASS), [eng]"
        };

        var tracks = Eac3ToBackendAdapter.ParseTracks(lines);

        Assert.AreEqual(6, tracks.Count);
        Assert.AreEqual(BluRayTrackKind.Chapters, tracks[0].Kind);
        Assert.AreEqual(string.Empty, tracks[0].Language);

        Assert.AreEqual(BluRayTrackKind.Video, tracks[1].Kind);
        Assert.AreEqual(string.Empty, tracks[1].Language);

        Assert.AreEqual(BluRayTrackKind.Audio, tracks[2].Kind);
        Assert.AreEqual("eng", tracks[2].Language);

        Assert.AreEqual(BluRayTrackKind.Audio, tracks[3].Kind);
        Assert.AreEqual("jpn", tracks[3].Language);
        StringAssert.Contains(tracks[3].Detail, "\"Japanese FLAC\"");

        Assert.AreEqual(BluRayTrackKind.Subtitle, tracks[4].Kind);
        Assert.AreEqual("chi", tracks[4].Language);

        Assert.AreEqual(BluRayTrackKind.Subtitle, tracks[5].Kind);
        Assert.AreEqual("eng", tracks[5].Language);
    }

    [TestMethod]
    public void ResolveTrackOutputPath_Eac3ToSubtitlesAndChapters_UsesExplicitExtensions()
    {
        var playlist = new BluRayPlaylistItem("Movie", "Movie.mkv", "MKV", @"D:\Movie.mkv", string.Empty, "0:00:02", TimeSpan.FromSeconds(2), 12);
        var chapters = new BluRayTrackItem("1", 1, "1", BluRayTrackKind.Chapters, "#01 · Chapters", "Chapters, 12 chapters", string.Empty);
        var video = new BluRayTrackItem("2", 2, "2", BluRayTrackKind.Video, "#02 · h265/HEVC", "h265/HEVC, 1920x1080p24", string.Empty);
        var audio = new BluRayTrackItem("3", 3, "3", BluRayTrackKind.Audio, "#03 · FLAC · jpn", "FLAC, [jpn], 2.0 channels", "jpn");
        var srt = new BluRayTrackItem("4", 4, "4", BluRayTrackKind.Subtitle, "#04 · Subtitle (SRT) · chi", "Subtitle (SRT), [chi]", "chi");
        var ass = new BluRayTrackItem("5", 5, "5", BluRayTrackKind.Subtitle, "#05 · Subtitle (ASS) · eng", "Subtitle (ASS), [eng]", "eng");
        var pgs = new BluRayTrackItem("6", 6, "6", BluRayTrackKind.Subtitle, "#06 · Subtitle (PGS) · eng", "Subtitle (PGS), [eng]", "eng");

        Assert.AreEqual(@"D:\out\Movie_T01_chapters.txt", BluRayDemuxSourceSupport.ResolveTrackOutputPath(BluRayDemuxBackend.Eac3To, @"D:\out", playlist, chapters));
        Assert.AreEqual(@"D:\out\Movie_T02_video.*", BluRayDemuxSourceSupport.ResolveTrackOutputPath(BluRayDemuxBackend.Eac3To, @"D:\out", playlist, video));
        Assert.AreEqual(@"D:\out\Movie_T03_audio_jpn.*", BluRayDemuxSourceSupport.ResolveTrackOutputPath(BluRayDemuxBackend.Eac3To, @"D:\out", playlist, audio));
        Assert.AreEqual(@"D:\out\Movie_T04_subtitle_chi.srt", BluRayDemuxSourceSupport.ResolveTrackOutputPath(BluRayDemuxBackend.Eac3To, @"D:\out", playlist, srt));
        Assert.AreEqual(@"D:\out\Movie_T05_subtitle_eng.ass", BluRayDemuxSourceSupport.ResolveTrackOutputPath(BluRayDemuxBackend.Eac3To, @"D:\out", playlist, ass));
        Assert.AreEqual(@"D:\out\Movie_T06_subtitle_eng.sup", BluRayDemuxSourceSupport.ResolveTrackOutputPath(BluRayDemuxBackend.Eac3To, @"D:\out", playlist, pgs));
    }

    [TestMethod]
    public void TryResolveBluRayDiscRootFromEntryFile_ResolvesIndexBdmvAndMpls()
    {
        Assert.AreEqual(@"D:\Movies\SampleBD", BluRayDemuxSourceSupport.TryResolveBluRayDiscRootFromEntryFile(@"D:\Movies\SampleBD\BDMV\index.bdmv"));
        Assert.AreEqual(@"D:\Movies\SampleBD", BluRayDemuxSourceSupport.TryResolveBluRayDiscRootFromEntryFile(@"D:\Movies\SampleBD\BDMV\PLAYLIST\00001.mpls"));
        Assert.IsNull(BluRayDemuxSourceSupport.TryResolveBluRayDiscRootFromEntryFile(@"D:\Movies\Sample.mkv"));
    }

    [TestMethod]
    public async Task Eac3To_ScanAndDemuxMkv_ReusesScanCacheOmitsSelectionTokenAndPublishesOutputs()
    {
        using var fixture = new BusinessExecutionSafetyTests.Fixture("mkv-demux");
        var mkvPath = Path.Combine(fixture.Root, "sample_movie.mkv");
        await File.WriteAllTextAsync(mkvPath, "fake mkv content");
        var outputDirectory = Path.Combine(fixture.Root, "sample_movie_demux");
        var adapter = new Eac3ToBackendAdapter(fixture.Probes, fixture.Paths);

        var playlists = await adapter.ScanDiscAsync(mkvPath);
        Assert.AreEqual(1, playlists.Count);
        var item = playlists[0];
        Assert.AreEqual("sample_movie", item.Id);
        Assert.AreEqual("sample_movie.mkv", item.DisplayName);
        Assert.AreEqual(mkvPath, item.SourcePath);
        Assert.AreEqual(string.Empty, item.SelectionToken);
        Assert.AreEqual("0:00:02", item.DurationText);
        Assert.AreEqual(12, item.ChapterCount);

        var scanResult = await adapter.ScanPlaylistAsync(mkvPath, item);
        Assert.AreEqual(5, scanResult.Tracks.Count);

        var callsLogPath = Path.Combine(fixture.Root, "eac3to-calls.log");
        var scanCalls = await File.ReadAllLinesAsync(callsLogPath);
        Assert.AreEqual(1, scanCalls.Length, "Second MKV scan for unchanged file should reuse cached probe result.");
        Assert.AreEqual(mkvPath, scanCalls[0].Trim());

        var selections = scanResult.Tracks
            .Select(track => new BluRayTrackSelection(
                track,
                BluRayDemuxSourceSupport.ResolveTrackOutputPath(BluRayDemuxBackend.Eac3To, outputDirectory, item, track)))
            .ToList();
        var request = new BluRayDemuxRequest(
            Guid.NewGuid(),
            BluRayDemuxBackend.Eac3To,
            mkvPath,
            outputDirectory,
            Path.Combine(outputDirectory, item.Id),
            item,
            selections);

        var displayCommand = adapter.BuildDisplayCommand(request);
        StringAssert.StartsWith(displayCommand, $"eac3to.exe \"{mkvPath}\" 1: ");
        Assert.IsFalse(displayCommand.Contains("1) ", StringComparison.Ordinal), "MKV command must not contain a Blu-ray playlist selection token.");

        var result = await adapter.RunAsync(request);
        Assert.AreEqual(EncodingJobState.Completed, result.State, result.Log);
        Assert.AreEqual(5, result.OutputPaths.Count);
        Assert.IsTrue(result.OutputPaths.All(File.Exists));
        Assert.IsTrue(File.Exists(Path.Combine(outputDirectory, "sample_movie_T01_chapters.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(outputDirectory, "sample_movie_T02_video.h265")));
        Assert.IsTrue(File.Exists(Path.Combine(outputDirectory, "sample_movie_T03_audio_jpn.flac")));
        Assert.IsTrue(File.Exists(Path.Combine(outputDirectory, "sample_movie_T04_subtitle_chi.srt")));
        Assert.IsTrue(File.Exists(Path.Combine(outputDirectory, "sample_movie_T05_subtitle_eng.ass")));
    }

    [TestMethod]
    public async Task DgDemux_RejectsMkvSourceBeforeToolLaunch()
    {
        using var fixture = new BusinessExecutionSafetyTests.Fixture("mkv-demux");
        var mkvPath = Path.Combine(fixture.Root, "sample_movie.mkv");
        await File.WriteAllTextAsync(mkvPath, "fake mkv content");
        var adapter = new DgDemuxBackendAdapter(fixture.Probes);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => adapter.ScanDiscAsync(mkvPath));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "dgdemux-calls.log")));
    }

    [TestMethod]
    public async Task Eac3To_MissingMkvFile_IsRejectedBeforeToolLaunch()
    {
        using var fixture = new BusinessExecutionSafetyTests.Fixture("mkv-demux");
        var missingMkv = Path.Combine(fixture.Root, "missing.mkv");
        var adapter = new Eac3ToBackendAdapter(fixture.Probes, fixture.Paths);

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => adapter.ScanDiscAsync(missingMkv));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "eac3to-calls.log")));
    }
}
