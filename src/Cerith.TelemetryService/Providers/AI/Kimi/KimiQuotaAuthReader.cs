#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cerith.TelemetryService.Providers.AI.Kimi;

internal enum KimiAuthStatus { Ready, Unavailable, Expired, Error }

internal sealed record KimiAuthIdentity(string AccessToken);

internal readonly record struct KimiAuthRead(KimiAuthStatus Status, KimiAuthIdentity? Identity,
    string? ErrorCode);

internal sealed class KimiQuotaAuthReader
{
    private const int MaxFileBytes = 256 * 1024;
    private readonly string? _codeHome;
    private readonly string? _legacyHome;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, string?> _readEnvironment;

    public KimiQuotaAuthReader(string? codeHome = null, string? legacyHome = null,
        TimeProvider? timeProvider = null, Func<string, string?>? readEnvironment = null)
    {
        _codeHome = codeHome;
        _legacyHome = legacyHome;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
    }

    public KimiAuthRead Read()
    {
        try
        {
            if (!UsesDefaultOAuthRoute())
                return new(KimiAuthStatus.Error, null, "auth_selection_unverified");
            var codeHome = ResolveHome(_codeHome, "KIMI_CODE_HOME", ".kimi-code");
            if (HasConfiguration(codeHome))
                return new(KimiAuthStatus.Error, null, "auth_selection_unverified");
            var codePath = CredentialPath(codeHome);
            if (File.Exists(codePath)) return ReadFile(codePath);

            var legacyHome = ResolveHome(_legacyHome, "KIMI_HOME", ".kimi");
            if (HasConfiguration(legacyHome))
                return new(KimiAuthStatus.Error, null, "auth_selection_unverified");
            var legacyPath = CredentialPath(legacyHome);
            return File.Exists(legacyPath) ? ReadFile(legacyPath) : new(KimiAuthStatus.Unavailable, null, null);
        }
        catch (FileNotFoundException) { return new(KimiAuthStatus.Unavailable, null, null); }
        catch (DirectoryNotFoundException) { return new(KimiAuthStatus.Unavailable, null, null); }
        catch (Exception) { return new(KimiAuthStatus.Error, null, "auth_unavailable"); }
    }

    private KimiAuthRead ReadFile(string path)
    {
        byte[]? bytes = null;
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length is <= 0 or > MaxFileBytes)
                    return new(KimiAuthStatus.Error, null, "auth_file_invalid");
                bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
            }

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("access_token", out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String)
                return new(KimiAuthStatus.Error, null, "auth_file_invalid");

            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token) || token.Length > 64 * 1024 || !IsSafeBearerToken(token))
                return new(KimiAuthStatus.Error, null, "auth_file_invalid");

            if (root.TryGetProperty("expires_at", out var expires) && expires.ValueKind is not JsonValueKind.Null)
            {
                if (!TryReadUnixSeconds(expires, out var seconds))
                    return new(KimiAuthStatus.Error, null, "auth_file_invalid");
                var maximumSeconds = (DateTimeOffset.MaxValue.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) /
                    (decimal)TimeSpan.TicksPerSecond;
                if (seconds < 0 || seconds > maximumSeconds)
                    return new(KimiAuthStatus.Error, null, "auth_file_invalid");
                var nowSeconds = (_timeProvider.GetUtcNow().UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) /
                    (decimal)TimeSpan.TicksPerSecond;
                if (seconds > 0 && seconds <= nowSeconds)
                    return new(KimiAuthStatus.Expired, null, "auth_expired");
            }

            return new(KimiAuthStatus.Ready, new KimiAuthIdentity(token), null);
        }
        catch (FileNotFoundException) { return new(KimiAuthStatus.Unavailable, null, null); }
        catch (InvalidDataException) { return new(KimiAuthStatus.Error, null, "auth_file_invalid"); }
        catch (JsonException) { return new(KimiAuthStatus.Error, null, "auth_file_invalid"); }
        catch (Exception) { return new(KimiAuthStatus.Error, null, "auth_unavailable"); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private string ResolveHome(string? configured, string variable, string suffix)
    {
        var candidate = configured;
        if (string.IsNullOrWhiteSpace(candidate)) candidate = _readEnvironment(variable);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile)) throw new IOException();
            candidate = Path.Combine(profile, suffix);
        }
        return Path.GetFullPath(candidate);
    }

    private static string CredentialPath(string home) => Path.Combine(home, "credentials", "kimi-code.json");

    private bool UsesDefaultOAuthRoute()
    {
        var baseUrl = _readEnvironment("KIMI_CODE_BASE_URL");
        if (baseUrl is not null && (string.IsNullOrWhiteSpace(baseUrl) ||
            KimiQuotaProvider.ResolveUsageEndpoint(baseUrl) != KimiQuotaProvider.UsageEndpoint)) return false;
        var oauthHost = _readEnvironment("KIMI_CODE_OAUTH_HOST") ?? _readEnvironment("KIMI_OAUTH_HOST");
        return oauthHost is null || StringComparer.OrdinalIgnoreCase.Equals(
            oauthHost.Trim().TrimEnd('/'), "https://auth.kimi.com");
    }

    private static bool HasConfiguration(string home)
    {
        try { _ = File.GetAttributes(Path.Combine(home, "config.toml")); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool TryReadUnixSeconds(JsonElement element, out decimal seconds)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out seconds)) return true;
        if (element.ValueKind == JsonValueKind.String && decimal.TryParse(element.GetString(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) return true;
        seconds = 0;
        return false;
    }

    private static bool IsSafeBearerToken(string token)
    {
        foreach (var character in token)
            if (char.IsControl(character) || char.IsWhiteSpace(character)) return false;
        return true;
    }
}
