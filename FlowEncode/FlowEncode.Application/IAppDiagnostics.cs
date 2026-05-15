namespace FlowEncode.Application;

public enum AppDiagnosticSeverity
{
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

public interface IAppDiagnostics
{
    void Write(
        string source,
        string message,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Information,
        IReadOnlyDictionary<string, string?>? context = null);

    void WriteException(
        string source,
        string operationName,
        Exception exception,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Error,
        IReadOnlyDictionary<string, string?>? context = null);
}
