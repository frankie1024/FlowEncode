using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FlowEncode.Infrastructure;

internal readonly record struct PublishedOutputFile(long Length, DateTime LastWriteTimeUtc, PublishedOutputFile.FileId? Identity)
{
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileRenameInfo = 3;
    private const int FileIdInfo = 18;

    public static PublishedOutputFile Capture(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Read(handle);
    }

    public void MoveBack(string target, string stagedPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Verify(Capture(target));
            File.Move(target, stagedPath);
            return;
        }

        // Refuse concurrent writers and use this same handle to rename the verified file.
        // A second path lookup could otherwise pick up a different file after the check.
        using var handle = CreateFile(GetWindowsPath(target), GenericRead | DeleteAccess, FileShare.Read,
            IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Verify(Read(handle));
        Rename(handle, GetWindowsPath(stagedPath));
    }

    private static string GetWindowsPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            return fullPath;
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    private static PublishedOutputFile Read(SafeFileHandle handle)
    {
        FileId? identity = null;
        if (OperatingSystem.IsWindows())
        {
            if (GetFileInformationByHandleEx(handle, FileIdInfo, out var fileId, Marshal.SizeOf<FileId>()))
            {
                identity = fileId;
            }
            else if (GetFileInformationByHandle(handle, out var legacyInfo))
            {
                var index = ((ulong)legacyInfo.FileIndexHigh << 32) | legacyInfo.FileIndexLow;
                identity = new FileId(legacyInfo.VolumeSerialNumber, index, 0);
            }
        }
        return new(RandomAccess.GetLength(handle), File.GetLastWriteTimeUtc(handle), identity);
    }

    private void Verify(PublishedOutputFile current)
    {
        if (OperatingSystem.IsWindows() && Identity is null)
            throw new IOException("The published file's identity could not be verified.");
        if (current != this)
            throw new IOException("The published file changed after publication.");
    }

    private static void Rename(SafeFileHandle handle, string path)
    {
        var nameOffset = Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.FirstCharacter)).ToInt32();
        var name = Encoding.Unicode.GetBytes(path + '\0');
        var bufferSize = Math.Max(Marshal.SizeOf<RenameInfo>(), nameOffset + name.Length);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            // FILE_RENAME_INFO uses a variable-length UTF-16 buffer. A zero Flags field
            // makes the rename fail if another file now occupies the staging path.
            var info = new RenameInfo { NameLength = name.Length - sizeof(char) };
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            if (!SetFileInformationByHandle(handle, FileRenameInfo, buffer, bufferSize))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct FileId(ulong VolumeSerialNumber, ulong IdentifierLow, ulong IdentifierHigh);

    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInfo
    {
        public uint Flags;
        public IntPtr RootDirectory;
        public int NameLength;
        public ushort FirstCharacter;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileId information, int bufferSize);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr information, int bufferSize);
}
