using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public partial class MainWindowViewModel
{
    private bool _isWritingSystemPath;
    private string _systemPathStatusText = string.Empty;

    internal bool IsWritingSystemPath => _isWritingSystemPath;

    internal Visibility SystemPathProgressVisibility => _isWritingSystemPath ? Visibility.Visible : Visibility.Collapsed;

    internal string SystemPathStatusText
    {
        get => _systemPathStatusText;
        private set => SetProperty(ref _systemPathStatusText, value);
    }

    internal async Task WriteSystemPathAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _isWritingSystemPath = true;
        RaiseSystemPathActionPropertyChanges();
        SystemPathStatusText = Texts.SystemPathWritingStatus;
        try
        {
            _toolProbeService.InvalidateCache();
            _encoderDiscoveryService.InvalidateCache();
            var readiness = await _environmentReadinessService.CheckAsync();
            var result = await Task.Run(() => _cliEnvironmentIntegrationService.RegisterSystemPath(readiness));
            SystemPathStatusText = Texts.SystemPathWrittenStatus(result.Directories.Count, result.Changed)
                + Environment.NewLine + string.Join(Environment.NewLine, result.Directories);
            StatusText = Texts.SystemPathWrittenStatus(result.Directories.Count, result.Changed);
        }
        catch (OperationCanceledException)
        {
            SystemPathStatusText = Texts.SystemPathCancelledStatus;
        }
        catch (Exception ex)
        {
            SystemPathStatusText = Texts.SystemPathFailedStatus(ex.Message);
            throw;
        }
        finally
        {
            _isWritingSystemPath = false;
            RaiseSystemPathActionPropertyChanges();
        }
    }

    private void RaiseSystemPathActionPropertyChanges()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsWritingSystemPath));
        OnPropertyChanged(nameof(SystemPathProgressVisibility));
    }
}
