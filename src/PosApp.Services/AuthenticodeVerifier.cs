using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace PosApp.Services;

internal sealed record AuthenticodePublisher(
    string DisplayName,
    string Subject,
    string Thumbprint);

/// <summary>
/// Performs the same Authenticode publisher verification Windows uses before
/// PosApp is allowed to launch an offline update package.
/// </summary>
internal static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertEChaining = unchecked((int)0x800B010A);

    public static bool TryVerify(
        string path,
        out AuthenticodePublisher publisher,
        out string failure)
    {
        publisher = new AuthenticodePublisher(string.Empty, string.Empty, string.Empty);
        failure = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            failure = "Installer signature verification is available only on Windows.";
            return false;
        }

        var fileInfo = new WinTrustFileInfo(path);
        var fileInfoPointer = IntPtr.Zero;
        var trustData = new WinTrustData();
        try
        {
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            trustData.FileInfoPointer = fileInfoPointer;
            trustData.StateAction = WtdStateActionVerify;

            var result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref trustData);
            if (result != 0)
            {
                failure = result switch
                {
                    TrustENoSignature => "The installer is not digitally signed.",
                    TrustEBadDigest => "The installer signature is invalid or the file was modified after signing.",
                    CertEUntrustedRoot => "The installer publisher is not trusted by this computer.",
                    CertEChaining => "Windows could not build a trusted publisher certificate chain.",
                    _ => $"Windows rejected the installer signature (0x{result:X8})."
                };
                return false;
            }

            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            var displayName = signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (string.IsNullOrWhiteSpace(displayName)) displayName = signer.Subject;
            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(signer.Subject) ||
                string.IsNullOrWhiteSpace(signer.Thumbprint))
            {
                failure = "Windows verified the signature but could not identify its publisher.";
                return false;
            }

            publisher = new AuthenticodePublisher(
                displayName.Trim(), signer.Subject.Trim(), signer.Thumbprint.Trim());

            return true;
        }
        catch (Exception ex)
        {
            failure = $"Windows could not verify the installer publisher: {ex.Message}";
            return false;
        }
        finally
        {
            if (trustData.StateData != IntPtr.Zero)
            {
                trustData.StateAction = WtdStateActionClose;
                _ = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref trustData);
            }

            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
            }
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;

        public WinTrustFileInfo(string path)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = path;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfoPointer;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;

        public WinTrustData()
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = WtdUiNone;
            RevocationChecks = WtdRevokeNone;
            UnionChoice = WtdChoiceFile;
            FileInfoPointer = IntPtr.Zero;
            StateAction = WtdStateActionVerify;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            // PosApp is offline at runtime. Use only locally cached certificate
            // information while still enforcing Windows publisher trust.
            ProviderFlags = WtdCacheOnlyUrlRetrieval;
            UiContext = 0;
            SignatureSettings = IntPtr.Zero;
        }
    }
}
