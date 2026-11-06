using System.Buffers.Binary;
using System.IO.Compression;
using FlowEncode.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class SharedAutomationHelpersTests
{
    [TestMethod]
    [DataRow(AudioProcessingMode.Opus, null, @"D:\out\movie.opus")]
    [DataRow(AudioProcessingMode.Eac3To, AudioEac3ToOutputFormat.Flac, @"D:\out\movie.flac")]
    [DataRow(AudioProcessingMode.Eac3To, AudioEac3ToOutputFormat.Ac3, @"D:\out\movie.ac3")]
    [DataRow(AudioProcessingMode.Ddp, null, @"D:\out")]
    public void AudioOutputPathPlanner_MatchesDesktopNaming(AudioProcessingMode mode, AudioEac3ToOutputFormat? format, string expected)
    {
        Assert.AreEqual(expected, AudioProcessingOutputPathPlanner.BuildOutputPath(@"C:\in\movie.dts", @"D:\out", mode, format));
    }

    [TestMethod]
    public void AudioOutputPathPlanner_FlagsVideoContainersForEac3To()
    {
        Assert.IsTrue(AudioProcessingOutputPathPlanner.IsDirectSourceUnsupported(@"C:\in\movie.MKV"));
        Assert.IsTrue(AudioProcessingOutputPathPlanner.IsDirectSourceUnsupported(@"C:\in\00001.m2ts"));
        Assert.IsFalse(AudioProcessingOutputPathPlanner.IsDirectSourceUnsupported(@"C:\in\track.thd"));
    }

    [TestMethod]
    public void BluRayTrackOutputPathPlanner_NamesTracksPerBackend()
    {
        var playlist = new BluRayPlaylistItem("00800", "00800.mpls", "", "", "", "", null, null);
        var subtitle = new BluRayTrackItem("4", 4, "4:", BluRayTrackKind.Subtitle, "PGS", "", "Chinese Simplified");
        var chapters = new BluRayTrackItem("1", 1, "1:", BluRayTrackKind.Chapters, "Chapters", "", "");

        Assert.AreEqual(@"D:\out\00800.*", BluRayTrackOutputPathPlanner.BuildTrackOutputPath(BluRayDemuxBackend.DgDemux, @"D:\out", playlist, subtitle));
        Assert.AreEqual(@"D:\out\00800_T04_subtitle_chinese_simplified.*", BluRayTrackOutputPathPlanner.BuildTrackOutputPath(BluRayDemuxBackend.Eac3To, @"D:\out", playlist, subtitle));
        Assert.AreEqual(@"D:\out\00800_T01_chapters.txt", BluRayTrackOutputPathPlanner.BuildTrackOutputPath(BluRayDemuxBackend.Eac3To, @"D:\out", playlist, chapters));
        Assert.AreEqual(@"D:\out\00800", BluRayTrackOutputPathPlanner.BuildOutputPrefixPath(@"D:\out", playlist));
    }

    [TestMethod]
    public void ChapterFile_ParsesOgmWithLanguagePrefixesAndMissingNames()
    {
        var chapters = ChapterFile.ParseOgm(
        [
            "# comment",
            "CHAPTER02=00:10:00.500",
            "CHAPTER01=00:00:00.000",
            "CHAPTER01NAME=eng:Opening",
        ]);

        Assert.AreEqual(2, chapters.Count);
        Assert.AreEqual(new ChapterEntry(TimeSpan.Zero, "Opening"), chapters[0]);
        Assert.AreEqual(new ChapterEntry(new TimeSpan(0, 0, 10, 0, 500), "Chapter 02"), chapters[1]);
    }

    [TestMethod]
    public async Task ChapterFile_RoundTripsOgmAndMatroskaXml()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FlowEncodeChapterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            IReadOnlyList<ChapterEntry> chapters =
            [
                new(TimeSpan.FromMinutes(5), "第二章"),
                new(TimeSpan.Zero, "Intro"),
            ];

            foreach (var name in new[] { "chapters.txt", "chapters.xml" })
            {
                var path = Path.Combine(directory, name);
                await ChapterFile.SaveAsync(path, chapters);
                var loaded = await ChapterFile.LoadAsync(path);

                CollectionAssert.AreEqual(chapters.OrderBy(static chapter => chapter.Timecode).ToList(), loaded.ToList(), name);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ChapterFile_ConvertsBetweenTimecodesAndFrames()
    {
        Assert.IsTrue(ChapterFile.TryParseTimecode("01:30.5", out var shortForm));
        Assert.AreEqual(TimeSpan.FromSeconds(90.5), shortForm);
        Assert.AreEqual(2398, ChapterFile.TimecodeToFrame(TimeSpan.FromSeconds(100), 24000, 1001, 0));
        Assert.AreEqual(99, ChapterFile.TimecodeToFrame(TimeSpan.FromHours(1), 24, 1, 100), "clamped to the last frame");
        Assert.AreEqual("00:00:01.000", ChapterFile.FormatTimecode(ChapterFile.FrameToTimecode(24, 24, 1)));
    }

    [TestMethod]
    public void BgraImageEncoder_WritesValidRgbaPng()
    {
        // 2x1: opaque blue, half-transparent red (BGRA order).
        byte[] bgra = [255, 0, 0, 255, 0, 0, 255, 128];
        var png = BgraImageEncoder.EncodePng(bgra, 2, 1);

        CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8).ToArray());
        var chunks = ReadChunks(png);
        Assert.AreEqual("IHDR", chunks[0].Type);
        Assert.AreEqual(2, BinaryPrimitives.ReadInt32BigEndian(chunks[0].Data));
        Assert.AreEqual(1, BinaryPrimitives.ReadInt32BigEndian(chunks[0].Data.AsSpan(4)));
        Assert.AreEqual("IEND", chunks[^1].Type);

        using var zlib = new ZLibStream(new MemoryStream(chunks.Single(static chunk => chunk.Type == "IDAT").Data), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 255, 255, 255, 0, 0, 128 }, raw.ToArray());
    }

    [TestMethod]
    public void BgraImageEncoder_StacksFramesWithBlackPadding()
    {
        var white = (Enumerable.Repeat((byte)255, 2 * 2 * 4).ToArray(), 2, 2);
        var gray = (Enumerable.Repeat((byte)128, 1 * 1 * 4).ToArray(), 1, 1);

        var horizontal = BgraImageEncoder.Stack([white, gray], horizontal: true);
        Assert.AreEqual((3, 2), (horizontal.Width, horizontal.Height));
        var bottomRightPixel = horizontal.Pixels.AsSpan(((1 * 3) + 2) * 4, 4).ToArray();
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 255 }, bottomRightPixel, "padding is opaque black");

        var vertical = BgraImageEncoder.Stack([white, gray], horizontal: false);
        Assert.AreEqual((2, 3), (vertical.Width, vertical.Height));
    }

    [TestMethod]
    public void PreviewCropCommandBuilder_UsesResolvedBounds()
    {
        var bounds = new PreviewFrameComposer().ResolveCropBounds(
            new PreviewFrameCropSettings(true, PreviewCropMode.Relative, new PreviewAbsoluteCrop(0, 0, 0, 0), new PreviewRelativeCrop(0, 140, 0, 140)),
            1920,
            1080);

        Assert.AreEqual("clip = core.std.CropRel(clip, left=0, top=140, right=0, bottom=140)", PreviewCropCommandBuilder.Build(PreviewCropMode.Relative, bounds));
        Assert.AreEqual("clip = core.std.CropAbs(clip, x=0, y=140, width=1920, height=800)", PreviewCropCommandBuilder.Build(PreviewCropMode.Absolute, bounds));
    }

    private static List<(string Type, byte[] Data)> ReadChunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            chunks.Add((type, png.AsSpan(offset + 8, length).ToArray()));
            offset += 12 + length;
        }

        return chunks;
    }
}
