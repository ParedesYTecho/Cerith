#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cerith.TelemetryService.Providers.AI.GeminiCli;

internal enum GeminiCliAuthStatus
{
    Ready,
    Unavailable,
    Unsupported,
    Error
}

internal sealed record GeminiCliAuthIdentity(string AccessToken, DateTimeOffset ExpiresAt);

internal readonly record struct GeminiCliAuthRead(GeminiCliAuthStatus Status, GeminiCliAuthIdentity? Identity,
    string? ErrorCode)
{
    public static GeminiCliAuthRead Ready(GeminiCliAuthIdentity identity) =>
        new(GeminiCliAuthStatus.Ready, identity, null);

    public static GeminiCliAuthRead Unavailable(string? code = null) =>
        new(GeminiCliAuthStatus.Unavailable, null, code);

    public static GeminiCliAuthRead Unsupported(string code) =>
        new(GeminiCliAuthStatus.Unsupported, null, code);

    public static GeminiCliAuthRead Error(string code) =>
        new(GeminiCliAuthStatus.Error, null, code);
}

internal sealed class GeminiCliQuotaAuthReader
{
    internal const int MaxFileBytes = 1024 * 1024;
    internal static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    private readonly string? _geminiCliHome;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, string?> _environmentReader;

    public GeminiCliQuotaAuthReader(string? geminiCliHome = null, TimeProvider? timeProvider = null,
        Func<string, string?>? environmentReader = null)
    {
        _geminiCliHome = geminiCliHome;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
    }

    public GeminiCliAuthRead Read()
    {
        string home;
        try
        {
            home = ResolveGlobalGeminiDirectory(_geminiCliHome ?? _environmentReader("GEMINI_CLI_HOME"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.GetTempPath());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or
            UnauthorizedAccessException or System.Security.SecurityException)
        { return GeminiCliAuthRead.Error("auth_path_unavailable"); }

        try
        {
            if (_environmentReader("GEMINI_FORCE_ENCRYPTED_FILE_STORAGE") == "true")
                return GeminiCliAuthRead.Unsupported("auth_storage_unsupported");
            if (!string.IsNullOrEmpty(_environmentReader("GOOGLE_GENAI_USE_GCA")) &&
                !string.IsNullOrEmpty(_environmentReader("GOOGLE_CLOUD_ACCESS_TOKEN")))
                return GeminiCliAuthRead.Unsupported("auth_mode_unsupported");

            var selectedType = ReadSelectedAuthType(Path.Combine(home, "settings.json"));
            if (selectedType is null)
                return GeminiCliAuthRead.Unsupported("auth_mode_unselected");
            if (!StringComparer.Ordinal.Equals(selectedType, "oauth-personal"))
                return GeminiCliAuthRead.Unsupported("auth_mode_unsupported");
            if (!string.IsNullOrEmpty(_environmentReader("GOOGLE_APPLICATION_CREDENTIALS")))
                return GeminiCliAuthRead.Unsupported("auth_mode_unsupported");

            var authPath = Path.Combine(home, "oauth_creds.json");
            if (!File.Exists(authPath))
                return string.IsNullOrEmpty(_environmentReader("GOOGLE_APPLICATION_CREDENTIALS"))
                    ? GeminiCliAuthRead.Unavailable()
                    : GeminiCliAuthRead.Unsupported("auth_mode_unsupported");

            var bytes = ReadBounded(authPath);
            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !TryGetBoundedString(root, "access_token", 64 * 1024, out var accessToken) ||
                    !IsSafeHeaderValue(accessToken) ||
                    !root.TryGetProperty("expiry_date", out var expiry) ||
                    expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt64(out var expiryMilliseconds))
                    return GeminiCliAuthRead.Error("auth_file_invalid");

                DateTimeOffset expiresAt;
                try { expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiryMilliseconds); }
                catch (ArgumentOutOfRangeException) { return GeminiCliAuthRead.Error("auth_file_invalid"); }

                if (expiresAt <= _timeProvider.GetUtcNow() + ExpirySkew)
                    return GeminiCliAuthRead.Unavailable("access_token_expired");

                return GeminiCliAuthRead.Ready(new GeminiCliAuthIdentity(accessToken, expiresAt));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (FileNotFoundException) { return GeminiCliAuthRead.Unavailable(); }
        catch (InvalidDataException) { return GeminiCliAuthRead.Error("auth_file_invalid"); }
        catch (JsonException) { return GeminiCliAuthRead.Error("auth_file_invalid"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        { return GeminiCliAuthRead.Error("auth_unavailable"); }
    }

    internal static string ResolveGlobalGeminiDirectory(string? geminiCliHome, string? userHome, string? tempPath)
    {
        var home = !string.IsNullOrEmpty(geminiCliHome) ? geminiCliHome : userHome;
        if (string.IsNullOrEmpty(home)) home = tempPath;
        if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home)) throw new InvalidDataException();
        return Path.GetFullPath(Path.Combine(home, ".gemini"));
    }

    private static string? ReadSelectedAuthType(string settingsPath)
    {
        if (!File.Exists(settingsPath)) return null;
        var bytes = ReadBounded(settingsPath);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            if (!root.TryGetProperty("security", out var security) || security.ValueKind == JsonValueKind.Null)
                return null;
            if (security.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            if (!security.TryGetProperty("auth", out var auth) || auth.ValueKind == JsonValueKind.Null) return null;
            if (auth.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            if (!auth.TryGetProperty("selectedType", out var selectedType) || selectedType.ValueKind == JsonValueKind.Null)
                return null;
            if (selectedType.ValueKind != JsonValueKind.String) throw new InvalidDataException();
            var value = selectedType.GetString();
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64) throw new InvalidDataException();
            return value;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool TryGetBoundedString(JsonElement parent, string name, int maximumLength, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > maximumLength) return false;
        value = candidate;
        return true;
    }

    private static bool IsSafeHeaderValue(string value)
    {
        foreach (var character in value)
            if (character < 0x21 || character > 0x7e) return false;
        return true;
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.SequentialScan);
        using var output = new MemoryStream(Math.Min((int)Math.Min(stream.Length, MaxFileBytes), 8192));
        var buffer = new byte[8192];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > MaxFileBytes) throw new InvalidDataException();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
