using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FlowEncode.Application;

public interface IVapourSynthWorkspaceLanguageService
{
    Task<VapourSynthLanguageFeaturesSnapshot> GetLanguageFeaturesAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    Task<VapourSynthScriptDiagnosticResult> DiagnoseScriptAsync(
        string? filePath,
        string content,
        CancellationToken cancellationToken = default);

    Task WarmupPythonLanguageServerAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VapourSynthPythonCompletionItem>> GetPythonCompletionsAsync(
        VapourSynthTextDocumentContext document,
        VapourSynthTextDocumentPosition position,
        string? triggerCharacter = null,
        CancellationToken cancellationToken = default);

    Task<VapourSynthPythonHoverInfo?> GetPythonHoverAsync(
        VapourSynthTextDocumentContext document,
        VapourSynthTextDocumentPosition position,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<VapourSynthPythonHoverInfo?>(null);

    Task<VapourSynthPythonSignatureHelp?> GetPythonSignatureHelpAsync(
        VapourSynthTextDocumentContext document,
        VapourSynthTextDocumentPosition position,
        string? triggerCharacter = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<VapourSynthPythonSignatureHelp?>(null);

    Task<VapourSynthScriptBenchmarkResult> BenchmarkScriptAsync(
        string? filePath,
        string content,
        string workingDirectory,
        int sampleFrames = 100,
        int outputIndex = 0,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new VapourSynthScriptBenchmarkResult(false, outputIndex, 0, 0, 0, 0, 0, 0, 0, string.Empty, 0, "Benchmarking is not supported."));

}

public sealed record VapourSynthLanguageFeaturesSnapshot(
    bool IsRuntimeReady,
    string RuntimeSummary,
    IReadOnlyList<VapourSynthCoreMemberDescriptor> CoreMembers,
    IReadOnlyList<VapourSynthPluginNamespaceDescriptor> Namespaces,
    IReadOnlyList<VapourSynthCoreMemberDescriptor>? VsConstants = null,
    IReadOnlyList<VapourSynthCoreMemberDescriptor>? VideoNodeMembers = null,
    IReadOnlyList<string>? InstalledPackages = null);

public sealed record VapourSynthCoreMemberDescriptor(
    string Name,
    string Kind,
    string Detail,
    string Documentation);

public sealed record VapourSynthPluginNamespaceDescriptor(
    string Name,
    string Identifier,
    string DisplayName,
    IReadOnlyList<VapourSynthPluginFunctionDescriptor> Functions);

public sealed record VapourSynthPluginFunctionDescriptor(
    string Name,
    string QualifiedName,
    string SignatureLabel,
    string ReturnType,
    IReadOnlyList<VapourSynthFunctionParameterDescriptor> Parameters,
    string Documentation);

public sealed record VapourSynthFunctionParameterDescriptor(
    string Name,
    string Label,
    string Documentation);

public sealed record VapourSynthScriptDiagnosticResult(
    bool IsRuntimeReady,
    string RuntimeSummary,
    IReadOnlyList<VapourSynthScriptDiagnostic> Diagnostics);

public sealed record VapourSynthScriptDiagnostic(
    string Code,
    string Severity,
    string Message,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string Source,
    string? RelatedText,
    string? QuickFixTitle = null,
    string? SuggestedReplacement = null,
    int? QuickFixInsertLine = null,
    string? QuickFixInsertText = null);

public sealed record VapourSynthTextDocumentContext(
    string? FilePath,
    string Content);

public sealed record VapourSynthTextDocumentPosition(
    int Line,
    int Column);

public sealed record VapourSynthTextRange(
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

public sealed record VapourSynthPythonCompletionItem(
    string Label,
    string Kind,
    string Detail,
    string Documentation,
    string InsertText,
    string SortText,
    string FilterText,
    bool IsSnippet);

public sealed record VapourSynthPythonHoverInfo(
    string Contents);

public sealed record VapourSynthPythonSignatureParameter(
    string Label,
    string Documentation);

public sealed record VapourSynthPythonSignatureItem(
    string Label,
    string Documentation,
    IReadOnlyList<VapourSynthPythonSignatureParameter> Parameters);

public sealed record VapourSynthPythonSignatureHelp(
    IReadOnlyList<VapourSynthPythonSignatureItem> Signatures,
    int ActiveSignature,
    int ActiveParameter);

public sealed record VapourSynthScriptBenchmarkResult(
    bool Succeeded,
    int OutputIndex,
    int FramesRendered,
    int StartFrame,
    int TotalFrames,
    double ElapsedSeconds,
    double Fps,
    int Width,
    int Height,
    string FormatName,
    int BitsPerSample,
    string? ErrorMessage);

