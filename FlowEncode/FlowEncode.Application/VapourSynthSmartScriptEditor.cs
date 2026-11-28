using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlowEncode.Application;

public sealed record SmartScriptOutputTarget(
    string VariableName,
    int SetOutputLineIndex,
    int OutputIndex,
    bool FoundSetOutput);

public sealed record SmartScriptEditResult(
    string UpdatedScript,
    int TargetLineNumber,
    string ResolvedVariableName,
    bool UpdatedInPlace,
    bool AddedImport);

public static partial class VapourSynthSmartScriptEditor
{
    private static readonly Regex SetOutputMethodRegex = new(
        @"^(?<indent>[ \t]*)(?<var>[A-Za-z_][A-Za-z0-9_]*)\.set_output\s*\((?<args>[^)]*)\)",
        RegexOptions.Compiled);

    private static readonly Regex AssignmentRegex = new(
        @"^(?<indent>[ \t]*)(?<var>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<rhs>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex ImportModuleAliasRegex = new(
        @"^[ \t]*import\s+(?<mod>[A-Za-z_][A-Za-z0-9_\.]*)(?:\s+as\s+(?<alias>[A-Za-z_][A-Za-z0-9_]*))?\s*(?:#.*)?$",
        RegexOptions.Compiled);

    private static readonly Regex FromImportRegex = new(
        @"^[ \t]*from\s+(?<mod>[A-Za-z_][A-Za-z0-9_\.]*)\s+import\s+(?<names>.+?)\s*(?:#.*)?$",
        RegexOptions.Compiled);

    private const string SmartSamplingMarker = "# [FlowEncode Smart Sampling]";
    private static readonly Regex SmartSamplingSwitchRegex = new(
        $@"^[ \t]*(?<variable>[A-Za-z_]\w*)_enabled[ \t]*=[ \t]*(?<enabled>True|False)[ \t]+{Regex.Escape(SmartSamplingMarker)}[ \t]*\r?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex GeneratedVideoSourcePathRegex = new(
        """
        ^
        (?:
            file [ \t]* = [ \t]* (?<literal>[rR]"[^"\r\n]+"|"(?:[^"\\\r\n]|\\.)+") [ \t]* \r?\n
            \w+ [ \t]* = [ \t]* core\.(?:lsmas\.LWLibavSource|ffms2\.Source)\( [ \t]* file [ \t]* \)
            |
            \w+ [ \t]* = [ \t]* \w+\.BestSource\(\)\.source\( (?<literal>[rR]"[^"\r\n]+"), [ \t]* bits=None \)
        )
        [ \t]* \r?$
        """,
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnorePatternWhitespace);

    private static readonly HashSet<string> VideoSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".mov", ".m4v", ".webm", ".avi", ".flv", ".y4m", ".yuv", ".avs",
        ".m2ts", ".mts", ".ts", ".vob", ".mpg", ".mpeg", ".m2v", ".mpv", ".vc1",
        ".avc", ".h264", ".264", ".hevc", ".h265", ".265", ".ivf", ".obu"
    };

    private static readonly HashSet<string> NonClipVariableNames = new(StringComparer.Ordinal)
    {
        "core", "vs", "os", "sys", "math", "Path", "path"
    };

    public static SmartScriptOutputTarget ResolveOutputTarget(string script, int outputIndex = 0)
    {
        var lines = SplitLines(script ?? string.Empty, out _);
        SmartScriptOutputTarget? matched = null;
        SmartScriptOutputTarget? firstAnyOutput = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var match = SetOutputMethodRegex.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var varName = match.Groups["var"].Value;
            var parsedIndex = ParseSetOutputIndex(match.Groups["args"].Value);
            var candidate = new SmartScriptOutputTarget(varName, i, parsedIndex, FoundSetOutput: true);
            firstAnyOutput ??= candidate;
            if (parsedIndex == outputIndex)
            {
                matched = candidate;
            }
        }

        if (matched is not null)
        {
            return matched;
        }

        if (firstAnyOutput is not null && outputIndex == 0)
        {
            return firstAnyOutput;
        }

        // Fall back to the last top-level clip-like assignment before the end of the script.
        string? lastAssignedVar = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var assign = AssignmentRegex.Match(lines[i]);
            if (assign.Success
                && assign.Groups["indent"].Value.Length == 0
                && !NonClipVariableNames.Contains(assign.Groups["var"].Value))
            {
                lastAssignedVar = assign.Groups["var"].Value;
            }
        }

        return new SmartScriptOutputTarget(
            lastAssignedVar ?? (outputIndex == 0 ? "clip" : $"clip_{outputIndex}"),
            lines.Count,
            outputIndex,
            FoundSetOutput: false);
    }

    public static SmartScriptEditResult ApplyCrop(
        string script,
        int outputIndex,
        bool isAbsolute,
        int left,
        int top,
        int widthOrRight,
        int heightOrBottom)
    {
        var lines = SplitLines(script ?? string.Empty, out var newline);
        var addedImport = EnsureVapourSynthRuntime(lines, needsCore: true, needsVs: false, out _);
        var target = ResolveOutputTarget(string.Join(newline, lines), outputIndex);
        var varName = target.VariableName;
        var statement = isAbsolute
            ? $"{varName} = core.std.CropAbs({varName}, width={widthOrRight}, height={heightOrBottom}, left={left}, top={top})"
            : $"{varName} = core.std.CropRel({varName}, left={left}, right={widthOrRight}, top={top}, bottom={heightOrBottom})";

        // Update existing crop statement for this same variable in place if present before set_output.
        var existingCropRegex = new Regex(
            $@"^(?<indent>[ \t]*){Regex.Escape(varName)}\s*=\s*(?:core\.std\.Crop(?:Rel|Abs)?|{Regex.Escape(varName)}\.std\.Crop(?:Rel|Abs)?)\s*\(",
            RegexOptions.Compiled);

        var searchLimit = Math.Min(target.SetOutputLineIndex, lines.Count);
        for (var i = searchLimit - 1; i >= 0; i--)
        {
            var m = existingCropRegex.Match(lines[i]);
            if (m.Success)
            {
                var endIdx = FindStatementEndLineIndex(lines, i);
                lines[i] = m.Groups["indent"].Value + statement;
                if (endIdx > i)
                {
                    lines.RemoveRange(i + 1, endIdx - i);
                }

                return new SmartScriptEditResult(
                    string.Join(newline, lines),
                    i + 1,
                    varName,
                    UpdatedInPlace: true,
                    AddedImport: addedImport);
            }
        }

        addedImport |= EnsureBaseClipIfMissing(lines, ref target, out var appendOutputAfterInsert);
        var insertAt = InsertBeforeOutput(lines, target.SetOutputLineIndex, [statement]);
        if (appendOutputAfterInsert)
        {
            lines.Insert(insertAt + 1, $"{varName}.set_output()");
        }

        return new SmartScriptEditResult(
            string.Join(newline, lines),
            insertAt + 1,
            varName,
            UpdatedInPlace: false,
            AddedImport: addedImport);
    }

    public static SmartScriptEditResult ApplyCropAndBbmod(
        string script,
        int outputIndex,
        int left,
        int top,
        int right,
        int bottom)
    {
        var lines = SplitLines(script ?? string.Empty, out var newline);
        var needsCrop = left > 0 || top > 0 || right > 0 || bottom > 0;
        var addedImport = EnsureVapourSynthRuntime(lines, needsCore: needsCrop, needsVs: false, out _);
        addedImport |= EnsureImportModule(lines, "awsmfunc", "awf", out var alias);
        var target = ResolveOutputTarget(string.Join(newline, lines), outputIndex);
        addedImport |= EnsureBaseClipIfMissing(lines, ref target, out var appendOutputAfterInsert);
        var varName = target.VariableName;

        var statements = new List<string>();
        if (needsCrop)
        {
            statements.Add($"{varName} = core.std.CropRel({varName}, left={left}, right={right}, top={top}, bottom={bottom})");
        }

        statements.Add($"{varName} = {alias}.bbmod({varName}, top=2, bottom=2, left=2, right=2, thresh=32, blur=20)");
        var insertAt = InsertBeforeOutput(lines, target.SetOutputLineIndex, statements);
        if (appendOutputAfterInsert)
        {
            lines.Insert(insertAt + statements.Count, $"{varName}.set_output()");
        }

        return new SmartScriptEditResult(
            string.Join(newline, lines),
            insertAt + 1,
            varName,
            UpdatedInPlace: false,
            AddedImport: addedImport);
    }

    public static SmartScriptEditResult ApplyAlignment(
        string script,
        int outputIndex,
        int frameOffset,
        double subpixelOffsetX,
        double subpixelOffsetY)
    {
        var lines = SplitLines(script ?? string.Empty, out var newline);
        var target = ResolveOutputTarget(script ?? string.Empty, outputIndex);
        var varName = target.VariableName;

        var statements = new List<string>();
        if (frameOffset > 0)
        {
            statements.Add($"{varName} = {varName}[{frameOffset}:]");
        }
        else if (frameOffset < 0)
        {
            statements.Add($"{varName} = {varName}[0] * {-frameOffset} + {varName}");
        }

        if (Math.Abs(subpixelOffsetX) >= 0.0005 || Math.Abs(subpixelOffsetY) >= 0.0005)
        {
            statements.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{varName} = {varName}.resize.Spline36(src_left={subpixelOffsetX:0.###}, src_top={subpixelOffsetY:0.###})"));
        }

        if (statements.Count == 0)
        {
            return new SmartScriptEditResult(script ?? string.Empty, Math.Max(1, target.SetOutputLineIndex), varName, false, false);
        }

        var addedImport = EnsureBaseClipIfMissing(lines, ref target, out var appendOutputAfterInsert);
        var insertAt = InsertBeforeOutput(lines, target.SetOutputLineIndex, statements);
        if (appendOutputAfterInsert)
        {
            lines.Insert(insertAt + statements.Count, $"{varName}.set_output()");
        }

        return new SmartScriptEditResult(
            string.Join(newline, lines),
            insertAt + 1,
            varName,
            UpdatedInPlace: false,
            AddedImport: addedImport);
    }

    public static SmartScriptEditResult ApplyReplaceRanges(
        string script,
        int outputIndex,
        IReadOnlyList<PreviewSceneRange> ranges,
        string filteredVariable = "flt")
    {
        var lines = SplitLines(script ?? string.Empty, out var newline);
        var addedImport = EnsureFromImportSymbol(lines, "vstools", "replace_ranges", out var resolvedFunc);
        var target = ResolveOutputTarget(string.Join(newline, lines), outputIndex);
        addedImport |= EnsureBaseClipIfMissing(lines, ref target, out var appendOutputAfterInsert);
        var varName = target.VariableName;

        var statements = new List<string>(2);
        if (!IsVariableDefinedBefore(lines, filteredVariable, target.SetOutputLineIndex))
        {
            statements.Add($"{filteredVariable} = {varName}  # 在此替换为目标滤镜处理后的画面");
        }

        var formatted = PreviewSceningFormatter.FormatReplaceRanges(ranges, varName, filteredVariable);
        if (!string.Equals(resolvedFunc, "replace_ranges", StringComparison.Ordinal))
        {
            formatted = formatted.Replace("= replace_ranges(", $"= {resolvedFunc}(", StringComparison.Ordinal);
        }

        statements.Add(formatted);
        var insertAt = InsertBeforeOutput(lines, target.SetOutputLineIndex, statements);
        if (appendOutputAfterInsert)
        {
            lines.Insert(insertAt + statements.Count, $"{varName}.set_output()");
        }

        return new SmartScriptEditResult(
            string.Join(newline, lines),
            insertAt + 1,
            varName,
            UpdatedInPlace: false,
            AddedImport: addedImport);
    }

    public static SmartScriptEditResult ApplyMaskedMerge(
        string script,
        int outputIndex,
        string filteredVariable = "flt")
    {
        var lines = SplitLines(script ?? string.Empty, out var newline);
        var addedImport = EnsureVapourSynthRuntime(lines, needsCore: true, needsVs: false, out _);
        var target = ResolveOutputTarget(string.Join(newline, lines), outputIndex);
        addedImport |= EnsureBaseClipIfMissing(lines, ref target, out var appendOutputAfterInsert);
        var varName = target.VariableName;

        var statements = new List<string>(3);
        if (!IsVariableDefinedBefore(lines, filteredVariable, target.SetOutputLineIndex))
        {
            statements.Add($"{filteredVariable} = {varName}  # 在此替换为目标滤镜处理后的画面");
        }

        statements.Add($"mask = {varName}.std.Prewitt().std.Maximum()");
        statements.Add($"{varName} = core.std.MaskedMerge({filteredVariable}, {varName}, mask)");
        var insertAt = InsertBeforeOutput(lines, target.SetOutputLineIndex, statements);
        if (appendOutputAfterInsert)
        {
            lines.Insert(insertAt + statements.Count, $"{varName}.set_output()");
        }

        return new SmartScriptEditResult(
            string.Join(newline, lines),
            insertAt + 1,
            varName,
            UpdatedInPlace: false,
            AddedImport: addedImport);
    }

    public static SmartScriptEditResult ApplyDerivedVariableAndRewireOutput(
        string script,
        int outputIndex,
        string newVariableName,
        string expressionTemplateWithPlaceholder)
    {
        return ApplyFilterTemplate(
            script,
            outputIndex,
            expressionTemplateWithPlaceholder,
            derivedVariableName: newVariableName);
    }

    public static SmartScriptEditResult ApplyFilterTemplate(
        string script,
        int outputIndex,
        string expressionTemplateWithPlaceholder,
        string? importModule = null,
        string? importAlias = null,
        string? fromModule = null,
        string? fromSymbol = null,
        string? derivedVariableName = null,
        IReadOnlyList<string>? setup = null,
        IReadOnlyList<string>? comments = null)
    {
        return ApplyMultiLineFilterTemplate(
            script, outputIndex, [.. setup ?? [], "{target} = " + expressionTemplateWithPlaceholder],
            importModule, importAlias, fromModule, fromSymbol, derivedVariableName, comments);
    }

    public static bool IsSmartSamplingEnabled(string script) =>
        SmartSamplingSwitchRegex.Match(script).Groups["enabled"].Value == "True";

    public static SmartScriptEditResult ToggleSmartSampling(string script, IReadOnlyList<string>? comments = null)
    {
        var existing = SmartSamplingSwitchRegex.Match(script);
        if (existing.Success)
        {
            var enabled = existing.Groups["enabled"];
            var value = enabled.Value == "True" ? "False" : "True";
            return new SmartScriptEditResult(
                script[..enabled.Index] + value + script[(enabled.Index + enabled.Length)..],
                script[..existing.Index].Count(static character => character == '\n') + 1,
                existing.Groups["variable"].Value, UpdatedInPlace: true, AddedImport: false);
        }

        var names = Regex.Matches(script, @"\b[A-Za-z_]\w*\b").Select(static match => match.Value).ToHashSet(StringComparer.Ordinal);
        var sample = "sample";
        for (var suffix = 2; names.Contains(sample) || names.Contains($"{sample}_enabled"); suffix++)
        {
            sample = $"sample{suffix}";
        }

        var alias = "awf";
        for (var suffix = 2; names.Contains(alias); suffix++)
        {
            alias = $"awf{suffix}";
        }

        var target = ResolveOutputTarget(script);
        if (!target.FoundSetOutput || target.OutputIndex != 0)
        {
            var lines = SplitLines(script, out var newline);
            var insertAt = target.FoundSetOutput ? target.SetOutputLineIndex + 1 : lines.Count;
            var indent = target.FoundSetOutput ? SetOutputMethodRegex.Match(lines[target.SetOutputLineIndex]).Groups["indent"].Value : string.Empty;
            lines.Insert(insertAt, $"{indent}{target.VariableName}.set_output()");
            script = string.Join(newline, lines);
        }

        return ApplyMultiLineFilterTemplate(script, 0,
            [
                $"{sample}_enabled = True  {SmartSamplingMarker}",
                "{target} = {var}",
                $"if {sample}_enabled:",
                "    {target} = {alias}.SelectRangeEvery({var}, every=3000, length=100, offset=[5000, 5000])"
            ],
            importModule: "awsmfunc", importAlias: alias, derivedVariableName: sample, comments: comments);
    }

    public static SmartScriptEditResult ApplyMultiLineFilterTemplate(
        string script,
        int outputIndex,
        IReadOnlyList<string> statementTemplatesWithPlaceholders,
        string? importModule = null,
        string? importAlias = null,
        string? fromModule = null,
        string? fromSymbol = null,
        string? derivedVariableName = null,
        IReadOnlyList<string>? comments = null)
    {
        var lines = SplitLines(script ?? string.Empty, out var newline);
        var addedImport = false;
        var resolvedAlias = importAlias ?? importModule ?? string.Empty;
        var resolvedSymbol = fromSymbol ?? string.Empty;

        var needsCore = statementTemplatesWithPlaceholders.Any(static s => s.Contains("core.", StringComparison.Ordinal));
        var needsVs = statementTemplatesWithPlaceholders.Any(static s => s.Contains("vs.", StringComparison.Ordinal))
            || string.Equals(importModule, "vapoursynth", StringComparison.Ordinal);
        addedImport |= EnsureVapourSynthRuntime(lines, needsCore, needsVs, out var vsAlias);

        if (!string.IsNullOrWhiteSpace(importModule) && !string.Equals(importModule, "vapoursynth", StringComparison.Ordinal))
        {
            addedImport |= EnsureImportModule(
                lines,
                importModule,
                string.IsNullOrWhiteSpace(importAlias) ? importModule : importAlias,
                out resolvedAlias);
        }
        else if (string.Equals(importModule, "vapoursynth", StringComparison.Ordinal))
        {
            resolvedAlias = vsAlias;
        }
        else if (!string.IsNullOrWhiteSpace(fromModule) && !string.IsNullOrWhiteSpace(fromSymbol))
        {
            addedImport |= EnsureFromImportSymbol(lines, fromModule, fromSymbol, out resolvedSymbol);
        }

        var target = ResolveOutputTarget(string.Join(newline, lines), outputIndex);
        addedImport |= EnsureBaseClipIfMissing(lines, ref target, out var appendOutputAfterInsert);
        var oldVar = target.VariableName;
        var targetVar = string.IsNullOrWhiteSpace(derivedVariableName) ? oldVar : derivedVariableName.Trim();

        const string filteredVariable = "flt";
        var temp = oldVar + "_work";
        for (var suffix = 2; IsVariableDefinedBefore(lines, temp, target.SetOutputLineIndex); suffix++)
            temp = $"{oldVar}_work{suffix}";

        var statements = new List<string>(statementTemplatesWithPlaceholders.Count + 1);
        if (statementTemplatesWithPlaceholders.Any(static statement => statement.Contains("{filtered}", StringComparison.Ordinal))
            && !IsVariableDefinedBefore(lines, filteredVariable, target.SetOutputLineIndex))
        {
            statements.Add($"{filteredVariable} = {oldVar}  # Replace with the filtered clip");
        }

        var firstCodeOffset = 0;
        if (comments is { Count: > 0 })
        {
            statements.AddRange(comments.Select(static comment => "# " + comment));
            firstCodeOffset = statements.Count;
        }

        foreach (var template in statementTemplatesWithPlaceholders)
        {
            var expanded = template
                .Replace("{var}", oldVar, StringComparison.Ordinal)
                .Replace("{target}", targetVar, StringComparison.Ordinal)
                .Replace("{temp}", temp, StringComparison.Ordinal)
                .Replace("{filtered}", filteredVariable, StringComparison.Ordinal)
                .Replace("{alias}", resolvedAlias, StringComparison.Ordinal)
                .Replace("{symbol}", resolvedSymbol, StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(vsAlias) && !string.Equals(vsAlias, "vs", StringComparison.Ordinal))
                expanded = Regex.Replace(expanded, @"\bvs\.", $"{vsAlias}.");
            if (!string.IsNullOrWhiteSpace(fromSymbol) && !string.IsNullOrWhiteSpace(resolvedSymbol)
                && !string.Equals(fromSymbol, resolvedSymbol, StringComparison.Ordinal))
            {
                expanded = Regex.Replace(expanded, $@"\b{Regex.Escape(fromSymbol)}\b", resolvedSymbol);
            }
            statements.Add(expanded);
        }

        var insertAt = InsertBeforeOutput(lines, target.SetOutputLineIndex, statements);
        if (appendOutputAfterInsert)
        {
            lines.Insert(insertAt + statements.Count, $"{targetVar}.set_output()");
        }
        else if (target.FoundSetOutput && !string.Equals(targetVar, oldVar, StringComparison.Ordinal))
        {
            var newOutputLineIndex = target.SetOutputLineIndex + statements.Count;
            if (newOutputLineIndex < lines.Count)
            {
                var line = lines[newOutputLineIndex];
                var match = SetOutputMethodRegex.Match(line);
                if (match.Success && string.Equals(match.Groups["var"].Value, oldVar, StringComparison.Ordinal))
                {
                    lines[newOutputLineIndex] = $"{match.Groups["indent"].Value}{targetVar}.set_output({match.Groups["args"].Value})";
                }
            }
        }

        return new SmartScriptEditResult(
            string.Join(newline, lines),
            insertAt + firstCodeOffset + 1,
            targetVar,
            UpdatedInPlace: false,
            AddedImport: addedImport);
    }

    public static bool IsSupportedVideoSourcePath(string filePath) => VideoSourceExtensions.Contains(Path.GetExtension(filePath));

    public static SmartScriptEditResult BuildVideoSourceScript(string videoFilePath)
    {
        var path = Path.GetFullPath(videoFilePath);
        var literal = path.Any(static character => character is '"' or '\r' or '\n') || path.EndsWith('\\')
            ? JsonSerializer.Serialize(path)
            : $"R\"{path}\"";
        // L-SMASH Works can return no clip for elementary VC-1 streams.
        var loader = string.Equals(Path.GetExtension(path), ".vc1", StringComparison.OrdinalIgnoreCase)
            ? "core.ffms2.Source"
            : "core.lsmas.LWLibavSource";
        var script = string.Join('\n',
            "from vstools import core",
            $"file = {literal}",
            $"src = {loader}(file)",
            "src.set_output()");
        return new SmartScriptEditResult(script, 3, "src", UpdatedInPlace: false, AddedImport: true);
    }

    public static string? FindGeneratedVideoSourcePath(string script)
    {
        var matches = GeneratedVideoSourcePathRegex.Matches(script);
        if (matches.Count != 1)
        {
            return null;
        }

        var literal = matches[0].Groups["literal"].Value;
        if (literal[0] is 'r' or 'R')
        {
            return literal[2..^1];
        }

        try
        {
            return JsonSerializer.Deserialize<string>(literal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private const string AbCompareMarker = "# [FlowEncode A/B]";
    private const string AbCompareSavedPrefix = "# [FlowEncode A/B saved] ";

    public static SmartScriptEditResult ToggleBeforeAfterCompare(string script, int cursorLineNumber)
    {
        var lines = SplitLines(script ?? string.Empty, out var newline);
        var hasAbBlock = lines.Any(static l => l.Contains(AbCompareMarker, StringComparison.Ordinal)
            || l.TrimStart().StartsWith(AbCompareSavedPrefix, StringComparison.Ordinal));

        if (hasAbBlock)
        {
            var restored = new List<string>(lines.Count);
            var firstRestoredLine = 1;
            var foundSaved = false;
            foreach (var line in lines)
            {
                if (line.Contains(AbCompareMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                var trimmedStart = line.TrimStart();
                if (trimmedStart.StartsWith(AbCompareSavedPrefix, StringComparison.Ordinal))
                {
                    var leading = line[..(line.Length - trimmedStart.Length)];
                    var original = trimmedStart[AbCompareSavedPrefix.Length..];
                    restored.Add(leading + original);
                    if (!foundSaved)
                    {
                        firstRestoredLine = restored.Count;
                        foundSaved = true;
                    }
                    continue;
                }

                restored.Add(line);
            }

            var targetAfterRestore = ResolveOutputTarget(string.Join(newline, restored), 0);
            return new SmartScriptEditResult(
                string.Join(newline, restored),
                firstRestoredLine,
                targetAfterRestore.VariableName,
                UpdatedInPlace: true,
                AddedImport: false);
        }

        // Locate candidate filter assignment at cursorLineNumber or fallback to the last assignment before set_output.
        var outputTarget = ResolveOutputTarget(script ?? string.Empty, 0);
        var limit = Math.Min(outputTarget.SetOutputLineIndex, lines.Count);
        var targetLineIdx = -1;

        var clampedCursorIdx = Math.Clamp(cursorLineNumber - 1, 0, Math.Max(0, lines.Count - 1));
        if (clampedCursorIdx < limit && IsFilterAssignmentLine(lines[clampedCursorIdx]))
        {
            targetLineIdx = clampedCursorIdx;
        }
        else
        {
            for (var i = Math.Min(clampedCursorIdx, limit - 1); i >= 0; i--)
            {
                if (IsFilterAssignmentLine(lines[i]))
                {
                    targetLineIdx = i;
                    break;
                }
            }

            if (targetLineIdx < 0)
            {
                for (var i = limit - 1; i >= 0; i--)
                {
                    if (IsFilterAssignmentLine(lines[i]))
                    {
                        targetLineIdx = i;
                        break;
                    }
                }
            }
        }

        if (targetLineIdx < 0)
        {
            return new SmartScriptEditResult(script ?? string.Empty, Math.Max(1, cursorLineNumber), outputTarget.VariableName, false, false);
        }

        var filterMatch = AssignmentRegex.Match(lines[targetLineIdx]);
        var afterVar = filterMatch.Groups["var"].Value;
        var rhs = filterMatch.Groups["rhs"].Value;

        // Collect clip variables defined strictly before targetLineIdx to determine the "before" clip variable.
        var earlierVars = new List<string>();
        for (var i = 0; i < targetLineIdx; i++)
        {
            var m = AssignmentRegex.Match(lines[i]);
            if (m.Success && m.Groups["indent"].Value.Length == 0 && !NonClipVariableNames.Contains(m.Groups["var"].Value))
            {
                earlierVars.Add(m.Groups["var"].Value);
            }
        }

        if (earlierVars.Count == 0)
        {
            return new SmartScriptEditResult(script ?? string.Empty, Math.Max(1, cursorLineNumber), afterVar, false, false);
        }

        string beforeVar;
        if (earlierVars.Contains(afterVar, StringComparer.Ordinal))
        {
            beforeVar = afterVar;
        }
        else
        {
            beforeVar = earlierVars
                .LastOrDefault(v => Regex.IsMatch(rhs, $@"\b{Regex.Escape(v)}\b"))
                ?? earlierVars[^1];
        }

        // Find the end line of this statement (supports multi-line parenthesized filter calls).
        var stmtEndIdx = FindStatementEndLineIndex(lines, targetLineIdx);

        // Comment out existing top-level set_output lines so toggling restores cleanly.
        for (var i = 0; i < lines.Count; i++)
        {
            if (SetOutputMethodRegex.IsMatch(lines[i]))
            {
                var trimmedStart = lines[i].TrimStart();
                var leading = lines[i][..(lines[i].Length - trimmedStart.Length)];
                lines[i] = leading + AbCompareSavedPrefix + trimmedStart;
            }
        }

        lines.Insert(stmtEndIdx + 1, $"_fe_after = {afterVar}  {AbCompareMarker}");
        lines.Insert(targetLineIdx, $"_fe_before = {beforeVar}  {AbCompareMarker}");
        lines.Add($"_fe_before.set_output(0)  {AbCompareMarker}");
        lines.Add($"_fe_after.set_output(1)  {AbCompareMarker}");

        return new SmartScriptEditResult(
            string.Join(newline, lines),
            targetLineIdx + 2,
            $"{beforeVar} vs {afterVar}",
            UpdatedInPlace: false,
            AddedImport: false);
    }

    private static bool EnsureVapourSynthRuntime(
        List<string> lines,
        bool needsCore,
        bool needsVs,
        out string vsAlias)
    {
        vsAlias = "vs";
        if (!needsCore && !needsVs)
        {
            return false;
        }

        var added = false;
        if (lines.Count == 1 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.Clear();
        }

        var hasCore = lines.Any(static l =>
            Regex.IsMatch(l, @"^[ \t]*core\s*=")
            || Regex.IsMatch(l, @"^[ \t]*from\s+vapoursynth\s+import\b.*\bcore\b"));

        var hasVsImport = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var m = ImportModuleAliasRegex.Match(lines[i]);
            if (m.Success && string.Equals(m.Groups["mod"].Value, "vapoursynth", StringComparison.Ordinal))
            {
                vsAlias = m.Groups["alias"].Success ? m.Groups["alias"].Value : "vapoursynth";
                hasVsImport = true;
                break;
            }
        }

        if (needsVs || (needsCore && !hasCore))
        {
            if (!hasVsImport)
            {
                added |= EnsureImportModule(lines, "vapoursynth", "vs", out vsAlias);
            }
        }

        if (needsCore && !hasCore)
        {
            var insertAt = FindAfterLastImportIndex(lines);
            lines.Insert(insertAt, $"core = {vsAlias}.core");
            added = true;
        }

        return added;
    }

    private static bool EnsureBaseClipIfMissing(
        List<string> lines,
        ref SmartScriptOutputTarget target,
        out bool appendOutputAfterInsert)
    {
        appendOutputAfterInsert = false;
        if (IsVariableDefinedBefore(lines, target.VariableName, target.SetOutputLineIndex))
        {
            return false;
        }

        // Script has no clip variable defined yet (e.g. blank tab or imports-only tab): scaffold a valid runnable clip.
        var countBefore = lines.Count;
        if (lines.Count == 1 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.Clear();
            countBefore = 0;
        }

        var added = EnsureVapourSynthRuntime(lines, needsCore: true, needsVs: true, out var vsAlias);

        var insertAt = FindAfterLastImportIndex(lines);
        while (insertAt < lines.Count && Regex.IsMatch(lines[insertAt], @"^[ \t]*core\s*="))
        {
            insertAt++;
        }

        if (insertAt > 0 && (insertAt >= lines.Count || !string.IsNullOrWhiteSpace(lines[insertAt - 1])))
        {
            lines.Insert(insertAt, string.Empty);
            insertAt++;
        }

        lines.Insert(insertAt, $"{target.VariableName} = core.std.BlankClip(width=1920, height=1080, format={vsAlias}.YUV420P8)  # Replace with the source clip");
        var delta = lines.Count - countBefore;
        target = target with
        {
            SetOutputLineIndex = target.FoundSetOutput ? target.SetOutputLineIndex + delta : insertAt + 1
        };
        if (!target.FoundSetOutput)
        {
            appendOutputAfterInsert = true;
        }

        return added || true;
    }

    private static bool IsVariableDefinedBefore(List<string> lines, string variableName, int limitLineIndex)
    {
        if (string.IsNullOrWhiteSpace(variableName))
        {
            return false;
        }

        var maxIdx = Math.Min(limitLineIndex, lines.Count);
        for (var i = 0; i < maxIdx; i++)
        {
            var match = AssignmentRegex.Match(lines[i]);
            if (match.Success && string.Equals(match.Groups["var"].Value, variableName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int FindStatementEndLineIndex(List<string> lines, int startLineIdx)
    {
        var depth = 0;
        for (var i = startLineIdx; i < lines.Count; i++)
        {
            var line = lines[i];
            char? quote = null;
            for (var c = 0; c < line.Length; c++)
            {
                var ch = line[c];
                if (quote.HasValue)
                {
                    if (ch == '\\')
                    {
                        c++;
                        continue;
                    }
                    if (ch == quote.Value)
                    {
                        quote = null;
                    }
                    continue;
                }
                if (ch == '#')
                {
                    break;
                }
                if (ch is '"' or '\'')
                {
                    quote = ch;
                }
                else if (ch is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (ch is ')' or ']' or '}')
                {
                    depth = Math.Max(0, depth - 1);
                }
            }

            if (depth <= 0 && !line.TrimEnd().EndsWith('\\'))
            {
                return i;
            }
        }

        return startLineIdx;
    }

    private static bool IsFilterAssignmentLine(string line)
    {
        var match = AssignmentRegex.Match(line);
        if (!match.Success || match.Groups["indent"].Value.Length != 0)
        {
            return false;
        }

        var varName = match.Groups["var"].Value;
        if (NonClipVariableNames.Contains(varName) || varName.StartsWith("_fe_", StringComparison.Ordinal))
        {
            return false;
        }

        var rhs = match.Groups["rhs"].Value.Trim();
        return rhs.Contains('(', StringComparison.Ordinal);
    }

    private static bool IsTopLevelImportStart(string line)
    {
        if (string.IsNullOrEmpty(line) || char.IsWhiteSpace(line[0]))
        {
            return false;
        }

        return line.StartsWith("import ", StringComparison.Ordinal)
            || line.StartsWith("from ", StringComparison.Ordinal);
    }

    private static int FindAfterLastImportIndex(List<string> lines)
    {
        var lastImportLine = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsTopLevelImportStart(lines[i]))
            {
                var endIdx = FindStatementEndLineIndex(lines, i);
                lastImportLine = endIdx;
                i = endIdx;
            }
        }

        return lastImportLine + 1;
    }

    private static bool EnsureImportModule(List<string> lines, string moduleName, string defaultAlias, out string resolvedAlias)
    {
        var lastImportLine = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsTopLevelImportStart(lines[i]))
            {
                var endIdx = FindStatementEndLineIndex(lines, i);
                lastImportLine = endIdx;
                var match = ImportModuleAliasRegex.Match(lines[i]);
                if (match.Success && string.Equals(match.Groups["mod"].Value, moduleName, StringComparison.Ordinal))
                {
                    resolvedAlias = match.Groups["alias"].Success ? match.Groups["alias"].Value : moduleName;
                    return false;
                }

                i = endIdx;
            }
        }

        resolvedAlias = defaultAlias;
        var importLine = string.Equals(moduleName, defaultAlias, StringComparison.Ordinal)
            ? $"import {moduleName}"
            : $"import {moduleName} as {defaultAlias}";
        lines.Insert(lastImportLine + 1, importLine);
        return true;
    }

    private static bool EnsureFromImportSymbol(
        List<string> lines,
        string moduleName,
        string symbolName,
        out string resolvedSymbol)
    {
        resolvedSymbol = symbolName;
        var lastImportLine = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IsTopLevelImportStart(lines[i]))
            {
                continue;
            }

            var endIdx = FindStatementEndLineIndex(lines, i);
            lastImportLine = endIdx;

            var combined = endIdx > i
                ? string.Join(" ", lines.Skip(i).Take(endIdx - i + 1).Select(static l => StripInlineComment(l).Trim()))
                    .Replace("(", " ", StringComparison.Ordinal)
                    .Replace(")", " ", StringComparison.Ordinal)
                : lines[i];

            var fromMatch = FromImportRegex.Match(combined);
            if (fromMatch.Success && string.Equals(fromMatch.Groups["mod"].Value, moduleName, StringComparison.Ordinal))
            {
                var namesPart = fromMatch.Groups["names"].Value;
                foreach (var entry in namesPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var aliasMatch = Regex.Match(entry, @"^(?<orig>[A-Za-z_][A-Za-z0-9_]*)(?:\s+as\s+(?<alias>[A-Za-z_][A-Za-z0-9_]*))?$");
                    if (aliasMatch.Success && string.Equals(aliasMatch.Groups["orig"].Value, symbolName, StringComparison.Ordinal))
                    {
                        resolvedSymbol = aliasMatch.Groups["alias"].Success
                            ? aliasMatch.Groups["alias"].Value
                            : symbolName;
                        return false;
                    }
                }
            }

            i = endIdx;
        }

        if (lines.Any(line => Regex.IsMatch(StripInlineComment(line), $@"\b{Regex.Escape(symbolName)}\b")))
        {
            resolvedSymbol = "jet_" + symbolName;
            for (var suffix = 2; IsVariableDefinedBefore(lines, resolvedSymbol, lines.Count); suffix++)
                resolvedSymbol = $"jet_{symbolName}{suffix}";
        }
        var aliasSuffix = resolvedSymbol == symbolName ? string.Empty : $" as {resolvedSymbol}";
        lines.Insert(lastImportLine + 1, $"from {moduleName} import {symbolName}{aliasSuffix}");
        return true;
    }

    private static string StripInlineComment(string line)
    {
        var hashIdx = line.IndexOf('#');
        return hashIdx >= 0 ? line[..hashIdx] : line;
    }

    private static int InsertBeforeOutput(List<string> lines, int outputLineIndex, IReadOnlyList<string> statements)
    {
        var clampedIndex = Math.Clamp(outputLineIndex, 0, lines.Count);
        var indent = string.Empty;
        if (clampedIndex < lines.Count)
        {
            var match = SetOutputMethodRegex.Match(lines[clampedIndex]);
            if (match.Success)
            {
                indent = match.Groups["indent"].Value;
            }
        }

        for (var i = 0; i < statements.Count; i++)
        {
            var stmt = string.IsNullOrEmpty(indent) || string.IsNullOrWhiteSpace(statements[i])
                ? statements[i]
                : indent + statements[i];
            lines.Insert(clampedIndex + i, stmt);
        }

        return clampedIndex;
    }

    private static int ParseSetOutputIndex(string args)
    {
        var trimmed = (args ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return 0;
        }

        var firstArg = trimmed.Split(',')[0].Trim();
        if (firstArg.StartsWith("index=", StringComparison.Ordinal))
        {
            firstArg = firstArg["index=".Length..].Trim();
        }

        return int.TryParse(firstArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            ? index
            : 0;
    }

    private static List<string> SplitLines(string text, out string newline)
    {
        newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];
    }
}
