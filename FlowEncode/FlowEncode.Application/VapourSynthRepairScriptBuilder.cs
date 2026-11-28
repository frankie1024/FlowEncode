using System;
using System.Collections.Generic;
using System.Linq;

namespace FlowEncode.Application;

public sealed record PreviewRepairAnalysisResult(
    int SampledCount,
    bool VariableCrop,
    string? BandingScanFailure,
    IReadOnlyList<PreviewRepairSuggestion> Suggestions,
    int ChapterCount = 0,
    int WindowCount = 0,
    bool HasPgs = false,
    string? FieldScanFailure = null,
    bool Deep = false,
    bool Truncated = false,
    int PlaylistSeamCount = 0,
    bool PlaylistMismatch = false);

public sealed record PreviewRepairRange(int Start, int End);

public sealed record PreviewRepairSuggestion(
    string Kind,
    int[] Frames,
    string[] Statements,
    int[] Edges,
    string? Failure = null,
    string? Method = null,
    int[]? Remainder = null,
    string[]? AlphaStatements = null,
    PreviewRepairRange[]? Ranges = null,
    string? Evidence = null,
    int[]? Region = null,
    double? Value = null,
    int? Total = null,
    string? Scope = null,
    int ScopeHits = 0,
    int ScopeSamples = 0)
{
    public bool CanApply => Failure is null && Statements.Length > 0;
    public bool ApplyToRanges => Kind is "aa" or "deband" or "interlace" or "local-edge" && Ranges is { Length: > 0 };
    public bool UsesCroppedEdges => Kind is "edge" or "local-edge" or "chroma-edge";
}

public static class VapourSynthRepairScriptBuilder
{
    public static SmartScriptEditResult Build(
        string script,
        int outputIndex,
        IReadOnlyList<PreviewRepairSuggestion> selected,
        bool compare = false)
    {
        if (selected.Count == 0 || selected.Any(static repair => !repair.CanApply))
        {
            throw new ArgumentException("Select at least one verified repair.", nameof(selected));
        }

        var originalScript = script.TrimEnd('\r', '\n');
        var newline = script.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var invariantOutputIndex = outputIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hasLocalRepairs = selected.Any(static repair => repair.ApplyToRanges);
        var lines = new List<string>
        {
            "# FlowEncode picture repairs",
            "import vapoursynth as _fe_repair_vs",
            $"_fe_repair_output = _fe_repair_vs.get_output({invariantOutputIndex})",
            "",
        };
        if (hasLocalRepairs)
        {
            lines.AddRange([
                "def _fe_repair_ranges(original, processed, ranges):",
                "    parts = []",
                "    cursor = 0",
                "    for start, end in ranges:",
                "        if cursor < start:",
                "            parts.append(original[cursor:start])",
                "        parts.append(processed[start:end + 1])",
                "        cursor = end + 1",
                "    if cursor < original.num_frames:",
                "        parts.append(original[cursor:])",
                "    return _fe_repair_vs.core.std.Splice(parts)",
                "",
            ]);
        }

        lines.Add("def _fe_apply_picture_repairs(_fe_repair_clip):");
        foreach (var repair in selected.OrderBy(static repair => repair.Kind switch
                 {
                     "crop" => 0,
                     "edge" or "local-edge" or "chroma-edge" => 1,
                     "dedot" => 2,
                     "aa" => 3,
                     "deband" => 4,
                     "cas" => 5,
                     "interlace" => -1,
                     _ => 6
                 }))
        {
            if (repair.ApplyToRanges)
            {
                lines.Add("    _fe_repair_original = _fe_repair_clip");
            }
            lines.AddRange(repair.Statements.Select(static statement => $"    {statement}"));
            if (repair.ApplyToRanges)
            {
                var ranges = string.Join(", ", MergeRanges(repair.Ranges!)
                    .Select(static range => FormattableString.Invariant($"({range.Start}, {range.End})")));
                lines.Add($"    _fe_repair_clip = _fe_repair_ranges(_fe_repair_original, _fe_repair_clip, [{ranges}])");
            }
        }

        lines.Add("    return _fe_repair_clip");
        lines.Add("");
        lines.Add("_fe_repaired = _fe_apply_picture_repairs(_fe_repair_output.clip)");
        if (compare)
        {
            lines.Add("_fe_repair_vs.clear_outputs()");
            lines.Add("_fe_repair_output.clip.set_output(0)");
            lines.Add("_fe_repaired.set_output(1)");
        }
        else
        {
            lines.Add("_fe_repair_alpha = _fe_repair_output.alpha");
            if (selected.FirstOrDefault(static repair => repair.Kind == "crop")?.AlphaStatements is { Length: > 0 } alphaStatements)
            {
                lines.Add("if _fe_repair_alpha is not None:");
                lines.AddRange(alphaStatements.Select(static statement => $"    {statement}"));
            }

            lines.Add($"_fe_repaired.set_output({invariantOutputIndex}, alpha=_fe_repair_alpha, alt_output=_fe_repair_output.alt_output)");
        }

        return new SmartScriptEditResult(
            originalScript + newline + newline + string.Join(newline, lines) + newline,
            originalScript.Count(static character => character == '\n') + 3,
            "_fe_repaired",
            UpdatedInPlace: false,
            AddedImport: true);
    }

    private static List<PreviewRepairRange> MergeRanges(IEnumerable<PreviewRepairRange> ranges)
    {
        var merged = new List<PreviewRepairRange>();
        foreach (var range in ranges.OrderBy(static item => item.Start))
        {
            if (merged.Count > 0 && range.Start <= (long)merged[^1].End + 1)
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, range.End) };
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }

    public static string FormatSnippet(IReadOnlyList<PreviewRepairSuggestion> selected, string clipVariable = "clip") =>
        string.Join(Environment.NewLine, selected.SelectMany(static suggestion => suggestion.Statements)
            .Select(statement => statement.Replace("_fe_repair_clip", clipVariable, StringComparison.Ordinal)));
}
