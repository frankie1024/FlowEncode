using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        var target = ResolveOutputTarget(script ?? string.Empty, outputIndex);
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
                lines[i] = m.Groups["indent"].Value + statement;
                return new SmartScriptEditResult(
                    string.Join(newline, lines),
                    i + 1,
                    varName,
                    UpdatedInPlace: true,
                    AddedImport: false);
            }
        }

        var insertAt = InsertBeforeOutput(lines, target.SetOutputLineIndex, [statement]);
        return new SmartScriptEditResult(
            string.Join(newline, lines),
            insertAt + 1,
            varName,
            UpdatedInPlace: false,
            AddedImport: false);
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
        var addedImport = EnsureImportModule(lines, "awsmfunc", "awf", out var alias);
        var target = ResolveOutputTarget(string.Join(newline, lines), outputIndex);
        var varName = target.VariableName;

        var statements = new List<string>();
        if (left > 0 || top > 0 || right > 0 || bottom > 0)
        {
            statements.Add($"{varName} = core.std.CropRel({varName}, left={left}, right={right}, top={top}, bottom={bottom})");
        }

        statements.Add($"{varName} = {alias}.bbmod({varName}, top=2, bottom=2, left=2, right=2, thresh=32, blur=20)");
        var insertAt = InsertBeforeOutput(lines, target.SetOutputLineIndex, statements);

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

        var insertAt = InsertBeforeOutput(lines, target.SetOutputLineIndex, statements);
        return new SmartScriptEditResult(
            string.Join(newline, lines),
            insertAt + 1,
            varName,
            UpdatedInPlace: false,
            AddedImport: false);
    }

    public static SmartScriptEditResult ApplyReplaceRanges(
        string script,
        int outputIndex,
        IReadOnlyList<PreviewSceneRange> ranges,
        string filteredVariable = "flt")
    {
        var lines = SplitLines(script ?? string.Empty, out var newline);
        var addedImport = EnsureFromImportSymbol(lines, "vstools", "replace_ranges");
        var target = ResolveOutputTarget(string.Join(newline, lines), outputIndex);
        var varName = target.VariableName;

        var statement = PreviewSceningFormatter.FormatReplaceRanges(ranges, varName, filteredVariable);
        var insertAt = InsertBeforeOutput(lines, target.SetOutputLineIndex, [statement]);
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
        var lines = SplitLines(script ?? string.Empty, out var newline);
        var target = ResolveOutputTarget(script ?? string.Empty, outputIndex);
        var oldVar = target.VariableName;
        var expr = expressionTemplateWithPlaceholder.Replace("{var}", oldVar, StringComparison.Ordinal);
        var statement = $"{newVariableName} = {expr}";

        var insertAt = InsertBeforeOutput(lines, target.SetOutputLineIndex, [statement]);
        if (target.FoundSetOutput)
        {
            var newOutputLineIndex = target.SetOutputLineIndex + 1;
            if (newOutputLineIndex < lines.Count)
            {
                var line = lines[newOutputLineIndex];
                var match = SetOutputMethodRegex.Match(line);
                if (match.Success && string.Equals(match.Groups["var"].Value, oldVar, StringComparison.Ordinal))
                {
                    lines[newOutputLineIndex] = $"{match.Groups["indent"].Value}{newVariableName}.set_output({match.Groups["args"].Value})";
                }
            }
        }

        return new SmartScriptEditResult(
            string.Join(newline, lines),
            insertAt + 1,
            newVariableName,
            UpdatedInPlace: false,
            AddedImport: false);
    }

    public static SmartScriptEditResult ApplyFilterTemplate(
        string script,
        int outputIndex,
        string expressionTemplateWithPlaceholder,
        string? importModule = null,
        string? importAlias = null,
        string? fromModule = null,
        string? fromSymbol = null,
        string? derivedVariableName = null)
    {
        var lines = SplitLines(script ?? string.Empty, out var newline);
        var addedImport = false;
        var resolvedAlias = importAlias ?? importModule ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(importModule))
        {
            addedImport = EnsureImportModule(
                lines,
                importModule,
                string.IsNullOrWhiteSpace(importAlias) ? importModule : importAlias,
                out resolvedAlias);
        }
        else if (!string.IsNullOrWhiteSpace(fromModule) && !string.IsNullOrWhiteSpace(fromSymbol))
        {
            addedImport = EnsureFromImportSymbol(lines, fromModule, fromSymbol);
        }

        var currentText = string.Join(newline, lines);
        var target = ResolveOutputTarget(currentText, outputIndex);
        var oldVar = target.VariableName;
        var targetVar = string.IsNullOrWhiteSpace(derivedVariableName) ? oldVar : derivedVariableName.Trim();

        var expr = expressionTemplateWithPlaceholder
            .Replace("{var}", oldVar, StringComparison.Ordinal)
            .Replace("{alias}", resolvedAlias, StringComparison.Ordinal);
        var statement = $"{targetVar} = {expr}";

        var insertAt = InsertBeforeOutput(lines, target.SetOutputLineIndex, [statement]);
        if (target.FoundSetOutput && !string.Equals(targetVar, oldVar, StringComparison.Ordinal))
        {
            var newOutputLineIndex = target.SetOutputLineIndex + 1;
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
            insertAt + 1,
            targetVar,
            UpdatedInPlace: false,
            AddedImport: addedImport);
    }

    public static SmartScriptEditResult ApplyVideoSourceLoader(string script, string videoFilePath)
    {
        if (string.IsNullOrWhiteSpace(videoFilePath))
        {
            return new SmartScriptEditResult(script ?? string.Empty, 1, "src", false, false);
        }

        var normalizedPath = videoFilePath.Trim();
        var rawLiteral = normalizedPath.Contains('"', StringComparison.Ordinal)
            ? $"\"{normalizedPath.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : $"r\"{normalizedPath}\"";

        var lines = SplitLines(script ?? string.Empty, out var newline);
        var hasContent = lines.Any(static l => !string.IsNullOrWhiteSpace(l));
        if (!hasContent)
        {
            var fresh = new List<string>
            {
                "import vapoursynth as vs",
                "core = vs.core",
                string.Empty,
                $"src = core.lsmas.LWLibavSource({rawLiteral})",
                "src.set_output()"
            };
            return new SmartScriptEditResult(
                string.Join(newline, fresh),
                4,
                "src",
                UpdatedInPlace: false,
                AddedImport: true);
        }

        var addedImport = EnsureImportModule(lines, "vapoursynth", "vs", out var vsAlias);
        var hasCore = lines.Any(static l => Regex.IsMatch(l, @"^[ \t]*core\s*="));
        if (!hasCore)
        {
            var insertCoreAt = FindAfterLastImportIndex(lines);
            lines.Insert(insertCoreAt, $"core = {vsAlias}.core");
            addedImport = true;
        }

        var assignedVars = new HashSet<string>(StringComparer.Ordinal);
        var hasSetOutput = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var assign = AssignmentRegex.Match(lines[i]);
            if (assign.Success && assign.Groups["indent"].Value.Length == 0)
            {
                assignedVars.Add(assign.Groups["var"].Value);
            }

            if (SetOutputMethodRegex.IsMatch(lines[i]))
            {
                hasSetOutput = true;
            }
        }

        var varName = "src";
        if (assignedVars.Contains(varName))
        {
            var suffix = 2;
            while (assignedVars.Contains($"src{suffix}"))
            {
                suffix++;
            }

            varName = $"src{suffix}";
        }

        var loaderLine = $"{varName} = core.lsmas.LWLibavSource({rawLiteral})";
        int targetLineIdx;
        if (!hasSetOutput)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            targetLineIdx = lines.Count;
            lines.Add(loaderLine);
            lines.Add($"{varName}.set_output()");
        }
        else
        {
            var insertAt = FindAfterLastImportIndex(lines);
            while (insertAt < lines.Count && Regex.IsMatch(lines[insertAt], @"^[ \t]*core\s*="))
            {
                insertAt++;
            }

            while (insertAt < lines.Count && string.IsNullOrWhiteSpace(lines[insertAt]))
            {
                insertAt++;
            }

            lines.Insert(insertAt, loaderLine);
            targetLineIdx = insertAt;
        }

        return new SmartScriptEditResult(
            string.Join(newline, lines),
            targetLineIdx + 1,
            varName,
            UpdatedInPlace: false,
            AddedImport: addedImport);
    }

    public static SmartScriptEditResult ApplyMultiLineFilterTemplate(
        string script,
        int outputIndex,
        IReadOnlyList<string> statementTemplatesWithPlaceholders,
        string? importModule = null,
        string? importAlias = null,
        string? fromModule = null,
        string? fromSymbol = null,
        string? derivedVariableName = null)
    {
        var lines = SplitLines(script ?? string.Empty, out var newline);
        var addedImport = false;
        var resolvedAlias = importAlias ?? importModule ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(importModule))
        {
            addedImport = EnsureImportModule(
                lines,
                importModule,
                string.IsNullOrWhiteSpace(importAlias) ? importModule : importAlias,
                out resolvedAlias);
        }
        else if (!string.IsNullOrWhiteSpace(fromModule) && !string.IsNullOrWhiteSpace(fromSymbol))
        {
            addedImport = EnsureFromImportSymbol(lines, fromModule, fromSymbol);
        }

        var currentText = string.Join(newline, lines);
        var target = ResolveOutputTarget(currentText, outputIndex);
        var oldVar = target.VariableName;
        var targetVar = string.IsNullOrWhiteSpace(derivedVariableName) ? oldVar : derivedVariableName.Trim();

        var statements = statementTemplatesWithPlaceholders
            .Select(template => template
                .Replace("{var}", oldVar, StringComparison.Ordinal)
                .Replace("{target}", targetVar, StringComparison.Ordinal)
                .Replace("{alias}", resolvedAlias, StringComparison.Ordinal))
            .ToList();

        var insertAt = InsertBeforeOutput(lines, target.SetOutputLineIndex, statements);
        if (target.FoundSetOutput && !string.Equals(targetVar, oldVar, StringComparison.Ordinal))
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
            insertAt + 1,
            targetVar,
            UpdatedInPlace: false,
            AddedImport: addedImport);
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

        string beforeVar;
        if (earlierVars.Contains(afterVar, StringComparer.Ordinal))
        {
            beforeVar = afterVar;
        }
        else
        {
            beforeVar = earlierVars
                .LastOrDefault(v => Regex.IsMatch(rhs, $@"\b{Regex.Escape(v)}\b"))
                ?? earlierVars.LastOrDefault()
                ?? afterVar;
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

    private static int FindAfterLastImportIndex(List<string> lines)
    {
        var lastImportLine = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("import ", StringComparison.Ordinal) || trimmed.StartsWith("from ", StringComparison.Ordinal))
            {
                lastImportLine = i;
            }
        }

        return lastImportLine + 1;
    }

    private static bool EnsureImportModule(List<string> lines, string moduleName, string defaultAlias, out string resolvedAlias)
    {
        var lastImportLine = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("import ", StringComparison.Ordinal) || trimmed.StartsWith("from ", StringComparison.Ordinal))
            {
                lastImportLine = i;
            }

            var match = ImportModuleAliasRegex.Match(lines[i]);
            if (match.Success && string.Equals(match.Groups["mod"].Value, moduleName, StringComparison.Ordinal))
            {
                resolvedAlias = match.Groups["alias"].Success ? match.Groups["alias"].Value : moduleName;
                return false;
            }
        }

        resolvedAlias = defaultAlias;
        var importLine = string.Equals(moduleName, defaultAlias, StringComparison.Ordinal)
            ? $"import {moduleName}"
            : $"import {moduleName} as {defaultAlias}";
        lines.Insert(lastImportLine + 1, importLine);
        return true;
    }

    private static bool EnsureFromImportSymbol(List<string> lines, string moduleName, string symbolName)
    {
        var lastImportLine = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("import ", StringComparison.Ordinal) || trimmed.StartsWith("from ", StringComparison.Ordinal))
            {
                lastImportLine = i;
            }

            var fromMatch = FromImportRegex.Match(lines[i]);
            if (fromMatch.Success && Regex.IsMatch(fromMatch.Groups["names"].Value, $@"\b{Regex.Escape(symbolName)}\b"))
            {
                return false;
            }
        }

        lines.Insert(lastImportLine + 1, $"from {moduleName} import {symbolName}");
        return true;
    }

    private static int InsertBeforeOutput(List<string> lines, int outputLineIndex, IReadOnlyList<string> statements)
    {
        var clampedIndex = Math.Clamp(outputLineIndex, 0, lines.Count);
        for (var i = 0; i < statements.Count; i++)
        {
            lines.Insert(clampedIndex + i, statements[i]);
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
