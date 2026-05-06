using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace FlowEncode;

internal static class NativeFileDialogHelper
{
    private const uint FOS_OVERWRITEPROMPT = 0x00000002;
    private const uint FOS_STRICTFILETYPES = 0x00000004;
    private const uint FOS_NOCHANGEDIR = 0x00000008;
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_FILEMUSTEXIST = 0x00001000;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;

    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid ClsidFileSaveDialog = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
    private static readonly Guid IidIFileOpenDialog = new("D57C7288-D4AD-4768-BE02-9D969532D960");
    private static readonly Guid IidIFileSaveDialog = new("84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB");
    private static readonly Guid IidIShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public static FileDialogResult? ShowOpenFileDialog(
        nint ownerWindowHandle,
        string title,
        string initialDirectory,
        params FileDialogFilter[] filters)
    {
        return ShowFileDialog(
            ownerWindowHandle,
            title,
            initialDirectory,
            defaultFileName: null,
            defaultExtension: null,
            saveDialog: false,
            filters);
    }

    public static FileDialogResult? ShowSaveFileDialog(
        nint ownerWindowHandle,
        string title,
        string initialDirectory,
        string defaultFileName,
        string defaultExtension,
        params FileDialogFilter[] filters)
    {
        return ShowFileDialog(
            ownerWindowHandle,
            title,
            initialDirectory,
            defaultFileName,
            defaultExtension,
            saveDialog: true,
            filters);
    }

    public static string? ShowFolderDialog(
        nint ownerWindowHandle,
        string title,
        string initialDirectory)
    {
        var dialog = CreateDialog<IFileOpenDialog>(ClsidFileOpenDialog, IidIFileOpenDialog);
        try
        {
            ConfigureDialogCommon(dialog, ownerWindowHandle, title, initialDirectory);
            ConfigureDialogOptions(dialog, FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_PICKFOLDERS | FOS_NOCHANGEDIR);

            var hr = dialog.Show(ownerWindowHandle);
            if (IsCanceled(hr))
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(hr);

            dialog.GetResult(out var selectedItem);
            try
            {
                return GetShellItemPath(selectedItem);
            }
            finally
            {
                ReleaseComObject(selectedItem);
            }
        }
        finally
        {
            ReleaseComObject(dialog);
        }
    }

    private static FileDialogResult? ShowFileDialog(
        nint ownerWindowHandle,
        string title,
        string initialDirectory,
        string? defaultFileName,
        string? defaultExtension,
        bool saveDialog,
        params FileDialogFilter[] filters)
    {
        var dialog = saveDialog
            ? (IFileDialog)CreateDialog<IFileSaveDialog>(ClsidFileSaveDialog, IidIFileSaveDialog)
            : CreateDialog<IFileOpenDialog>(ClsidFileOpenDialog, IidIFileOpenDialog);

        try
        {
            ConfigureDialogCommon(dialog, ownerWindowHandle, title, initialDirectory);
            ConfigureDialogFilters(dialog, filters);
            ConfigureDialogOptions(
                dialog,
                saveDialog
                    ? FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_OVERWRITEPROMPT | FOS_STRICTFILETYPES | FOS_NOCHANGEDIR
                    : FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_FILEMUSTEXIST | FOS_NOCHANGEDIR);

            if (saveDialog && !string.IsNullOrWhiteSpace(defaultFileName))
            {
                dialog.SetFileName(defaultFileName);
            }

            var normalizedExtension = NormalizeDefaultExtension(defaultExtension);
            if (saveDialog && !string.IsNullOrWhiteSpace(normalizedExtension))
            {
                dialog.SetDefaultExtension(normalizedExtension);
            }

            var hr = dialog.Show(ownerWindowHandle);
            if (IsCanceled(hr))
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(hr);

            dialog.GetResult(out var selectedItem);
            try
            {
                var selectedPath = GetShellItemPath(selectedItem);
                if (string.IsNullOrWhiteSpace(selectedPath))
                {
                    return null;
                }

                dialog.GetFileTypeIndex(out var selectedFilterIndex);
                return new FileDialogResult(selectedPath, unchecked((int)selectedFilterIndex));
            }
            finally
            {
                ReleaseComObject(selectedItem);
            }
        }
        finally
        {
            ReleaseComObject(dialog);
        }
    }

    private static void ConfigureDialogCommon(
        IFileDialog dialog,
        nint ownerWindowHandle,
        string title,
        string initialDirectory)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            dialog.SetTitle(title);
        }

        var normalizedDirectory = NormalizeExistingDirectory(initialDirectory);
        if (string.IsNullOrWhiteSpace(normalizedDirectory))
        {
            return;
        }

        var shellItem = CreateShellItem(normalizedDirectory);
        if (shellItem is null)
        {
            return;
        }

        try
        {
            dialog.SetDefaultFolder(shellItem);
            dialog.SetFolder(shellItem);
        }
        finally
        {
            ReleaseComObject(shellItem);
        }
    }

    private static void ConfigureDialogOptions(IFileDialog dialog, uint optionsToAdd)
    {
        dialog.GetOptions(out var existingOptions);
        dialog.SetOptions(existingOptions | optionsToAdd);
    }

    private static void ConfigureDialogFilters(IFileDialog dialog, IReadOnlyList<FileDialogFilter> filters)
    {
        if (filters.Count == 0)
        {
            return;
        }

        var normalizedFilters = filters
            .Where(filter => !string.IsNullOrWhiteSpace(filter.Label) && !string.IsNullOrWhiteSpace(filter.Pattern))
            .ToArray();
        if (normalizedFilters.Length == 0)
        {
            return;
        }

        var specs = normalizedFilters
            .Select(filter => new COMDLG_FILTERSPEC
            {
                pszName = filter.Label,
                pszSpec = filter.Pattern
            })
            .ToArray();
        dialog.SetFileTypes((uint)specs.Length, specs);
        dialog.SetFileTypeIndex(1);
    }

    private static string? GetShellItemPath(IShellItem shellItem)
    {
        shellItem.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var pathPointer);
        try
        {
            var selectedPath = Marshal.PtrToStringUni(pathPointer)?.Trim();
            return string.IsNullOrWhiteSpace(selectedPath) ? null : selectedPath;
        }
        finally
        {
            if (pathPointer != nint.Zero)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
    }

    private static IShellItem? CreateShellItem(string path)
    {
        var normalizedPath = NormalizeExistingDirectory(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        var hr = SHCreateItemFromParsingName(
            normalizedPath,
            nint.Zero,
            in IidIShellItem,
            out var shellItemObject);
        if (hr < 0 || shellItemObject is null)
        {
            return null;
        }

        return (IShellItem)shellItemObject;
    }

    private static T CreateDialog<T>(Guid clsid, Guid iid) where T : class
    {
        var hr = CoCreateInstance(
            in clsid,
            null,
            CLSCTX.CLSCTX_INPROC_SERVER,
            in iid,
            out var instance);
        Marshal.ThrowExceptionForHR(hr);
        return (T)instance;
    }

    private static bool IsCanceled(int hresult)
    {
        return unchecked((uint)hresult) == 0x800704C7;
    }

    private static string? NormalizeDefaultExtension(string? defaultExtension)
    {
        if (string.IsNullOrWhiteSpace(defaultExtension))
        {
            return null;
        }

        return defaultExtension.Trim().TrimStart('.');
    }

    private static string? NormalizeExistingDirectory(string? initialDirectory)
    {
        if (string.IsNullOrWhiteSpace(initialDirectory))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(initialDirectory);
            return Directory.Exists(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    internal readonly record struct FileDialogFilter(string Label, string Pattern);

    internal readonly record struct FileDialogResult(string Path, int SelectedFilterIndex);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        in Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object? pUnkOuter,
        CLSCTX dwClsContext,
        in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        nint pbc,
        in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pszName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pszSpec;
    }

    private enum SIGDN : uint
    {
        SIGDN_FILESYSPATH = 0x80058000
    }

    [Flags]
    private enum CLSCTX : uint
    {
        CLSCTX_INPROC_SERVER = 0x1
    }

    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig]
        int Show(nint parent);

        void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);

        void SetFileTypeIndex(uint iFileType);

        void GetFileTypeIndex(out uint piFileType);

        void Advise(nint pfde, out uint pdwCookie);

        void Unadvise(uint dwCookie);

        void SetOptions(uint fos);

        void GetOptions(out uint pfos);

        void SetDefaultFolder(IShellItem psi);

        void SetFolder(IShellItem psi);

        void GetFolder(out IShellItem ppsi);

        void GetCurrentSelection(out IShellItem ppsi);

        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);

        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);

        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);

        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

        void GetResult(out IShellItem ppsi);

        void AddPlace(IShellItem psi, uint fdap);

        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);

        void Close(int hr);

        void SetClientGuid(in Guid guid);

        void ClearClientData();

        void SetFilter([MarshalAs(UnmanagedType.IUnknown)] object pFilter);
    }

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog : IFileDialog
    {
    }

    [ComImport]
    [Guid("84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileSaveDialog : IFileDialog
    {
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);

        void GetParent(out IShellItem ppsi);

        void GetDisplayName(SIGDN sigdnName, out nint ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
