using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace PiuLink.Host.Core.Install;

public sealed record AuthenticodeVerificationResult(
    bool IsValid,
    string? SignerSubject,
    string? SignerThumbprint,
    string Message);

public static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyAction =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static AuthenticodeVerificationResult Verify(
        string filePath,
        string expectedSubject,
        string expectedThumbprint)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new AuthenticodeVerificationResult(false, null, null, "Authenticode verification requires Windows.");
        }

        if (!File.Exists(filePath))
        {
            return new AuthenticodeVerificationResult(false, null, null, "The signed file does not exist.");
        }

        var trustResult = VerifyWindowsTrust(filePath);
        if (trustResult != 0)
        {
            return new AuthenticodeVerificationResult(
                false,
                null,
                null,
                $"Windows rejected the Authenticode signature (0x{trustResult:X8}).");
        }

        try
        {
            using var sourceCertificate = X509Certificate.CreateFromSignedFile(filePath);
            using var certificate = new X509Certificate2(sourceCertificate);
            var subjectMatches = string.Equals(
                certificate.Subject,
                expectedSubject,
                StringComparison.Ordinal);
            var thumbprintMatches = string.Equals(
                certificate.Thumbprint,
                expectedThumbprint,
                StringComparison.OrdinalIgnoreCase);
            if (!subjectMatches || !thumbprintMatches)
            {
                return new AuthenticodeVerificationResult(
                    false,
                    certificate.Subject,
                    certificate.Thumbprint,
                    "The payload is signed, but not by the reviewed Sunshine certificate.");
            }

            return new AuthenticodeVerificationResult(
                true,
                certificate.Subject,
                certificate.Thumbprint,
                "The Authenticode signature and signer identity are valid.");
        }
        catch (System.Security.Cryptography.CryptographicException exception)
        {
            return new AuthenticodeVerificationResult(false, null, null, exception.Message);
        }
    }

    private static int VerifyWindowsTrust(string filePath)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePath,
            FileHandle = IntPtr.Zero,
            KnownSubject = IntPtr.Zero
        };
        var fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());

        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var trustData = new WinTrustData
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
                PolicyCallbackData = IntPtr.Zero,
                SipClientData = IntPtr.Zero,
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfoPointer = fileInfoPointer,
                StateAction = 0,
                StateData = IntPtr.Zero,
                UrlReference = IntPtr.Zero,
                ProviderFlags = 0,
                UiContext = 0
            };
            var action = GenericVerifyAction;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeCoTaskMem(fileInfoPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [In] ref Guid actionId,
        [In] ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructureSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;

        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructureSize;
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
    }
}
