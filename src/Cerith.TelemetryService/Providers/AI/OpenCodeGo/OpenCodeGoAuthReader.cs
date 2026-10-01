#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cerith.TelemetryService;

internal enum OpenCodeGoAuthStatus
{
    Ready,
    Unavailable,
    Unsupported,
    Error
}

internal sealed class OpenCodeGoCredential
{
    public string ApiKey { get; }
    public string Fingerprint { get; }

    public OpenCodeGoCredential(string apiKey, string fingerprint)
    {
        ApiKey = apiKey;
        Fingerprint = fingerprint;
    }
}

internal readonly struct OpenCodeGoAuthRead
{
    public OpenCodeGoAuthStatus Status { get; }
    public OpenCodeGoCredential? Credential { get; }
    public string? ErrorCode { get; }
    public string SourceFingerprint { get; }

    private OpenCodeGoAuthRead(OpenCodeGoAuthStatus status, OpenCodeGoCredential? credential,
        string? errorCode, string sourceFingerprint)
    {
        Status = status;
        Credential = credential;
        ErrorCode = errorCode;
        SourceFingerprint = sourceFingerprint;
    }

    public static OpenCodeGoAuthRead Ready(OpenCodeGoCredential credential, string sourceFingerprint) =>
        new(OpenCodeGoAuthStatus.Ready, credential, null, sourceFingerprint);
    public static OpenCodeGoAuthRead Unavailable(string sourceFingerprint) =>
        new(OpenCodeGoAuthStatus.Unavailable, null, null, sourceFingerprint);
    public static OpenCodeGoAuthRead Unsupported(string code, string sourceFingerprint) =>
        new(OpenCodeGoAuthStatus.Unsupported, null, code, sourceFingerprint);
    public static OpenCodeGoAuthRead Error(string code, string sourceFingerprint) =>
        new(OpenCodeGoAuthStatus.Error, null, code, sourceFingerprint);
}

internal sealed class OpenCodeGoAuthReader
{
    internal const int MaxFileBytes = 256 * 1024;
    // Bound path normalization and fingerprint work for environment-selected stores.
    private const int MaxPathLength = 4096;
    private const string AuthContentVariable = "OPENCODE_AUTH_CONTENT";
    private const string XdgDataHomeVariable = "XDG_DATA_HOME";
    private readonly string? _directory;
    private readonly Func<string, string?> _getEnvironmentVariable;

    public OpenCodeGoAuthReader(string? directory = null, Func<string, string?>? getEnvironmentVariable = null)
    {
        _directory = directory;
        _getEnvironmentVariable = getEnvironmentVariable ?? (name => Environment.GetEnvironmentVariable(name));
    }

    public OpenCodeGoAuthRead Read()
    {
        if (_directory is null)
        {
            string? authContent;
            try
            {
                authContent = _getEnvironmentVariable(AuthContentVariable);
            }
            catch (Exception)
            {
                return OpenCodeGoAuthRead.Error("auth_source_unavailable",
                    FingerprintSource("environment:OPENCODE_AUTH_CONTENT", "unavailable", ReadOnlySpan<byte>.Empty));
            }

            if (!string.IsNullOrEmpty(authContent)) return ReadAuthContent(authContent);
        }

        string path;
        string source;
        try
        {
            string home;
            if (_directory is not null)
            {
                home = _directory;
            }
            else
            {
                string? xdgDataHome;
                try
                {
                    xdgDataHome = _getEnvironmentVariable(XdgDataHomeVariable);
                }
                catch (Exception)
                {
                    return OpenCodeGoAuthRead.Error("auth_source_unavailable",
                        FingerprintSource("environment:XDG_DATA_HOME", "unavailable", ReadOnlySpan<byte>.Empty));
                }

                if (!string.IsNullOrEmpty(xdgDataHome))
                {
                    if (xdgDataHome.Length > MaxPathLength || !Path.IsPathFullyQualified(xdgDataHome))
                    {
                        var fingerprint = xdgDataHome.Length > MaxPathLength
                            ? FingerprintText("environment:XDG_DATA_HOME", "unsupported_length", xdgDataHome.Length.ToString())
                            : FingerprintText("environment:XDG_DATA_HOME", "unsupported_path", xdgDataHome);
                        return OpenCodeGoAuthRead.Error("auth_path_unsupported", fingerprint);
                    }

                    home = Path.Combine(xdgDataHome, "opencode");
                }
                else
                {
                    var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (string.IsNullOrWhiteSpace(profile))
                        return OpenCodeGoAuthRead.Error("auth_path_unavailable",
                            FingerprintSource("default-auth-path", "unavailable", ReadOnlySpan<byte>.Empty));
                    home = Path.Combine(profile, ".local", "share", "opencode");
                }
            }

            if (string.IsNullOrWhiteSpace(home))
                return OpenCodeGoAuthRead.Error("auth_path_unavailable",
                    FingerprintSource("auth-path", "unavailable", ReadOnlySpan<byte>.Empty));
            if (home.Length > MaxPathLength)
                return OpenCodeGoAuthRead.Error("auth_path_unsupported",
                    FingerprintText("auth-path", "unsupported_length", home.Length.ToString()));

            path = Path.Combine(Path.GetFullPath(home), "auth.json");
            if (path.Length > MaxPathLength)
                return OpenCodeGoAuthRead.Error("auth_path_unsupported",
                    FingerprintText("auth-path", "unsupported_length", path.Length.ToString()));
            source = "file:" + path;
        }
        catch (Exception)
        {
            return OpenCodeGoAuthRead.Error("auth_path_unavailable",
                FingerprintSource("auth-path", "unavailable", ReadOnlySpan<byte>.Empty));
        }

        return ReadFile(path, source);
    }

    private static OpenCodeGoAuthRead ReadAuthContent(string authContent)
    {
        if (authContent.Length > MaxFileBytes)
            return OpenCodeGoAuthRead.Error("auth_content_too_large",
                FingerprintText("environment:OPENCODE_AUTH_CONTENT", "oversized", authContent.Length.ToString()));

        int byteCount;
        try
        {
            byteCount = Encoding.UTF8.GetByteCount(authContent);
        }
        catch (Exception)
        {
            return OpenCodeGoAuthRead.Error("auth_content_invalid",
                FingerprintSource("environment:OPENCODE_AUTH_CONTENT", "invalid", ReadOnlySpan<byte>.Empty));
        }

        if (byteCount > MaxFileBytes)
            return OpenCodeGoAuthRead.Error("auth_content_too_large",
                FingerprintText("environment:OPENCODE_AUTH_CONTENT", "oversized", authContent.Length.ToString()));

        byte[]? contents = null;
        try
        {
            contents = Encoding.UTF8.GetBytes(authContent);
            var fingerprint = FingerprintSource("environment:OPENCODE_AUTH_CONTENT", "contents", contents);
            return Parse(contents, fingerprint);
        }
        catch (Exception)
        {
            return OpenCodeGoAuthRead.Error("auth_content_invalid",
                FingerprintSource("environment:OPENCODE_AUTH_CONTENT", "invalid", ReadOnlySpan<byte>.Empty));
        }
        finally
        {
            if (contents is not null) CryptographicOperations.ZeroMemory(contents);
        }
    }

    private static OpenCodeGoAuthRead ReadFile(string path, string source)
    {
        byte[]? contents = null;
        try
        {
            contents = ReadBounded(path);
            var fingerprint = FingerprintSource(source, "contents", contents);
            return Parse(contents, fingerprint);
        }
        catch (FileNotFoundException)
        {
            return OpenCodeGoAuthRead.Unavailable(FingerprintSource(source, "missing", ReadOnlySpan<byte>.Empty));
        }
        catch (DirectoryNotFoundException)
        {
            return OpenCodeGoAuthRead.Unavailable(FingerprintSource(source, "missing", ReadOnlySpan<byte>.Empty));
        }
        catch (InvalidDataException)
        {
            return OpenCodeGoAuthRead.Error("auth_file_invalid", FingerprintSource(source, "invalid", ReadOnlySpan<byte>.Empty));
        }
        catch (Exception)
        {
            return OpenCodeGoAuthRead.Error("auth_unavailable", FingerprintSource(source, "unavailable", ReadOnlySpan<byte>.Empty));
        }
        finally
        {
            if (contents is not null) CryptographicOperations.ZeroMemory(contents);
        }
    }

    private static OpenCodeGoAuthRead Parse(byte[] contents, string sourceFingerprint)
    {
        try
        {
            using var document = JsonDocument.Parse(contents, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return OpenCodeGoAuthRead.Error("auth_file_invalid", sourceFingerprint);
            if (!root.TryGetProperty("opencode-go", out var entry))
                return OpenCodeGoAuthRead.Unavailable(sourceFingerprint);
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                return OpenCodeGoAuthRead.Error("auth_file_invalid", sourceFingerprint);
            if (!StringComparer.Ordinal.Equals(type.GetString(), "api"))
                return OpenCodeGoAuthRead.Unsupported("auth_type_unsupported", sourceFingerprint);
            if (!entry.TryGetProperty("key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String)
                return OpenCodeGoAuthRead.Error("auth_file_invalid", sourceFingerprint);

            var key = keyElement.GetString();
            if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || !IsSafeHeaderValue(key))
                return OpenCodeGoAuthRead.Error("auth_file_invalid", sourceFingerprint);

            return OpenCodeGoAuthRead.Ready(new OpenCodeGoCredential(key, Fingerprint(key)), sourceFingerprint);
        }
        catch (JsonException)
        {
            return OpenCodeGoAuthRead.Error("auth_file_invalid", sourceFingerprint);
        }
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        var buffer = new byte[MaxFileBytes + 1];
        try
        {
            var count = 0;
            while (count < buffer.Length)
            {
                var read = stream.Read(buffer, count, buffer.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxFileBytes || stream.ReadByte() != -1) throw new InvalidDataException();
            var contents = new byte[count];
            buffer.AsSpan(0, count).CopyTo(contents);
            return contents;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static bool IsSafeHeaderValue(string value)
    {
        foreach (var character in value)
            if (character < 0x21 || character > 0x7e) return false;
        return true;
    }

    private static string Fingerprint(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string FingerprintText(string source, string materialKind, string material)
    {
        var bytes = Encoding.UTF8.GetBytes(material);
        try { return FingerprintSource(source, materialKind, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string FingerprintSource(string source, string materialKind, ReadOnlySpan<byte> material)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var kindBytes = Encoding.UTF8.GetBytes(materialKind);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(sourceBytes);
            hash.AppendData(new byte[] { 0 });
            hash.AppendData(kindBytes);
            hash.AppendData(new byte[] { 0 });
            hash.AppendData(material);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sourceBytes);
            CryptographicOperations.ZeroMemory(kindBytes);
        }
    }
}
