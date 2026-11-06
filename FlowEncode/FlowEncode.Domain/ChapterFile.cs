using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FlowEncode.Domain;

public sealed record ChapterEntry(TimeSpan Timecode, string Title);

/// <summary>
/// Reads and writes OGM chapter text (CHAPTER01=00:00:00.000 / CHAPTER01NAME=...) and Matroska chapter XML.
/// </summary>
public static partial class ChapterFile
{
    public static async Task<IReadOnlyList<ChapterEntry>> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (IsXmlPath(filePath))
        {
            return ParseMatroskaXml(await File.ReadAllTextAsync(filePath, cancellationToken));
        }

        return ParseOgm(await File.ReadAllLinesAsync(filePath, cancellationToken));
    }

    /// <summary>
    /// Writes Matroska XML for a .xml path and OGM text for anything else.
    /// </summary>
    public static async Task SaveAsync(string filePath, IReadOnlyList<ChapterEntry> chapters, CancellationToken cancellationToken = default)
    {
        var ordered = chapters.OrderBy(static chapter => chapter.Timecode).ToList();
        if (IsXmlPath(filePath))
        {
            await File.WriteAllTextAsync(filePath, BuildMatroskaXml(ordered), cancellationToken);
            return;
        }

        await File.WriteAllLinesAsync(filePath, BuildOgmLines(ordered), cancellationToken);
    }

    public static IReadOnlyList<ChapterEntry> ParseOgm(IEnumerable<string> lines)
    {
        var timecodes = new Dictionary<int, TimeSpan>();
        var titles = new Dictionary<int, string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var match = OgmLinePattern().Match(line);
            if (!match.Success
                || !int.TryParse(match.Groups["index"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                continue;
            }

            var value = match.Groups["value"].Value.Trim();
            if (match.Groups["name"].Success)
            {
                titles[index] = StripOgmLanguagePrefix(value);
            }
            else if (TryParseTimecode(value, out var timecode))
            {
                timecodes[index] = timecode;
            }
        }

        return timecodes
            .OrderBy(static item => item.Key)
            .Select(item => new ChapterEntry(
                item.Value,
                titles.TryGetValue(item.Key, out var title) && !string.IsNullOrWhiteSpace(title)
                    ? title
                    : $"Chapter {item.Key:00}"))
            .ToList();
    }

    public static IReadOnlyList<ChapterEntry> ParseMatroskaXml(string xml)
    {
        var document = XDocument.Parse(xml);
        return document
            .Descendants("ChapterAtom")
            .Select(atom =>
            {
                var startText = atom.Element("ChapterTimeStart")?.Value.Trim();
                var title = atom
                    .Elements("ChapterDisplay")
                    .Elements("ChapterString")
                    .FirstOrDefault()
                    ?.Value
                    .Trim();

                return TryParseTimecode(startText, out var timecode)
                    ? new ChapterEntry(timecode, string.IsNullOrWhiteSpace(title) ? "Chapter" : title)
                    : null;
            })
            .Where(static chapter => chapter is not null)
            .Select(static chapter => chapter!)
            .OrderBy(static chapter => chapter.Timecode)
            .ToList();
    }

    public static IEnumerable<string> BuildOgmLines(IReadOnlyList<ChapterEntry> chapters)
    {
        for (var index = 0; index < chapters.Count; index++)
        {
            var chapterNumber = index + 1;
            yield return $"CHAPTER{chapterNumber:00}={FormatTimecode(chapters[index].Timecode)}";
            yield return $"CHAPTER{chapterNumber:00}NAME={chapters[index].Title}";
        }
    }

    public static string BuildMatroskaXml(IReadOnlyList<ChapterEntry> chapters)
    {
        var editionEntry = new XElement("EditionEntry",
            chapters.Select((chapter, index) =>
                new XElement("ChapterAtom",
                    new XElement("ChapterTimeStart", FormatTimecode(chapter.Timecode)),
                    new XElement("ChapterUID", index + 1),
                    new XElement("ChapterDisplay",
                        new XElement("ChapterString", chapter.Title),
                        new XElement("ChapterLanguage", "und")))));

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("Chapters", editionEntry));
        return document.ToString();
    }

    public static bool TryParseTimecode(string? value, out TimeSpan timecode)
    {
        timecode = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim().Replace(',', '.');
        var parts = text.Split(':');
        if (parts.Length == 2)
        {
            text = $"00:{text}";
        }

        return TimeSpan.TryParseExact(
                text,
                [@"h\:mm\:ss\.fff", @"hh\:mm\:ss\.fff", @"h\:mm\:ss", @"hh\:mm\:ss"],
                CultureInfo.InvariantCulture,
                out timecode)
            || TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out timecode);
    }

    public static string FormatTimecode(TimeSpan timecode)
    {
        return timecode.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Maps a timecode to a frame index for the given frame rate (24 fps when unknown), clamped to the clip.
    /// </summary>
    public static int TimecodeToFrame(TimeSpan timecode, int fpsNumerator, int fpsDenominator, int totalFrames)
    {
        var fps = ResolveFps(fpsNumerator, fpsDenominator);
        var maxFrame = totalFrames > 0 ? totalFrames - 1 : int.MaxValue;
        return Math.Clamp((int)Math.Round(Math.Max(0, timecode.TotalSeconds) * fps), 0, maxFrame);
    }

    public static TimeSpan FrameToTimecode(int frameNumber, int fpsNumerator, int fpsDenominator)
    {
        return TimeSpan.FromSeconds(Math.Max(0, frameNumber) / ResolveFps(fpsNumerator, fpsDenominator));
    }

    private static double ResolveFps(int fpsNumerator, int fpsDenominator)
    {
        return fpsNumerator > 0 && fpsDenominator > 0
            ? fpsNumerator / (double)fpsDenominator
            : 24.0;
    }

    private static bool IsXmlPath(string filePath)
    {
        return string.Equals(Path.GetExtension(filePath), ".xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripOgmLanguagePrefix(string value)
    {
        var separatorIndex = value.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex > 0 && separatorIndex <= 3)
        {
            return value[(separatorIndex + 1)..].Trim();
        }

        return value.Trim();
    }

    [GeneratedRegex(@"^CHAPTER(?<index>\d+)(?<name>NAME)?=(?<value>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex OgmLinePattern();
}
