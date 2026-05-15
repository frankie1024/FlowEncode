using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FlowEncode.Infrastructure;

internal sealed class ProcessJobObject : IDisposable
{
    private readonly SafeJobHandle _handle;
    private int _disposed;

    private ProcessJobObject(SafeJobHandle handle)
    {
        _handle = handle;
    }

    public static ProcessJobObject? TryAttach(Process process, Action<string>? onFailure = null)
    {
        SafeJobHandle? handle = null;

        try
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
            {
                var win32Error = Marshal.GetLastWin32Error();
                handle.Dispose();
                ReportFailure(onFailure, process, "CreateJobObject", win32Error);
                return null;
            }

            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitFlags.KillOnJobClose
                }
            };

            if (!SetInformationJobObject(
                    handle,
                    JobObjectInfoClass.ExtendedLimitInformation,
                    ref limits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                var win32Error = Marshal.GetLastWin32Error();
                handle.Dispose();
                ReportFailure(onFailure, process, "SetInformationJobObject", win32Error);
                return null;
            }

            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                var win32Error = Marshal.GetLastWin32Error();
                handle.Dispose();
                ReportFailure(onFailure, process, "AssignProcessToJobObject", win32Error);
                return null;
            }

            return new ProcessJobObject(handle);
        }
        catch (Exception ex)
        {
            handle?.Dispose();
            ReportFailure(onFailure, process, "exception", null, ex);
            return null;
        }
    }

    private static void ReportFailure(
        Action<string>? onFailure,
        Process process,
        string stage,
        int? win32Error = null,
        Exception? exception = null)
    {
        if (onFailure is null)
        {
            return;
        }

        try
        {
            var processId = TryGetProcessId(process);
            var executablePath = string.IsNullOrWhiteSpace(process.StartInfo.FileName)
                ? "unknown"
                : process.StartInfo.FileName;
            var builder = new System.Text.StringBuilder()
                .Append("Process job attach failed at ")
                .Append(stage)
                .Append(" for PID ")
                .Append(processId)
                .Append(" (")
                .Append(executablePath)
                .Append(')');

            if (win32Error.HasValue)
            {
                builder.Append(". Win32Error=").Append(win32Error.Value);
            }

            if (exception is not null)
            {
                builder
                    .Append(". ")
                    .Append(exception.GetType().Name)
                    .Append(": ")
                    .Append(exception.Message);
            }

            onFailure(builder.ToString());
        }
        catch (Exception diagnosticException)
        {
            Debug.WriteLine($"Failed to report process job attach failure. {diagnosticException}");
        }
    }

    private static int TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return -1;
        }
    }

    public void Terminate()
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0)
        {
            return;
        }

        try
        {
            if (!_handle.IsInvalid)
            {
                TerminateJobObject(_handle, 1);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to terminate process job object. {ex}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _handle.Dispose();
    }

    [Flags]
    private enum JobObjectLimitFlags : uint
    {
        KillOnJobClose = 0x00002000
    }

    private enum JobObjectInfoClass
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public JobObjectLimitFlags LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle hJob,
        JobObjectInfoClass jobObjectInfoClass,
        ref JobObjectExtendedLimitInformation lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
