namespace FlowEncode.Application;

public static class AppDiagnosticsExtensions
{
    public static bool TryRun(
        this IAppDiagnostics diagnostics,
        string source,
        string operationName,
        Action action,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Error,
        IReadOnlyDictionary<string, string?>? context = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            diagnostics.WriteException(source, operationName, ex, severity, context);
            return false;
        }
    }

    public static async Task<bool> TryRunAsync(
        this IAppDiagnostics diagnostics,
        string source,
        string operationName,
        Func<Task> action,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Error,
        IReadOnlyDictionary<string, string?>? context = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            diagnostics.WriteException(source, operationName, ex, severity, context);
            return false;
        }
    }

    public static Task RunLoggedAsync(
        this IAppDiagnostics diagnostics,
        string source,
        string operationName,
        Func<Task> action,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Error,
        IReadOnlyDictionary<string, string?>? context = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(action);

        return RunLoggedCoreAsync(diagnostics, source, operationName, action, severity, context);
    }

    public static void RunFireAndForget(
        this IAppDiagnostics diagnostics,
        string source,
        string operationName,
        Func<Task> action,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Error,
        IReadOnlyDictionary<string, string?>? context = null)
    {
        _ = diagnostics.RunLoggedAsync(source, operationName, action, severity, context);
    }

    private static async Task RunLoggedCoreAsync(
        IAppDiagnostics diagnostics,
        string source,
        string operationName,
        Func<Task> action,
        AppDiagnosticSeverity severity,
        IReadOnlyDictionary<string, string?>? context)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            diagnostics.WriteException(source, operationName, ex, severity, context);
        }
    }
}
