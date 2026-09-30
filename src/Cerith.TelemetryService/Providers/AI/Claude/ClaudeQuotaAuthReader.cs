#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Cerith.TelemetryService.Providers.AI.Claude;

internal enum ClaudeAuthStatus
{
    Ready,
    Unavailable,
    Unsupported,
    Error
}

internal sealed record ClaudeAuthIdentity(string AccessToken, long ExpiresAtUnixMilliseconds, string ConfigDirectory);

internal readonly record struct ClaudeAuthRead(ClaudeAuthStatus Status, ClaudeAuthIdentity? Identity, string? ErrorCode)
{
    public static ClaudeAuthRead Ready(ClaudeAuthIdentity identity) => new(ClaudeAuthStatus.Ready, identity, null);
    public static ClaudeAuthRead Unavailable() => new(ClaudeAuthStatus.Unavailable, null, null);
    public static ClaudeAuthRead Unsupported() => new(ClaudeAuthStatus.Unsupported, null, "route_unsupported");
    public static ClaudeAuthRead Error(string code) => new(ClaudeAuthStatus.Error, null, code);
}

internal sealed class ClaudeQuotaAuthReader
{
    internal const int MaxAuthFileBytes = 512 * 1024;
    private const int MaxSettingsFileBytes = 256 * 1024;

    private static readonly string[] RoutingEnvironmentNames =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS",
        "ANTHROPIC_API_KEY_HELPER", "ANTHROPIC_BEDROCK_BASE_URL", "ANTHROPIC_VERTEX_BASE_URL",
        "ANTHROPIC_FOUNDRY_BASE_URL", "ANTHROPIC_FOUNDRY_RESOURCE", "ANTHROPIC_FOUNDRY_API_KEY",
        "ANTHROPIC_VERTEX_PROJECT_ID", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN_FILE",
        "ANTHROPIC_PROFILE", "ANTHROPIC_FEDERATION_RULE_ID", "ANTHROPIC_ORGANIZATION_ID",
        "ANTHROPIC_SERVICE_ACCOUNT_ID", "ANTHROPIC_WORKSPACE_ID", "ANTHROPIC_IDENTITY_TOKEN",
        "ANTHROPIC_IDENTITY_TOKEN_FILE",
        "CLAUDE_CODE_API_KEY_HELPER", "CLAUDE_CODE_PROFILE", "CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST",
        "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY",
        "AWS_BEARER_TOKEN_BEDROCK", "CLOUD_ML_REGION"
    ];

    private readonly string? _configDirectory;
    private readonly Func<string, string?> _readEnvironment;
    private readonly TimeProvider _timeProvider;
    private readonly Func<bool> _managedPolicyPresent;

    public ClaudeQuotaAuthReader(string? configDirectory = null, Func<string, string?>? readEnvironment = null,
        TimeProvider? timeProvider = null, Func<bool>? managedPolicyPresent = null)
    {
        _configDirectory = configDirectory;
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _managedPolicyPresent = managedPolicyPresent ?? ManagedRegistryPolicyPresent;
    }

    public ClaudeAuthRead Read()
    {
        string configDirectory;
        try
        {
            configDirectory = ResolveConfigDirectory();
            if (HasRoutingOverride(configDirectory)) return ClaudeAuthRead.Unsupported();
        }
        catch (InvalidDataException) { return ClaudeAuthRead.Error("config_invalid"); }
        catch (Exception) { return ClaudeAuthRead.Error("config_unavailable"); }

        var path = Path.Combine(configDirectory, ".credentials.json");
        if (!File.Exists(path)) return ClaudeAuthRead.Unavailable();

        byte[]? bytes = null;
        try
        {
            bytes = ReadBounded(path, MaxAuthFileBytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object ||
                !TryReadBoundedString(oauth, "accessToken", 64 * 1024, out var accessToken) ||
                !IsSafeHeaderValue(accessToken) ||
                !oauth.TryGetProperty("expiresAt", out var expiresAt) || expiresAt.ValueKind != JsonValueKind.Number ||
                !expiresAt.TryGetInt64(out var expiresAtMilliseconds))
                return ClaudeAuthRead.Error("auth_file_invalid");

            if (expiresAtMilliseconds <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() + 30_000)
                return ClaudeAuthRead.Error(oauth.TryGetProperty("refreshToken", out var refresh) &&
                    refresh.ValueKind == JsonValueKind.String && !refresh.ValueEquals("")
                    ? "auth_refresh_required" : "auth_expired");

            _ = DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMilliseconds);

            return ClaudeAuthRead.Ready(new ClaudeAuthIdentity(accessToken, expiresAtMilliseconds, configDirectory));
        }
        catch (FileNotFoundException) { return ClaudeAuthRead.Unavailable(); }
        catch (InvalidDataException) { return ClaudeAuthRead.Error("auth_file_invalid"); }
        catch (JsonException) { return ClaudeAuthRead.Error("auth_file_invalid"); }
        catch (ArgumentOutOfRangeException) { return ClaudeAuthRead.Error("auth_file_invalid"); }
        catch (Exception) { return ClaudeAuthRead.Error("auth_unavailable"); }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private string ResolveConfigDirectory()
    {
        var configured = _configDirectory ?? _readEnvironment("CLAUDE_CONFIG_DIR");
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
        return Path.GetFullPath(path);
    }

    private bool HasRoutingOverride(string configDirectory)
    {
        if (_managedPolicyPresent()) return true;
        foreach (var name in RoutingEnvironmentNames)
        {
            if (_readEnvironment(name) is not null) return true;
        }

        var profileDirectory = _readEnvironment("ANTHROPIC_CONFIG_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Anthropic");
        if (File.Exists(Path.Combine(profileDirectory, "active_config")) ||
            File.Exists(Path.Combine(profileDirectory, "configs", "default.json"))) return true;

        var settingsPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(configDirectory, "settings.json"),
            Path.Combine(configDirectory, "settings.local.json"),
            Path.Combine(configDirectory, "managed-settings.json")
        };
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            settingsPaths.Add(Path.Combine(programFiles, "ClaudeCode", "managed-settings.json"));
            var policyParts = Path.Combine(programFiles, "ClaudeCode", "managed-settings.d");
            if (Directory.Exists(policyParts)) return true;
        }

        foreach (var settingsPath in settingsPaths)
        {
            if (File.Exists(settingsPath) && SettingsOverrideRoute(settingsPath)) return true;
        }
        return false;
    }

    private static bool ManagedRegistryPolicyPresent()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\ClaudeCode", writable: false);
        using var user = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Policies\ClaudeCode", writable: false);
        return machine?.GetValue("Settings", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null ||
            user?.GetValue("Settings", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null;
    }

    private static bool SettingsOverrideRoute(string path)
    {
        var bytes = ReadBounded(path, MaxSettingsFileBytes);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            if (root.TryGetProperty("apiKeyHelper", out var helper) && helper.ValueKind is not JsonValueKind.Null)
                return true;
            if (root.TryGetProperty("awsAuthRefresh", out var awsAuth) && awsAuth.ValueKind is not JsonValueKind.Null)
                return true;
            foreach (var name in new[] { "forceLoginGatewayUrl", "forceLoginOrgUUID", "policyHelper" })
            {
                if (root.TryGetProperty(name, out var policy) && policy.ValueKind != JsonValueKind.Null) return true;
            }
            if (root.TryGetProperty("forceLoginMethod", out var method) &&
                (method.ValueKind != JsonValueKind.String || method.GetString() != "claudeai")) return true;
            if (root.TryGetProperty("env", out var environment))
            {
                if (environment.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
                foreach (var property in environment.EnumerateObject())
                {
                    foreach (var name in RoutingEnvironmentNames)
                    {
                        if (StringComparer.OrdinalIgnoreCase.Equals(property.Name, name)) return true;
                    }
                }
            }
            return false;
        }
        catch (JsonException) { throw new InvalidDataException(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        if (stream.Length > maximumBytes) throw new InvalidDataException();
        var bytes = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new InvalidDataException();
            offset += read;
        }
        if (stream.ReadByte() != -1) throw new InvalidDataException();
        return bytes;
    }

    private static bool TryReadBoundedString(JsonElement element, string name, int maxLength, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        var candidate = property.GetString();
        if (string.IsNullOrEmpty(candidate) || candidate.Length > maxLength) return false;
        value = candidate;
        return true;
    }

    private static bool IsSafeHeaderValue(string value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character) || !char.IsAscii(character)) return false;
        }
        return true;
    }
}
