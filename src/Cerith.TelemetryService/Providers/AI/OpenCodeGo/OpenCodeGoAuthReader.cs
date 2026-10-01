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

    private OpenCodeGoAuthRead(OpenCodeGoAuthStatus status, OpenCodeGoCredential? credential, string? errorCode)
    {
        Status = status;
        Credential = credential;
        ErrorCode = errorCode;
    }

    public static OpenCodeGoAuthRead Ready(OpenCodeGoCredential credential) =>
        new(OpenCodeGoAuthStatus.Ready, credential, null);
    public static OpenCodeGoAuthRead Unavailable() => new(OpenCodeGoAuthStatus.Unavailable, null, null);
    public static OpenCodeGoAuthRead Unsupported(string code) => new(OpenCodeGoAuthStatus.Unsupported, null, code);
    public static OpenCodeGoAuthRead Error(string code) => new(OpenCodeGoAuthStatus.Error, null, code);
}

internal sealed class OpenCodeGoAuthReader
{
    internal const int MaxFileBytes = 256 * 1024;
    private readonly string? _directory;

    public OpenCodeGoAuthReader(string? directory = null) => _directory = directory;

    public OpenCodeGoAuthRead Read()
    {
        string path;
        try
        {
            var home = _directory;
            if (home is null)
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(profile)) return OpenCodeGoAuthRead.Error("auth_path_unavailable");
                home = Path.Combine(profile, ".local", "share", "opencode");
            }
            if (string.IsNullOrWhiteSpace(home)) return OpenCodeGoAuthRead.Error("auth_path_unavailable");
            path = Path.Combine(Path.GetFullPath(home), "auth.json");
        }
        catch (Exception)
        {
            return OpenCodeGoAuthRead.Error("auth_path_unavailable");
        }

        if (!File.Exists(path)) return OpenCodeGoAuthRead.Unavailable();

        byte[]? contents = null;
        try
        {
            contents = ReadBounded(path);
            using var document = JsonDocument.Parse(contents, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return OpenCodeGoAuthRead.Error("auth_file_invalid");
            if (!root.TryGetProperty("opencode-go", out var entry))
                return OpenCodeGoAuthRead.Unavailable();
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                return OpenCodeGoAuthRead.Error("auth_file_invalid");
            if (!StringComparer.Ordinal.Equals(type.GetString(), "api"))
                return OpenCodeGoAuthRead.Unsupported("auth_type_unsupported");
            if (!entry.TryGetProperty("key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String)
                return OpenCodeGoAuthRead.Error("auth_file_invalid");

            var key = keyElement.GetString();
            if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || !IsSafeHeaderValue(key))
                return OpenCodeGoAuthRead.Error("auth_file_invalid");

            return OpenCodeGoAuthRead.Ready(new OpenCodeGoCredential(key, Fingerprint(key)));
        }
        catch (FileNotFoundException)
        {
            return OpenCodeGoAuthRead.Unavailable();
        }
        catch (InvalidDataException)
        {
            return OpenCodeGoAuthRead.Error("auth_file_invalid");
        }
        catch (JsonException)
        {
            return OpenCodeGoAuthRead.Error("auth_file_invalid");
        }
        catch (Exception)
        {
            return OpenCodeGoAuthRead.Error("auth_unavailable");
        }
        finally
        {
            if (contents is not null) CryptographicOperations.ZeroMemory(contents);
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
}
