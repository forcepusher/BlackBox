using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Sloperator.Core.Web;

namespace Sloperator.Platform.Windows;

/// <summary>
/// A file's embedded Authenticode signature, as Windows checks it (WinVerifyTrust, the policy
/// Explorer and SmartScreen use): the file unaltered since it was signed, the certificate chained to a
/// root Windows trusts, revocation as Windows does by default; no UI. A valid one gives its signer
/// certificate's subject, the publisher, read from the very signature Windows checked.
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class WindowsSignature
{
    private static Guid _verifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2

    private const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;

    // No answer on revocation: Windows couldn't tell, which is no verdict on the file.
    private const int CERT_E_REVOCATION_FAILURE = unchecked((int)0x800B010E), CRYPT_E_REVOCATION_OFFLINE = unchecked((int)0x80092013);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public nint hFile;
        public Guid* pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public WINTRUST_FILE_INFO* pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public char* pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint hwnd, Guid* pgActionID, WINTRUST_DATA* pWVTData);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperProvDataFromStateData(nint hStateData);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvSignerFromChain(nint pProvData, uint idxSigner, int fCounterSigner, uint idxCounterSigner);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvCertFromChain(nint pSgnr, uint idxCert);

    /// <summary>The Authenticode signature of the file at <paramref name="path"/>; never throws.</summary>
    public static Signature Of(string path)
    {
        try
        {
            fixed (char* file = Path.GetFullPath(path))
            {
                var info = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = file };
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)sizeof(WINTRUST_DATA),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE, // with no provider flags: Windows' own revocation policy
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = &info,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                };
                var action = _verifyV2;
                var result = WinVerifyTrust(0, &action, &data);
                try
                {
                    if (result != 0)
                        return new Signature(result is CERT_E_REVOCATION_FAILURE or CRYPT_E_REVOCATION_OFFLINE
                            // The file couldn't be opened or read (HRESULT_FROM_WIN32): nothing told of its signature.
                            || (uint)result >> 16 == 0x8007 ? Signed.Unknown : Signed.No, Code: result);
                    return Publisher(data.hWVTStateData) is { } publisher
                        ? new Signature(Signed.Yes, publisher)
                        : new Signature(Signed.Unknown);
                }
                finally
                {
                    data.dwStateAction = WTD_STATEACTION_CLOSE;
                    WinVerifyTrust(0, &action, &data);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or System.Security.Cryptography.CryptographicException)
        {
            return new Signature(Signed.Unknown, Code: ex.HResult);
        }
    }

    /// <summary>The subject of the first signer's certificate in the state WinVerifyTrust left; null when it can't be read.</summary>
    private static string? Publisher(nint state)
    {
        var provider = WTHelperProvDataFromStateData(state);
        var signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
        var cert = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
        if (cert == 0)
            return null;
        // CRYPT_PROVIDER_CERT: a DWORD cbStruct, then the PCCERT_CONTEXT at pointer alignment.
        var context = *(nint*)(cert + sizeof(nint));
        if (context == 0)
            return null;
        using var certificate = new X509Certificate2(context);
        return certificate.Subject;
    }
}
