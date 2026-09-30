#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cerith.TelemetryService.Providers.AI.Kimi;

internal enum KimiAuthStatus { Ready, Unavailable, RefreshPending, LoginRequired, Error }

internal sealed record KimiAuthIdentity(string AccessToken, bool HasRefreshToken);

internal readonly record struct KimiAuthRead(KimiAuthStatus Status, KimiAuthIdentity? Identity,
    string? ErrorCode);

internal sealed class KimiQuotaAuthReader
{
    private const int MaxFileBytes = 256 * 1024;
    private const string ManagedProvider = "managed:kimi-code";
    private const string ExpectedOAuthKey = "oauth/kimi-code";
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
            var environmentError = GetEnvironmentSelectionError();
            if (environmentError is not null) return new(KimiAuthStatus.Error, null, environmentError);

            var codeHome = ResolveHome(_codeHome, "KIMI_CODE_HOME", ".kimi-code");
            var configError = VerifyConfiguration(codeHome, out var hasConfig);
            if (configError is not null) return new(KimiAuthStatus.Error, null, configError);
            var codePath = CredentialPath(codeHome);
            if (File.Exists(codePath)) return ReadFile(codePath);
            if (hasConfig) return new(KimiAuthStatus.Unavailable, null, null);

            var legacyHome = ResolveHome(_legacyHome, "KIMI_HOME", ".kimi");
            configError = VerifyConfiguration(legacyHome, out _);
            if (configError is not null) return new(KimiAuthStatus.Error, null, configError);
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

            var hasRefreshToken = false;
            if (root.TryGetProperty("refresh_token", out var refreshToken) &&
                refreshToken.ValueKind is not JsonValueKind.Null)
            {
                if (refreshToken.ValueKind != JsonValueKind.String)
                    return new(KimiAuthStatus.Error, null, "auth_file_invalid");
                hasRefreshToken = !string.IsNullOrWhiteSpace(refreshToken.GetString());
            }

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
                    return hasRefreshToken
                        ? new(KimiAuthStatus.RefreshPending, null, "auth_refresh_pending")
                        : new(KimiAuthStatus.LoginRequired, null, "auth_required");
            }

            return new(KimiAuthStatus.Ready, new KimiAuthIdentity(token, hasRefreshToken), null);
        }
        catch (FileNotFoundException) { return new(KimiAuthStatus.Unavailable, null, null); }
        catch (InvalidDataException) { return new(KimiAuthStatus.Error, null, "auth_file_invalid"); }
        catch (JsonException) { return new(KimiAuthStatus.Error, null, "auth_file_invalid"); }
        catch (Exception) { return new(KimiAuthStatus.Error, null, "auth_unavailable"); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private string? GetEnvironmentSelectionError()
    {
        var baseUrl = _readEnvironment("KIMI_CODE_BASE_URL");
        if (baseUrl is not null && (string.IsNullOrWhiteSpace(baseUrl) ||
            KimiQuotaProvider.ResolveUsageEndpoint(baseUrl) != KimiQuotaProvider.UsageEndpoint))
            return "auth_selection_conflict";
        if (!string.IsNullOrWhiteSpace(_readEnvironment("KIMI_MODEL_NAME")))
            return "auth_selection_conflict";

        var oauthHost = _readEnvironment("KIMI_CODE_OAUTH_HOST") ?? _readEnvironment("KIMI_OAUTH_HOST");
        return oauthHost is not null && !StringComparer.OrdinalIgnoreCase.Equals(
            oauthHost.Trim().TrimEnd('/'), "https://auth.kimi.com")
            ? "auth_selection_conflict"
            : null;
    }

    private string? VerifyConfiguration(string home, out bool hasConfiguration)
    {
        var path = Path.Combine(home, "config.toml");
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { hasConfiguration = false; return null; }
        catch (DirectoryNotFoundException) { hasConfiguration = false; return null; }
        catch (Exception) { hasConfiguration = true; return "auth_selection_unverified"; }

        hasConfiguration = true;
        if ((attributes & FileAttributes.Directory) != 0) return "auth_selection_unverified";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxFileBytes) return "auth_selection_unverified";
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            return VerifyConfigurationText(reader);
        }
        catch (Exception) { return "auth_selection_unverified"; }
    }

    private static string? VerifyConfigurationText(TextReader reader)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var tables = new HashSet<string>(StringComparer.Ordinal);
        var section = "root";
        var modelAlias = string.Empty;
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            var content = StripTomlComment(line).Trim();
            if (content.Length == 0) continue;
            if (content[0] == '[')
            {
                section = ReadSection(content, out modelAlias);
                if (section != "other" && !tables.Add(section + ":" + modelAlias))
                    return "auth_selection_unverified";
                continue;
            }

            var separator = content.IndexOf('=');
            if (separator <= 0) continue;
            var key = content[..separator].Trim();
            var rawValue = content[(separator + 1)..].Trim();
            string? target = null;
            if (section == "root" && key == "default_model") target = "default_model";
            else if (section == "model" && key == "provider") target = "model_provider:" + modelAlias;
            else if (section == "provider" && key is "type" or "base_url" or "api_key" or "api_key_env")
                target = "provider:" + key;
            else if (section == "oauth" && key is "storage" or "key") target = "oauth:" + key;
            else if (section == "env" && key is "KIMI_API_KEY" or "KIMI_BASE_URL") target = "env:" + key;

            if (target is null) continue;
            if (!assigned.Add(target) || !TryReadTomlString(rawValue, out var value))
                return "auth_selection_unverified";
            values.Add(target, value);
        }

        if (!values.TryGetValue("default_model", out var model) ||
            !values.TryGetValue("model_provider:" + model, out var selectedProvider))
            return "auth_selection_unverified";
        if (selectedProvider != ManagedProvider) return "auth_selection_conflict";
        if (!values.TryGetValue("provider:type", out var type)) return "auth_selection_unverified";
        if (type != "kimi") return "auth_selection_conflict";
        if (values.TryGetValue("provider:base_url", out var baseUrl) &&
            KimiQuotaProvider.ResolveUsageEndpoint(baseUrl) != KimiQuotaProvider.UsageEndpoint)
            return "auth_selection_conflict";
        if (values.TryGetValue("provider:api_key", out var apiKey) && apiKey.Length != 0 ||
            values.TryGetValue("provider:api_key_env", out var apiKeyEnv) && apiKeyEnv.Length != 0 ||
            values.TryGetValue("env:KIMI_API_KEY", out var configuredApiKey) && configuredApiKey.Length != 0)
            return "auth_selection_conflict";
        if (values.TryGetValue("env:KIMI_BASE_URL", out var configuredBaseUrl) &&
            configuredBaseUrl.Length != 0 &&
            KimiQuotaProvider.ResolveUsageEndpoint(configuredBaseUrl) != KimiQuotaProvider.UsageEndpoint)
            return "auth_selection_conflict";
        if (!values.TryGetValue("oauth:storage", out var storage) ||
            !values.TryGetValue("oauth:key", out var oauthKey)) return "auth_selection_unverified";
        return storage == "file" && oauthKey == ExpectedOAuthKey ? null : "auth_selection_conflict";
    }

    private static string ReadSection(string header, out string modelAlias)
    {
        modelAlias = string.Empty;
        if (header is "[providers.\"managed:kimi-code\"]" or "[providers.'managed:kimi-code']") return "provider";
        if (header is "[providers.\"managed:kimi-code\".oauth]" or "[providers.'managed:kimi-code'.oauth]") return "oauth";
        if (header is "[providers.\"managed:kimi-code\".env]" or "[providers.'managed:kimi-code'.env]") return "env";
        const string doublePrefix = "[models.\"";
        const string singlePrefix = "[models.'";
        if (header.StartsWith(doublePrefix, StringComparison.Ordinal) && header.EndsWith("\"]", StringComparison.Ordinal))
        {
            modelAlias = header[doublePrefix.Length..^2];
            return modelAlias.Length == 0 || modelAlias.Contains('"') ? "other" : "model";
        }
        if (header.StartsWith(singlePrefix, StringComparison.Ordinal) && header.EndsWith("']", StringComparison.Ordinal))
        {
            modelAlias = header[singlePrefix.Length..^2];
            return modelAlias.Length == 0 || modelAlias.Contains('\'') ? "other" : "model";
        }
        return "other";
    }

    private static bool TryReadTomlString(string text, out string value)
    {
        value = string.Empty;
        if (text.Length < 2 || text[0] is not ('"' or '\'') || text[^1] != text[0]) return false;
        var content = text[1..^1];
        if (content.Contains(text[0]) || content.Contains('\\')) return false;
        foreach (var character in content)
            if (char.IsControl(character)) return false;
        value = content;
        return true;
    }

    private static string StripTomlComment(string line)
    {
        char quote = '\0';
        var escaped = false;
        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (quote == '"' && character == '\\') escaped = true;
                else if (character == quote) quote = '\0';
            }
            else if (character is '"' or '\'') quote = character;
            else if (character == '#') return line[..i];
        }
        return line;
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
