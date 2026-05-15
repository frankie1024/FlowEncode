using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FlowEncode.Infrastructure;

internal static class ErrorDialogSuppression
{
    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;
    private const uint SemNoOpenFileErrorBox = 0x8000;
    private const uint SuppressedErrorMode = SemFailCriticalErrors | SemNoGpFaultErrorBox | SemNoOpenFileErrorBox;

    public static IDisposable Enter()
    {
        if (!OperatingSystem.IsWindows())
        {
            return NoopScope.Instance;
        }

        return new Scope();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadErrorMode(uint dwNewMode, out uint lpOldMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetErrorMode(uint uMode);

    private sealed class Scope : IDisposable
    {
        private readonly bool _threadModeApplied;
        private readonly uint _oldMode;
        private bool _disposed;

        public Scope()
        {
            try
            {
                if (SetThreadErrorMode(SuppressedErrorMode, out var oldThreadMode))
                {
                    _threadModeApplied = true;
                    _oldMode = oldThreadMode;
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set thread error mode. {ex}");
            }

            try
            {
                _oldMode = SetErrorMode(SuppressedErrorMode);
            }
            catch
            {
                _oldMode = 0;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                if (_threadModeApplied)
                {
                    _ = SetThreadErrorMode(_oldMode, out _);
                }
                else
                {
                    _ = SetErrorMode(_oldMode);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to restore process error mode. {ex}");
            }
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

