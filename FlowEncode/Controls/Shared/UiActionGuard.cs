using System;
using System.Diagnostics;
using System.Threading.Tasks;
using FlowEncode.Application;
using Microsoft.UI.Xaml;

namespace FlowEncode.Controls.Shared;

internal static class UiActionGuard
{
    public static Task RunAsync(
        FrameworkElement owner,
        string source,
        string actionName,
        string errorTitle,
        string closeButtonText,
        Func<Task> action)
    {
        return RunAsync(owner.XamlRoot, owner.ActualTheme, source, actionName, errorTitle, closeButtonText, action);
    }

    public static async Task RunAsync(
        XamlRoot? xamlRoot,
        ElementTheme actualTheme,
        string source,
        string actionName,
        string errorTitle,
        string closeButtonText,
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException ex)
        {
            TryWriteException(source, actionName, ex, AppDiagnosticSeverity.Warning);
        }
        catch (Exception ex)
        {
            TryWriteException(source, actionName, ex, AppDiagnosticSeverity.Error);
            await WindowInteractionHelper.ShowMessageAsync(
                xamlRoot,
                actualTheme,
                closeButtonText,
                errorTitle,
                ex.Message);
        }
    }

    private static void TryWriteException(
        string source,
        string actionName,
        Exception exception,
        AppDiagnosticSeverity severity)
    {
        try
        {
            App.GetService<IAppDiagnostics>().WriteException(source, actionName, exception, severity);
        }
        catch (Exception diagnosticException)
        {
            Debug.WriteLine($"Failed to write UI action diagnostic. {diagnosticException}");
        }
    }
}
