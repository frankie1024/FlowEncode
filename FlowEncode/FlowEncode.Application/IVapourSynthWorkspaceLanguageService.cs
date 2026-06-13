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

}

public sealed record VapourSynthLanguageFeaturesSnapshot(
    bool IsRuntimeReady,
    string RuntimeSummary,
    IReadOnlyList<VapourSynthCoreMemberDescriptor> CoreMembers,
    IReadOnlyList<VapourSynthPluginNamespaceDescriptor> Namespaces);

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
    string? RelatedText);

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

