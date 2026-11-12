using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FlowEncode.Mcp.Tools;

[McpServerToolType]
public static class VapourSynthTools
{
    private const int MaxInlineImagePixels = 1920 * 1080;

    [McpServerTool(Name = "vapoursynth_new_script", Destructive = false)]
    [Description("Creates a VapourSynth script (.vpy). Without 'content' it writes a starter script that loads 'sourcePath' "
        + "with the given source filter and sets it as output 0.")]
    public static async Task<ScriptDocumentView> NewScript(
        IVapourSynthWorkspaceService workspace,
        [Description("Absolute path of the .vpy file to create.")] string scriptPath,
        [Description("Full script text. Omit to generate a starter script.")] string? content = null,
        [Description("Video for the starter script to load.")] string? sourcePath = null,
        [Description("Source filter for the starter script. Defaults to lsmas.LWLibavSource.")] string? sourceFilter = null,
        [Description("Replace an existing file. Defaults to false.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var fullPath = RequireScriptPath(scriptPath);
        if (!overwrite && File.Exists(fullPath))
        {
            throw new McpException($"{fullPath} already exists. Pass overwrite=true or use vapoursynth_save_script.");
        }

        var text = content ?? BuildStarterScript(
            string.IsNullOrWhiteSpace(sourcePath) ? null : McpPaths.RequireAbsolute(sourcePath, nameof(sourcePath)),
            string.IsNullOrWhiteSpace(sourceFilter) ? "lsmas.LWLibavSource" : sourceFilter.Trim());
        var document = await McpToolGuard.RunAsync(() => workspace.SaveDocumentAsync(fullPath, text, cancellationToken));
        return ScriptDocumentView.From(document);
    }

    [McpServerTool(Name = "vapoursynth_read_script", ReadOnly = true, Idempotent = true)]
    [Description("Reads a VapourSynth script and returns its text.")]
    public static async Task<ScriptDocumentView> ReadScript(
        IVapourSynthWorkspaceService workspace,
        [Description("Absolute path of the .vpy / .py script.")] string scriptPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = McpPaths.RequireExistingFile(scriptPath, nameof(scriptPath));
        var document = await McpToolGuard.RunAsync(() => workspace.OpenDocumentAsync(fullPath, cancellationToken));
        return ScriptDocumentView.From(document);
    }

    [McpServerTool(Name = "vapoursynth_save_script", Destructive = true)]
    [Description("Writes the full text of a VapourSynth script, replacing the file. Run vapoursynth_diagnose_script afterwards.")]
    public static async Task<ScriptDocumentView> SaveScript(
        IVapourSynthWorkspaceService workspace,
        [Description("Absolute path of the .vpy / .py script.")] string scriptPath,
        [Description("The complete new script text.")] string content,
        CancellationToken cancellationToken = default)
    {
        var fullPath = RequireScriptPath(scriptPath);
        var document = await McpToolGuard.RunAsync(() => workspace.SaveDocumentAsync(fullPath, content ?? string.Empty, cancellationToken));
        return ScriptDocumentView.From(document);
    }

    [McpServerTool(Name = "vapoursynth_diagnose_script", ReadOnly = true)]
    [Description("Checks a VapourSynth script with the installed runtime: syntax errors, unknown plugins / functions, "
        + "bad arguments and evaluation errors, each with line and column.")]
    public static async Task<VapourSynthScriptDiagnosticResult> DiagnoseScript(
        IVapourSynthWorkspaceLanguageService languageService,
        [Description("Absolute path of the script. Required unless 'content' is given (relative imports resolve from it).")] string? scriptPath = null,
        [Description("Script text to check instead of the file's current content.")] string? content = null,
        CancellationToken cancellationToken = default)
    {
        var (path, text) = await ResolveScriptAsync(scriptPath, content, cancellationToken);
        return await McpToolGuard.RunAsync(() => languageService.DiagnoseScriptAsync(path, text, cancellationToken));
    }

    [McpServerTool(Name = "vapoursynth_list_functions", ReadOnly = true)]
    [Description("Lists the VapourSynth plugin namespaces and functions available in the installed runtime, with signatures. "
        + "Filter by namespace or function name to keep the result small.")]
    public static async Task<VapourSynthFunctionCatalog> ListFunctions(
        IVapourSynthWorkspaceLanguageService languageService,
        [Description("Case-insensitive filter matched against namespace and function names, e.g. 'resize' or 'std'.")] string? filter = null,
        [Description("Maximum functions to return. Defaults to 100.")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await McpToolGuard.RunAsync(() => languageService.GetLanguageFeaturesAsync(cancellationToken: cancellationToken));
        var needle = filter?.Trim() ?? string.Empty;
        var functions = snapshot.Namespaces
            .SelectMany(static ns => ns.Functions.Select(function => (Namespace: ns, Function: function)))
            .Where(item => needle.Length == 0
                || item.Namespace.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || item.Namespace.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || item.Function.QualifiedName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new VapourSynthFunctionCatalog(
            snapshot.IsRuntimeReady,
            snapshot.RuntimeSummary,
            snapshot.Namespaces.Select(static ns => ns.Name).ToList(),
            functions.Count,
            functions
                .Take(Math.Clamp(limit, 1, 1000))
                .Select(static item => new VapourSynthFunctionView(
                    item.Function.QualifiedName,
                    item.Function.SignatureLabel,
                    item.Function.ReturnType,
                    string.IsNullOrWhiteSpace(item.Function.Documentation) ? null : item.Function.Documentation))
                .ToList());
    }

    [McpServerTool(Name = "vapoursynth_get_outputs", ReadOnly = true)]
    [Description("Evaluates a script and lists its outputs (index, size, frame count, frame rate, format, bit depth).")]
    public static async Task<IReadOnlyList<VapourSynthPreviewOutputInfo>> GetOutputs(
        IVapourSynthPreviewService previewService,
        VapourSynthPreviewGate gate,
        [Description("Absolute path of the script. Required unless 'content' is given.")] string? scriptPath = null,
        [Description("Script text to evaluate instead of the file's content.")] string? content = null,
        CancellationToken cancellationToken = default)
    {
        var (path, text) = await ResolveScriptAsync(scriptPath, content, cancellationToken);
        await gate.Lock.WaitAsync(cancellationToken);
        try
        {
            var session = await OpenAsync(previewService, path, text, cancellationToken);
            return session.Outputs;
        }
        finally
        {
            await CloseQuietlyAsync(previewService);
            gate.Lock.Release();
        }
    }

    [McpServerTool(Name = "vapoursynth_render_frame", ReadOnly = false)]
    [Description("Renders one frame of a script output to a PNG and returns it as an image together with the frame's "
        + "properties (_PictType, _Matrix, _ColorRange, ...). Pick the frame by number or by timecode (e.g. a chapter time). "
        + "An optional crop previews the cropped frame and returns the matching core.std.CropAbs / CropRel line.")]
    public static async Task<CallToolResult> RenderFrame(
        IVapourSynthPreviewService previewService,
        PreviewFrameComposer composer,
        VapourSynthPreviewGate gate,
        [Description("Absolute path of the script. Required unless 'content' is given.")] string? scriptPath = null,
        [Description("Script text to render instead of the file's content.")] string? content = null,
        [Description("Output index (see vapoursynth_get_outputs). Defaults to 0.")] int outputIndex = 0,
        [Description("0-based frame number. Defaults to 0.")] int? frame = null,
        [Description("Timecode such as 00:12:34.567; used instead of 'frame' when given.")] string? timecode = null,
        [Description("Absolute .png path to save the frame to. Defaults to a file in the temp folder.")] string? savePath = null,
        [Description("Crop to preview. Absolute mode uses left/top/width/height, relative mode left/top/right/bottom.")] CropOptions? crop = null,
        [Description("Return the image inline (default true). Large frames are still saved at full size.")] bool includeImage = true,
        CancellationToken cancellationToken = default)
    {
        var (path, text) = await ResolveScriptAsync(scriptPath, content, cancellationToken);
        await gate.Lock.WaitAsync(cancellationToken);
        try
        {
            var session = await OpenAsync(previewService, path, text, cancellationToken);
            var output = FindOutput(session, outputIndex);
            var frameNumber = ResolveFrame(output, frame, timecode);
            var rendered = await McpToolGuard.RunAsync(() => previewService.RenderFrameAsync(output.Index, frameNumber, cancellationToken: cancellationToken));

            var cropSettings = crop?.ToSettings();
            var composed = composer.Compose(new PreviewFrameCompositionRequest(
                rendered.Pixels,
                rendered.Width,
                rendered.Height,
                cropSettings ?? new PreviewFrameCropSettings(false, PreviewCropMode.Relative, new PreviewAbsoluteCrop(0, 0, rendered.Width, rendered.Height), new PreviewRelativeCrop(0, 0, 0, 0))));
            var png = BgraImageEncoder.EncodePng(composed.Pixels, composed.Width, composed.Height);
            var pngPath = await SavePngAsync(png, savePath, path, output.Index, frameNumber, cancellationToken);

            var summary = new RenderedFrameView(
                pngPath,
                output.Index,
                frameNumber,
                ChapterFile.FormatTimecode(ChapterFile.FrameToTimecode(frameNumber, output.FpsNumerator, output.FpsDenominator)),
                rendered.Width,
                rendered.Height,
                composed.Width,
                composed.Height,
                rendered.FrameType,
                rendered.Properties.ToDictionary(static property => property.Key, static property => property.Value),
                cropSettings is null ? null : PreviewCropCommandBuilder.Build(cropSettings.Mode, composed.CropBounds),
                cropSettings is null ? null : composed.CropBounds);
            return BuildImageResult(summary, png, composed.Width * composed.Height, includeImage);
        }
        finally
        {
            await CloseQuietlyAsync(previewService);
            gate.Lock.Release();
        }
    }

    [McpServerTool(Name = "vapoursynth_compare_frames", ReadOnly = false)]
    [Description("Renders the same frame from several scripts (e.g. before / after filtering) or several outputs of one "
        + "script and places them side by side or stacked in one PNG, like the desktop compare view.")]
    public static async Task<CallToolResult> CompareFrames(
        IVapourSynthPreviewService previewService,
        VapourSynthPreviewGate gate,
        [Description("What to compare: each entry has scriptPath (or content) and an optional outputIndex. 2-4 entries.")] CompareSource[] sources,
        [Description("0-based frame number. Defaults to 0.")] int? frame = null,
        [Description("Timecode such as 00:12:34.567; used instead of 'frame' when given (resolved against the first source).")] string? timecode = null,
        [Description("horizontal (side by side, default) or vertical.")] string? layout = null,
        [Description("Absolute .png path for the combined image. Defaults to a file in the temp folder.")] string? savePath = null,
        [Description("Return the image inline (default true).")] bool includeImage = true,
        CancellationToken cancellationToken = default)
    {
        if (sources is null || sources.Length is < 2 or > 4)
        {
            throw new McpException("Pass 2 to 4 sources to compare.");
        }

        var horizontal = !string.Equals(layout?.Trim(), "vertical", StringComparison.OrdinalIgnoreCase);
        var frames = new List<(byte[] Pixels, int Width, int Height)>();
        var details = new List<RenderedFrameView>();
        int? resolvedFrame = null;
        string? firstPath = null;

        await gate.Lock.WaitAsync(cancellationToken);
        try
        {
            foreach (var source in sources)
            {
                var (path, text) = await ResolveScriptAsync(source.ScriptPath, source.Content, cancellationToken);
                firstPath ??= path;
                var session = await OpenAsync(previewService, path, text, cancellationToken);
                var output = FindOutput(session, source.OutputIndex ?? 0);
                resolvedFrame ??= ResolveFrame(output, frame, timecode);
                var frameNumber = Math.Clamp(resolvedFrame.Value, 0, Math.Max(0, output.TotalFrames - 1));
                var rendered = await McpToolGuard.RunAsync(() => previewService.RenderFrameAsync(output.Index, frameNumber, cancellationToken: cancellationToken));
                frames.Add((rendered.Pixels, rendered.Width, rendered.Height));
                details.Add(new RenderedFrameView(
                    path ?? "(inline script)",
                    output.Index,
                    frameNumber,
                    ChapterFile.FormatTimecode(ChapterFile.FrameToTimecode(frameNumber, output.FpsNumerator, output.FpsDenominator)),
                    rendered.Width,
                    rendered.Height,
                    rendered.Width,
                    rendered.Height,
                    rendered.FrameType,
                    rendered.Properties.ToDictionary(static property => property.Key, static property => property.Value),
                    null,
                    null));
                await CloseQuietlyAsync(previewService);
            }
        }
        finally
        {
            await CloseQuietlyAsync(previewService);
            gate.Lock.Release();
        }

        var stacked = BgraImageEncoder.Stack(frames, horizontal);
        var png = BgraImageEncoder.EncodePng(stacked.Pixels, stacked.Width, stacked.Height);
        var pngPath = await SavePngAsync(png, savePath, firstPath, 0, resolvedFrame ?? 0, cancellationToken, suffix: "compare");
        return BuildImageResult(new CompareFramesView(pngPath, horizontal ? "horizontal" : "vertical", details), png, stacked.Width * stacked.Height, includeImage);
    }

    [McpServerTool(Name = "read_chapters", ReadOnly = true, Idempotent = true)]
    [Description("Reads an OGM chapter text file or Matroska chapter XML. With fps it also maps each chapter to a frame number.")]
    public static async Task<IReadOnlyList<ChapterView>> ReadChapters(
        [Description("Absolute path of the .txt (OGM) or .xml (Matroska) chapter file.")] string chapterPath,
        [Description("Frame rate numerator for frame mapping, e.g. 24000.")] int? fpsNumerator = null,
        [Description("Frame rate denominator, e.g. 1001.")] int? fpsDenominator = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = McpPaths.RequireExistingFile(chapterPath, nameof(chapterPath));
        var chapters = await McpToolGuard.RunAsync(() => ChapterFile.LoadAsync(fullPath, cancellationToken));
        return chapters
            .Select((chapter, index) => new ChapterView(
                index + 1,
                ChapterFile.FormatTimecode(chapter.Timecode),
                chapter.Title,
                fpsNumerator is > 0 && fpsDenominator is > 0
                    ? ChapterFile.TimecodeToFrame(chapter.Timecode, fpsNumerator.Value, fpsDenominator.Value, 0)
                    : null))
            .ToList();
    }

    [McpServerTool(Name = "write_chapters", Destructive = true)]
    [Description("Writes chapters to an OGM text file (.txt) or Matroska chapter XML (.xml), sorted by time.")]
    public static async Task<IReadOnlyList<ChapterView>> WriteChapters(
        [Description("Absolute path of the .txt or .xml file to write.")] string chapterPath,
        [Description("Chapters with timecode (hh:mm:ss.fff) and title.")] ChapterInput[] chapters,
        [Description("Replace an existing file. Defaults to false.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var fullPath = McpPaths.RequireAbsolute(chapterPath, nameof(chapterPath));
        if (!overwrite && File.Exists(fullPath))
        {
            throw new McpException($"{fullPath} already exists. Pass overwrite=true to replace it.");
        }

        if (chapters is null || chapters.Length == 0)
        {
            throw new McpException("Pass at least one chapter.");
        }

        var entries = chapters
            .Select((chapter, index) => ChapterFile.TryParseTimecode(chapter.Timecode, out var timecode)
                ? new ChapterEntry(timecode, string.IsNullOrWhiteSpace(chapter.Title) ? $"Chapter {index + 1:00}" : chapter.Title.Trim())
                : throw new McpException($"'{chapter.Timecode}' is not a valid timecode (use hh:mm:ss.fff)."))
            .OrderBy(static chapter => chapter.Timecode)
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await McpToolGuard.RunAsync(async () =>
        {
            await ChapterFile.SaveAsync(fullPath, entries, cancellationToken);
            return true;
        });
        return entries
            .Select((chapter, index) => new ChapterView(index + 1, ChapterFile.FormatTimecode(chapter.Timecode), chapter.Title, null))
            .ToList();
    }

    internal static string BuildStarterScript(string? sourcePath, string sourceFilter)
    {
        var source = sourcePath is null
            ? "r\"C:\\path\\to\\source.mkv\""
            : $"r\"{sourcePath.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        return string.Join(
            "\n",
            "import vapoursynth as vs",
            "",
            "core = vs.core",
            "",
            $"clip = core.{sourceFilter}({source})",
            "",
            "clip.set_output(0)",
            "");
    }

    private static string RequireScriptPath(string? scriptPath)
    {
        var fullPath = McpPaths.RequireAbsolute(scriptPath, nameof(scriptPath));
        var extension = Path.GetExtension(fullPath);
        if (!string.Equals(extension, ".vpy", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".py", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException("VapourSynth scripts must end in .vpy or .py.");
        }

        return fullPath;
    }

    private static async Task<(string? Path, string Content)> ResolveScriptAsync(string? scriptPath, string? content, CancellationToken cancellationToken)
    {
        if (content is not null)
        {
            return (string.IsNullOrWhiteSpace(scriptPath) ? null : McpPaths.RequireAbsolute(scriptPath, nameof(scriptPath)), content);
        }

        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            throw new McpException("Pass scriptPath or content.");
        }

        var fullPath = McpPaths.RequireExistingFile(scriptPath, nameof(scriptPath));
        return (fullPath, await File.ReadAllTextAsync(fullPath, cancellationToken));
    }

    private static Task<VapourSynthPreviewSessionInfo> OpenAsync(
        IVapourSynthPreviewService previewService,
        string? scriptPath,
        string content,
        CancellationToken cancellationToken)
    {
        var workingDirectory = scriptPath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(scriptPath)!;
        return McpToolGuard.RunAsync(() => previewService.OpenSessionAsync(
            new VapourSynthPreviewOpenRequest(
                scriptPath,
                scriptPath is null ? "inline.vpy" : Path.GetFileName(scriptPath),
                content,
                workingDirectory),
            cancellationToken));
    }

    private static async Task CloseQuietlyAsync(IVapourSynthPreviewService previewService)
    {
        try
        {
            if (previewService.HasActiveSession)
            {
                await previewService.CloseSessionAsync(CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    private static VapourSynthPreviewOutputInfo FindOutput(VapourSynthPreviewSessionInfo session, int outputIndex)
    {
        return session.Outputs.FirstOrDefault(output => output.Index == outputIndex)
            ?? throw new McpException(session.Outputs.Count == 0
                ? "The script has no outputs. Call clip.set_output()."
                : $"Output {outputIndex} does not exist. Available: {string.Join(", ", session.Outputs.Select(static output => output.Index))}.");
    }

    internal static int ResolveFrame(VapourSynthPreviewOutputInfo output, int? frame, string? timecode)
    {
        var lastFrame = Math.Max(0, output.TotalFrames - 1);
        if (!string.IsNullOrWhiteSpace(timecode))
        {
            if (!ChapterFile.TryParseTimecode(timecode, out var parsed))
            {
                throw new McpException($"'{timecode}' is not a valid timecode (use hh:mm:ss.fff).");
            }

            return ChapterFile.TimecodeToFrame(parsed, output.FpsNumerator, output.FpsDenominator, output.TotalFrames);
        }

        var requested = frame ?? 0;
        if (requested < 0 || requested > lastFrame)
        {
            throw new McpException($"Frame {requested} is out of range (0-{lastFrame}).");
        }

        return requested;
    }

    private static async Task<string> SavePngAsync(
        byte[] png,
        string? savePath,
        string? scriptPath,
        int outputIndex,
        int frameNumber,
        CancellationToken cancellationToken,
        string suffix = "frame")
    {
        string fullPath;
        if (string.IsNullOrWhiteSpace(savePath))
        {
            var baseName = scriptPath is null ? "inline" : Path.GetFileNameWithoutExtension(scriptPath);
            var directory = Path.Combine(Path.GetTempPath(), "FlowEncode", "mcp-frames");
            Directory.CreateDirectory(directory);
            fullPath = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{baseName}.out{outputIndex}.{suffix}{frameNumber}.{DateTime.Now:HHmmssfff}.png"));
        }
        else
        {
            fullPath = McpPaths.RequireAbsolute(savePath, nameof(savePath));
            if (!string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase))
            {
                fullPath += ".png";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        }

        await File.WriteAllBytesAsync(fullPath, png, cancellationToken);
        return fullPath;
    }

    private static CallToolResult BuildImageResult<T>(T summary, byte[] png, int pixelCount, bool includeImage)
    {
        var content = new List<ContentBlock>
        {
            new TextContentBlock { Text = JsonSerializer.Serialize(summary, McpJson.Options) }
        };

        if (includeImage && pixelCount <= MaxInlineImagePixels)
        {
            content.Add(ImageContentBlock.FromBytes(png, "image/png"));
        }
        else if (includeImage)
        {
            content.Add(new TextContentBlock { Text = "The image is larger than 1920x1080, so it was saved to disk but not returned inline." });
        }

        return new CallToolResult { Content = content };
    }
}

public sealed class CropOptions
{
    [Description("absolute or relative (default).")]
    public string? Mode { get; init; }

    public int Left { get; init; }

    public int Top { get; init; }

    [Description("Relative mode: pixels removed from the right edge.")]
    public int Right { get; init; }

    [Description("Relative mode: pixels removed from the bottom edge.")]
    public int Bottom { get; init; }

    [Description("Absolute mode: width of the kept area.")]
    public int Width { get; init; }

    [Description("Absolute mode: height of the kept area.")]
    public int Height { get; init; }

    internal PreviewFrameCropSettings ToSettings()
    {
        var absolute = string.Equals(Mode?.Trim(), "absolute", StringComparison.OrdinalIgnoreCase);
        if (absolute && (Width <= 0 || Height <= 0))
        {
            throw new McpException("Absolute crop needs a positive width and height.");
        }

        return new PreviewFrameCropSettings(
            true,
            absolute ? PreviewCropMode.Absolute : PreviewCropMode.Relative,
            new PreviewAbsoluteCrop(Left, Top, Width, Height),
            new PreviewRelativeCrop(Left, Top, Right, Bottom));
    }
}

public sealed class CompareSource
{
    [Description("Absolute path of the script.")]
    public string? ScriptPath { get; init; }

    [Description("Inline script text instead of a file.")]
    public string? Content { get; init; }

    [Description("Output index. Defaults to 0.")]
    public int? OutputIndex { get; init; }
}

public sealed class ChapterInput
{
    [Description("hh:mm:ss.fff")]
    public string Timecode { get; init; } = string.Empty;

    public string? Title { get; init; }
}

public sealed record ScriptDocumentView(string? FilePath, string Content, int LineCount)
{
    public static ScriptDocumentView From(VapourSynthWorkspaceDocument document) => new(
        document.FilePath,
        document.Content,
        document.Content.Length == 0 ? 0 : document.Content.Split('\n').Length);
}

public sealed record VapourSynthFunctionCatalog(
    bool IsRuntimeReady,
    string RuntimeSummary,
    IReadOnlyList<string> Namespaces,
    int MatchCount,
    IReadOnlyList<VapourSynthFunctionView> Functions);

public sealed record VapourSynthFunctionView(
    string Name,
    string Signature,
    string ReturnType,
    string? Documentation);

public sealed record RenderedFrameView(
    string ImagePath,
    int OutputIndex,
    int Frame,
    string Timecode,
    int SourceWidth,
    int SourceHeight,
    int ImageWidth,
    int ImageHeight,
    string? FrameType,
    IReadOnlyDictionary<string, string> Properties,
    string? CropCommand,
    PreviewCropBounds? CropBounds);

public sealed record CompareFramesView(
    string ImagePath,
    string Layout,
    IReadOnlyList<RenderedFrameView> Frames);

public sealed record ChapterView(int Number, string Timecode, string Title, int? Frame);
