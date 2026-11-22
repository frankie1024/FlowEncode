using System.Runtime.InteropServices;
using System.Text;

namespace FlowEncode.Infrastructure;

internal static class WindowsConsoleTextDecoder
{
    private const uint CpAcp = 0;

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        return Decode(bytes, CpAcp);
    }

    internal static string Decode(ReadOnlySpan<byte> bytes, uint codePage)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (!OperatingSystem.IsWindows())
        {
            return Encoding.UTF8.GetString(bytes);
        }

        var charCount = MultiByteToWideChar(
            codePage,
            0,
            ref MemoryMarshal.GetReference(bytes),
            bytes.Length,
            null,
            0);
        if (charCount <= 0)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        var chars = new char[charCount];
        var written = MultiByteToWideChar(
            codePage,
            0,
            ref MemoryMarshal.GetReference(bytes),
            bytes.Length,
            chars,
            charCount);
        return written > 0
            ? new string(chars, 0, written)
            : Encoding.UTF8.GetString(bytes);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MultiByteToWideChar(
        uint codePage,
        uint dwFlags,
        ref byte lpMultiByteStr,
        int cbMultiByte,
        [Out] char[]? lpWideCharStr,
        int cchWideChar);
}
