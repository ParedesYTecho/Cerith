#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Cerith.TelemetryService;

internal enum DevinAuthStatus
{
    Ready,
    Unconfigured,
    Error
}

internal sealed class DevinCliCredential(string apiKey)
{
    public string ApiKey { get; } = apiKey;

    public override string ToString() => "DevinCliCredential { ApiKey = <redacted> }";
}

internal readonly record struct DevinAuthRead(DevinAuthStatus Status, DevinCliCredential? Credential,
    string? ErrorCode)
{
    public static DevinAuthRead Ready(DevinCliCredential credential) => new(DevinAuthStatus.Ready, credential, null);
    public static DevinAuthRead Unconfigured() => new(DevinAuthStatus.Unconfigured, null, "not_configured");
    public static DevinAuthRead Error(string code) => new(DevinAuthStatus.Error, null, code);
}

internal sealed class DevinQuotaAuthReader
{
    internal const int MaxCredentialFileBytes = 32 * 1024;
    internal const int MaxApiKeyLength = 8192;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string? _applicationDataDirectory;

    public DevinQuotaAuthReader(string? applicationDataDirectory = null) =>
        _applicationDataDirectory = applicationDataDirectory;

    public DevinAuthRead Read()
    {
        string applicationData;
        try
        {
            applicationData = _applicationDataDirectory ??
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(applicationData)) return DevinAuthRead.Error("auth_path_unavailable");
        }
        catch (Exception)
        {
            return DevinAuthRead.Error("auth_path_unavailable");
        }

        string path;
        try
        {
            path = Path.Combine(applicationData, "devin", "credentials.toml");
        }
        catch (Exception)
        {
            return DevinAuthRead.Error("auth_path_unavailable");
        }

        byte[] contents = new byte[MaxCredentialFileBytes + 1];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var length = 0;
            while (length < contents.Length)
            {
                var read = stream.Read(contents, length, contents.Length - length);
                if (read == 0) break;
                length += read;
            }

            if (length > MaxCredentialFileBytes) return DevinAuthRead.Error("credential_file_too_large");
            var raw = StrictUtf8.GetString(contents, 0, length);
            return Parse(raw);
        }
        catch (FileNotFoundException)
        {
            return DevinAuthRead.Unconfigured();
        }
        catch (DirectoryNotFoundException)
        {
            return DevinAuthRead.Unconfigured();
        }
        catch (DecoderFallbackException)
        {
            return DevinAuthRead.Error("credential_file_invalid");
        }
        catch (Exception)
        {
            return DevinAuthRead.Error("credential_read_failed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contents);
        }
    }

    private static DevinAuthRead Parse(string raw)
    {
        string? apiKey = null;
        string? apiServerUrl = null;
        var apiKeySeen = false;
        var apiServerUrlSeen = false;
        var firstLine = true;

        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (firstLine)
            {
                trimmed = trimmed.TrimStart('\uFEFF');
                firstLine = false;
            }

            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            if (trimmed.StartsWith('[')) break;

            var separator = trimmed.IndexOf('=');
            if (separator < 0) continue;
            var key = trimmed[..separator].Trim();
            if (key is not ("windsurf_api_key" or "api_server_url")) continue;
            if (!TryParseString(trimmed[(separator + 1)..], out var value))
                return DevinAuthRead.Error("credential_file_invalid");

            if (key == "windsurf_api_key")
            {
                if (apiKeySeen) return DevinAuthRead.Error("credential_file_invalid");
                apiKeySeen = true;
                apiKey = value;
            }
            else
            {
                if (apiServerUrlSeen) return DevinAuthRead.Error("credential_file_invalid");
                apiServerUrlSeen = true;
                apiServerUrl = value;
            }
        }

        if (!apiKeySeen || !IsSafeApiKey(apiKey)) return DevinAuthRead.Error("credential_missing_key");
        if (apiServerUrlSeen && !IsFixedApiServer(apiServerUrl))
            return DevinAuthRead.Error("endpoint_override_unsupported");

        return DevinAuthRead.Ready(new DevinCliCredential(apiKey!));
    }

    private static bool TryParseString(string raw, out string value)
    {
        value = string.Empty;
        var text = raw.TrimStart();
        if (text.Length < 2 || text[0] is not ('"' or '\'')) return false;

        var quote = text[0];
        var closing = text.IndexOf(quote, 1);
        if (closing < 1) return false;
        var parsed = text[1..closing];
        if (parsed.Contains('\\')) return false;

        var remainder = text[(closing + 1)..].TrimStart();
        if (remainder.Length > 0 && !remainder.StartsWith('#')) return false;
        value = parsed;
        return true;
    }

    private static bool IsSafeApiKey(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxApiKeyLength) return false;
        foreach (var character in value)
            if (character < 0x21 || character > 0x7e) return false;
        return true;
    }

    private static bool IsFixedApiServer(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps &&
            StringComparer.OrdinalIgnoreCase.Equals(uri.DnsSafeHost, "server.codeium.com") &&
            uri.Port == 443 &&
            uri.UserInfo.Length == 0 &&
            uri.AbsolutePath == "/" &&
            uri.Query.Length == 0 &&
            uri.Fragment.Length == 0;
    }
}
