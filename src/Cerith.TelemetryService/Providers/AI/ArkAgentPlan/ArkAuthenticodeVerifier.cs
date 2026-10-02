#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Cerith.TelemetryService.Providers.AI.ArkAgentPlan;

internal static class ArkAuthenticodeVerifier
{
    internal const string ExpectedSha256 = "86D640FFCCAFA3CA5562536F226A7AADFBB362566741C1EA7E3A6A536FB58C05";
    private const long MaximumExecutableBytes = 96L * 1024 * 1024;
    private const uint WtdChoiceFile = 1;
    private const uint WtdUiNone = 2;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;
    private const uint WtdDisableMd2Md4 = 0x00002000;
    private const uint WtdRevocationCheckChainExcludeRoot = 0x00000080;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static async Task<FileStream?> OpenVerifiedExecutableAsync(string path,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        FileStream? file = null;
        var keepOpen = false;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length is <= 0 or > MaximumExecutableBytes) return null;

            var digest = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!StringComparer.Ordinal.Equals(Convert.ToHexString(digest), ExpectedSha256)) return null;
            if (!VerifyAuthenticodeSignature(path)) return null;

#pragma warning disable SYSLIB0057
            using var signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate = new X509Certificate2(signer);
            if (!IsExpectedPublisher(certificate)) return null;
            keepOpen = true;
            return file;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (!keepOpen) file?.Dispose();
        }
    }

    internal static bool IsExpectedPublisher(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var attributes = certificate.SubjectName.Decode(X500DistinguishedNameFlags.UseNewLines)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => value.Trim())
            .Select(static value =>
            {
                var separator = value.IndexOf('=');
                return separator < 1 ? (Name: string.Empty, Value: string.Empty) :
                    (Name: value[..separator].Trim(), Value: value[(separator + 1)..].Trim());
            })
            .ToArray();
        return attributes.Any(static attribute =>
                   StringComparer.Ordinal.Equals(attribute.Name, "CN") &&
                   StringComparer.Ordinal.Equals(attribute.Value, "北京火山引擎科技有限公司")) &&
               attributes.Any(static attribute =>
                   StringComparer.Ordinal.Equals(attribute.Name, "O") &&
                   StringComparer.Ordinal.Equals(attribute.Value, "北京火山引擎科技有限公司"));
    }

    private static bool VerifyAuthenticodeSignature(string path)
    {
        var pathPointer = Marshal.StringToCoTaskMemUni(path);
        var fileInfoPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                UnionChoice = WtdChoiceFile,
                FileInfo = fileInfoPointer,
                ProviderFlags = WtdRevocationCheckChainExcludeRoot |
                    WtdCacheOnlyUrlRetrieval | WtdDisableMd2Md4
            };
            var actionId = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref actionId, ref trustData) == 0;
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
            }
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr windowHandle, ref Guid actionId, ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructureSize;
        public IntPtr FilePath;
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
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
