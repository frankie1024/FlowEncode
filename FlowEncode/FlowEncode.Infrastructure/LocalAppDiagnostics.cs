using FlowEncode.Application;

namespace FlowEncode.Infrastructure;

public sealed class LocalAppDiagnostics : IAppDiagnostics
{
    private readonly LocalAppPaths _paths;

    public LocalAppDiagnostics(LocalAppPaths paths)
    {
        _paths = paths;
    }

    public void Write(
        string source,
        string message,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Information,
        IReadOnlyDictionary<string, string?>? context = null)
    {
        AppDiagnosticsLog.Write(_paths, source, message, severity, context);
    }

    public void WriteException(
        string source,
        string operationName,
        Exception exception,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Error,
        IReadOnlyDictionary<string, string?>? context = null)
    {
        AppDiagnosticsLog.Write(_paths, source, operationName, severity, context, exception);
    }
}
