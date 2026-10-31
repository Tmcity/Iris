using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Iris.Maintenance;

internal enum RuntimeTrustFailure { Verification, ProviderCertificate, CertificateContext, CertificateEncoding, Publisher }

internal static class RuntimeInstallerTrust
{
    internal const string FailureKey = "Iris.RuntimeTrustFailure";
    internal const string StatusKey = "Iris.RuntimeTrustStatus";
    private static InvalidDataException Invalid(RuntimeTrustFailure reason, int? status = null)
    {
        var error = new InvalidDataException("The desktop runtime installer signature is not trusted.");
        error.Data[FailureKey] = reason;
        if (status.HasValue) error.Data[StatusKey] = status.Value;
        return error;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        internal uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] internal string Path;
        internal nint File;
        internal nint Subject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        internal uint Size;
        internal nint Policy;
        internal nint Sip;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal nint File;
        internal uint StateAction;
        internal nint State;
        internal nint Url;
        internal uint Flags;
        internal uint UiContext;
        internal nint Signature;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, in Guid action, ref TrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint certificate);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate
    {
        internal uint Size;
        internal nint Certificate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        internal uint Encoding;
        internal nint Encoded;
        internal uint EncodedSize;
        internal nint Info;
        internal nint Store;
    }

    internal static void VerifyMicrosoft(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = pointer, StateAction = 1 };
        Marshal.StructureToPtr(file, pointer, false);
        try
        {
            int status = WinVerifyTrust(0, in action, ref data);
            if (status != 0) throw Invalid(RuntimeTrustFailure.Verification, status);
            nint provider = WTHelperProvDataFromStateData(data.State);
            nint signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            nint signerCertificate = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
            if (signerCertificate == 0) throw Invalid(RuntimeTrustFailure.ProviderCertificate);
            nint contextPointer = Marshal.PtrToStructure<ProviderCertificate>(signerCertificate).Certificate;
            if (contextPointer == 0) throw Invalid(RuntimeTrustFailure.CertificateContext);
            var context = Marshal.PtrToStructure<CertificateContext>(contextPointer);
            if (context.Encoded == 0 || context.EncodedSize is 0 or > 1048576) throw Invalid(RuntimeTrustFailure.CertificateEncoding);
            byte[] encoded = new byte[context.EncodedSize];
            Marshal.Copy(context.Encoded, encoded, 0, encoded.Length);
            using var certificate = X509CertificateLoader.LoadCertificate(encoded);
            if (!IsMicrosoftPublisher(certificate))
                throw Invalid(RuntimeTrustFailure.Publisher);
        }
        finally
        {
            data.StateAction = 2;
            _ = WinVerifyTrust(0, in action, ref data);
            Marshal.DestroyStructure<TrustFile>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    internal static bool IsMicrosoftPublisher(X509Certificate2 certificate)
    {
        var reader = new AsnReader(certificate.SubjectName.RawData, AsnEncodingRules.DER);
        var name = reader.ReadSequence();
        bool organization = false;
        while (name.HasData)
        {
            var attributes = name.ReadSetOf();
            while (attributes.HasData)
            {
                var attribute = attributes.ReadSequence();
                string oid = attribute.ReadObjectIdentifier();
                if (oid == "2.5.4.10")
                {
                    Asn1Tag tag = attribute.PeekTag();
                    if (tag.TagClass != TagClass.Universal || organization) return false;
                    string value = attribute.ReadCharacterString((UniversalTagNumber)tag.TagValue);
                    if (value != "Microsoft Corporation") return false;
                    organization = true;
                }
                else _ = attribute.ReadEncodedValue();
                attribute.ThrowIfNotEmpty();
            }
        }
        reader.ThrowIfNotEmpty();
        return organization;
    }
}
