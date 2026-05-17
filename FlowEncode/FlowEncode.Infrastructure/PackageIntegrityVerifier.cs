using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FlowEncode.Infrastructure;

internal static class PackageIntegrityVerifier
{
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionIgnore = 0;
    private const uint WtdSaferFlag = 0x00000100;
    private const uint WtdUiContextExecute = 0;

    public static async Task VerifySha256Async(
        string filePath,
        string expectedHash,
        CancellationToken cancellationToken,
        string packageLabel)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"{packageLabel} 下载文件不存在。", filePath);
        }

        var normalizedExpectedHash = NormalizeSha256Digest(expectedHash)
            ?? throw new InvalidOperationException($"{packageLabel} 缺少可用的 SHA256 摘要，已停止安装。");
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var actualHash = Convert.ToHexString(hash).ToLowerInvariant();

        if (!string.Equals(actualHash, normalizedExpectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{packageLabel} 的 SHA256 校验失败，已停止安装。");
        }
    }

    public static void VerifyAuthenticodeSignature(
        string filePath,
        IReadOnlyCollection<string> expectedPublishers,
        string packageLabel)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"{packageLabel} 下载文件不存在。", filePath);
        }

        using var winTrustState = new WinTrustFileState(filePath);
        var verifyResult = WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, winTrustState.DataPointer);
        if (verifyResult != 0)
        {
            throw new InvalidOperationException($"{packageLabel} 的 Authenticode 签名校验失败，已停止安装。");
        }

        try
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            var simpleName = certificate.GetNameInfo(X509NameType.SimpleName, false);
            var publisherMatched = expectedPublishers.Count == 0
                || expectedPublishers.Any(expectedPublisher =>
                    string.Equals(simpleName, expectedPublisher, StringComparison.OrdinalIgnoreCase)
                    || certificate.Subject.Contains(expectedPublisher, StringComparison.OrdinalIgnoreCase));

            if (!publisherMatched)
            {
                throw new InvalidOperationException(
                    $"{packageLabel} 的签名发布者不符合预期，检测到发布者：{simpleName}。");
            }
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException($"{packageLabel} 缺少可验证的签名信息，已停止安装。", exception);
        }
    }

    internal static string? NormalizeSha256Digest(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }

        var normalized = hash.Trim();
        if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["sha256:".Length..];
        }

        return normalized.ToLowerInvariant();
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionId,
        IntPtr pWvtData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePathPointer;
        public IntPtr FileHandle;
        public IntPtr KnownSubjectPointer;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackDataPointer;
        public IntPtr SipClientDataPointer;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfoPointer;
        public uint StateAction;
        public IntPtr StateDataHandle;
        public IntPtr UrlReferencePointer;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettingsPointer;
    }

    private sealed class WinTrustFileState : IDisposable
    {
        private IntPtr _dataPointer;
        private IntPtr _fileInfoPointer;
        private IntPtr _filePathPointer;

        public WinTrustFileState(string filePath)
        {
            _filePathPointer = Marshal.StringToCoTaskMemUni(filePath);

            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePathPointer = _filePathPointer,
                FileHandle = IntPtr.Zero,
                KnownSubjectPointer = IntPtr.Zero
            };

            _fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, _fileInfoPointer, false);

            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                PolicyCallbackDataPointer = IntPtr.Zero,
                SipClientDataPointer = IntPtr.Zero,
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                FileInfoPointer = _fileInfoPointer,
                StateAction = WtdStateActionIgnore,
                StateDataHandle = IntPtr.Zero,
                UrlReferencePointer = IntPtr.Zero,
                ProviderFlags = WtdSaferFlag,
                UiContext = WtdUiContextExecute,
                SignatureSettingsPointer = IntPtr.Zero
            };

            _dataPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, _dataPointer, false);
        }

        public IntPtr DataPointer => _dataPointer;

        public void Dispose()
        {
            if (_dataPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinTrustData>(_dataPointer);
                Marshal.FreeCoTaskMem(_dataPointer);
                _dataPointer = IntPtr.Zero;
            }

            if (_fileInfoPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(_fileInfoPointer);
                Marshal.FreeCoTaskMem(_fileInfoPointer);
                _fileInfoPointer = IntPtr.Zero;
            }

            if (_filePathPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_filePathPointer);
                _filePathPointer = IntPtr.Zero;
            }
        }
    }
}
