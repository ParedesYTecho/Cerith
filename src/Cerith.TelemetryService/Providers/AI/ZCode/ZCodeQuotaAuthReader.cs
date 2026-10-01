#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cerith.TelemetryService;

internal enum ZCodeAuthStatus
{
    Ready,
    Unavailable,
    Unsupported,
    Error
}

internal sealed record ZCodeQuotaAuthIdentity(string ApiKey, string Family, string AccountKey,
    string? OrganizationId, string? ProjectId, string StableId);

internal readonly record struct ZCodeAuthRead(ZCodeAuthStatus Status, ZCodeQuotaAuthIdentity? Identity,
    string? ErrorCode)
{
    public static ZCodeAuthRead Ready(ZCodeQuotaAuthIdentity identity) => new(ZCodeAuthStatus.Ready, identity, null);
    public static ZCodeAuthRead Unavailable() => new(ZCodeAuthStatus.Unavailable, null, null);
    public static ZCodeAuthRead Unsupported(string code) => new(ZCodeAuthStatus.Unsupported, null, code);
    public static ZCodeAuthRead Error(string code) => new(ZCodeAuthStatus.Error, null, code);
}

internal sealed class ZCodeQuotaAuthReader
{
    internal const int MaxFileBytes = 1024 * 1024;
    private static readonly Regex IndividualCredentialName = new(
        @"^account-provider:coding-plan:account:(?<family>zai|bigmodel)-individual-coding-plan:account:(?<account>[^:]+):api-key$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TeamCredentialName = new(
        @"^account-provider:team:account:(?<family>zai|bigmodel)-team-coding-plan:(?<product>[^:]+):(?<organization>[^:]+):(?<project>[^:]+):account:(?<account>[^:]+):api-key$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string? _userProfile;

    public ZCodeQuotaAuthReader(string? userProfile = null) => _userProfile = userProfile;

    public ZCodeAuthRead Read()
    {
        string profile;
        try
        {
            profile = Path.GetFullPath(_userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (string.IsNullOrWhiteSpace(profile)) return ZCodeAuthRead.Error("auth_path_unavailable");
        }
        catch (Exception) { return ZCodeAuthRead.Error("auth_path_unavailable"); }

        var appDirectory = Path.Combine(profile, ".zcode", "v2");
        var settingPath = Path.Combine(appDirectory, "setting.json");
        if (!File.Exists(settingPath)) return ZCodeAuthRead.Unavailable();

        try
        {
            var settingsBytes = ReadBounded(settingPath);
            try
            {
                using var settingsDocument = JsonDocument.Parse(settingsBytes, new JsonDocumentOptions { MaxDepth = 16 });
                var selection = ReadSelection(settingsDocument.RootElement);
                if (selection.Status != ZCodeAuthStatus.Ready || selection.Family is null || selection.PlanKind is null)
                    return selection.Read;

                var configPath = Path.Combine(appDirectory, "config.json");
                if (File.Exists(configPath))
                    return ReadConfiguredProvider(configPath, selection, profile);

                var credentialsPath = Path.Combine(appDirectory, "credentials.json");
                if (!File.Exists(credentialsPath)) return ZCodeAuthRead.Unavailable();
                return ReadSelectedCredential(credentialsPath, selection, profile);
            }
            finally { CryptographicOperations.ZeroMemory(settingsBytes); }
        }
        catch (FileNotFoundException) { return ZCodeAuthRead.Unavailable(); }
        catch (InvalidDataException) { return ZCodeAuthRead.Error("auth_file_invalid"); }
        catch (JsonException) { return ZCodeAuthRead.Error("auth_file_invalid"); }
        catch (Exception) { return ZCodeAuthRead.Error("auth_unavailable"); }
    }

    private static Selection ReadSelection(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetString(root, "providerFamilyDomain", 32, out var family) ||
            family is not ("zai" or "bigmodel"))
            return Selection.Failed(ZCodeAuthRead.Unsupported("provider_selection_missing"));

        string? selectedPlan = null;
        string? planKind = null;
        string? organizationId = null;
        string? projectId = null;
        if (root.TryGetProperty("modelProviderFamilySelectedKeys", out var selected) &&
            selected.ValueKind == JsonValueKind.Object && selected.TryGetProperty(family, out var selectedValue) &&
            selectedValue.ValueKind == JsonValueKind.String)
        {
            var raw = selectedValue.GetString()?.Trim() ?? string.Empty;
            var planPrefix = $"builtin:{family}-coding-plan";
            if (StringComparer.Ordinal.Equals(raw, planPrefix))
            {
                selectedPlan = planPrefix;
                planKind = "coding";
            }
            else if (raw.StartsWith($"team-plan:{planPrefix}:", StringComparison.Ordinal))
            {
                if (!TryReadLegacyTeamSelection(raw, family, out organizationId, out projectId))
                    return Selection.Failed(ZCodeAuthRead.Unsupported("team_selection_invalid"));
                selectedPlan = planPrefix;
                planKind = "team";
            }
        }

        if (root.TryGetProperty("providerFamilyConnectionSelections", out var connections) &&
            connections.ValueKind == JsonValueKind.Object && connections.TryGetProperty(family, out var connection) &&
            connection.ValueKind == JsonValueKind.Object && TryGetString(connection, "kind", 64, out var kind))
        {
            var connectionPlanKind = kind switch
            {
                "individual-coding-plan" => "coding",
                "team-coding-plan" => "team",
                _ => null
            };
            if (connectionPlanKind is not null)
            {
                if (planKind is not null && !StringComparer.Ordinal.Equals(planKind, connectionPlanKind))
                    return Selection.Failed(ZCodeAuthRead.Unsupported("provider_selection_ambiguous"));
                selectedPlan = $"builtin:{family}-coding-plan";
                planKind = connectionPlanKind;
            }
        }

        return planKind is null || selectedPlan is null
            ? Selection.Failed(ZCodeAuthRead.Unsupported("coding_plan_not_selected"))
            : new Selection(ZCodeAuthStatus.Ready, family, planKind, organizationId, projectId, default);
    }

    private static ZCodeAuthRead ReadConfiguredProvider(string path, Selection selection, string profile)
    {
        var bytes = ReadBounded(path);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("provider", out var providers) ||
                providers.ValueKind != JsonValueKind.Object || !providers.TryGetProperty(selection.ProviderKey!, out var provider) ||
                provider.ValueKind != JsonValueKind.Object)
                return ZCodeAuthRead.Unavailable();
            if (provider.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
                return ZCodeAuthRead.Unsupported("provider_disabled");
            if (!provider.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Object)
                return ZCodeAuthRead.Error("auth_file_invalid");
            if (options.TryGetProperty("baseURL", out var configuredBaseUrl) &&
                (configuredBaseUrl.ValueKind != JsonValueKind.String ||
                    !IsSupportedBaseUrl(configuredBaseUrl.GetString(), selection.Family!)))
                return ZCodeAuthRead.Unsupported("route_unsupported");
            if (!TryGetString(options, "apiKey", 64 * 1024, out var apiKey) || !IsSafeHeaderValue(apiKey))
                return ZCodeAuthRead.Error("auth_file_invalid");
            if (selection.PlanKind == "team" && (selection.OrganizationId is null || selection.ProjectId is null))
                return ZCodeAuthRead.Unsupported("team_context_missing");

            var accountKey = "provider-config";
            var stableId = CreateCredentialStableId(apiKey, selection.Family!, accountKey,
                selection.OrganizationId, selection.ProjectId);
            return ZCodeAuthRead.Ready(new ZCodeQuotaAuthIdentity(apiKey, selection.Family!, accountKey,
                selection.OrganizationId, selection.ProjectId, stableId));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static ZCodeAuthRead ReadSelectedCredential(string path, Selection selection, string profile)
    {
        var bytes = ReadBounded(path);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ZCodeAuthRead.Error("auth_file_invalid");

            var candidates = new List<(string Name, JsonElement Value, string Account, string? Organization, string? Project)>();
            foreach (var property in root.EnumerateObject())
            {
                var match = (selection.PlanKind == "team" ? TeamCredentialName : IndividualCredentialName).Match(property.Name);
                if (!match.Success || !StringComparer.Ordinal.Equals(match.Groups["family"].Value, selection.Family)) continue;
                var organization = selection.PlanKind == "team" ? DecodeSegment(match.Groups["organization"].Value) : null;
                var project = selection.PlanKind == "team" ? DecodeSegment(match.Groups["project"].Value) : null;
                var product = selection.PlanKind == "team" ? DecodeSegment(match.Groups["product"].Value) : null;
                if (selection.PlanKind == "team" && (organization is null || project is null || product is null))
                    continue;
                if (selection.OrganizationId is not null && !StringComparer.Ordinal.Equals(organization, selection.OrganizationId)) continue;
                if (selection.ProjectId is not null && !StringComparer.Ordinal.Equals(project, selection.ProjectId)) continue;
                candidates.Add((property.Name, property.Value, match.Groups["account"].Value, organization, project));
            }

            if (candidates.Count == 0) return ZCodeAuthRead.Unavailable();
            if (candidates.Count != 1) return ZCodeAuthRead.Unsupported("auth_selection_ambiguous");
            var candidate = candidates[0];
            if (candidate.Value.ValueKind != JsonValueKind.String)
                return ZCodeAuthRead.Error("auth_file_invalid");
            var encodedSecret = candidate.Value.GetString();
            if (string.IsNullOrWhiteSpace(encodedSecret) || encodedSecret.Length > 96 * 1024)
                return ZCodeAuthRead.Error("auth_file_invalid");
            if (!TryDecryptCredential(encodedSecret, profile, out var apiKey) || !IsSafeHeaderValue(apiKey))
                return ZCodeAuthRead.Error("auth_decryption_failed");

            var stableId = $"zcode:{selection.Family}:{candidate.Name}";
            return ZCodeAuthRead.Ready(new ZCodeQuotaAuthIdentity(apiKey, selection.Family!, candidate.Account,
                candidate.Organization, candidate.Project, stableId));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool IsSupportedBaseUrl(string? value, string family)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = value.Trim().TrimEnd('/');
        var expected = family switch
        {
            "zai" => "https://api.z.ai/api/coding/paas/v4",
            "bigmodel" => "https://open.bigmodel.cn/api/coding/paas/v4",
            _ => string.Empty
        };
        return expected.Length > 0 && StringComparer.OrdinalIgnoreCase.Equals(candidate, expected);
    }

    private static bool TryReadLegacyTeamSelection(string raw, string family, out string? organization, out string? project)
    {
        organization = project = null;
        var prefix = $"team-plan:builtin:{family}-coding-plan:";
        if (!raw.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = raw[prefix.Length..].Split(':');
        if (parts.Length != 3) return false;
        var decoded = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var value = DecodeSegment(parts[i]);
            if (value is null) return false;
            decoded[i] = value;
        }
        if (!IsSafeHeaderValue(decoded[1]) || !IsSafeHeaderValue(decoded[2])) return false;
        organization = decoded[1];
        project = decoded[2];
        return true;
    }

    private static bool TryDecryptCredential(string encoded, string profile, out string apiKey)
    {
        apiKey = string.Empty;
        if (encoded.StartsWith("enc:", StringComparison.Ordinal) && !encoded.StartsWith("enc:v1:", StringComparison.Ordinal))
            return false;
        if (!encoded.StartsWith("enc:v1:", StringComparison.Ordinal))
        {
            apiKey = encoded.Trim();
            return apiKey.Length > 0 && apiKey.Length <= 64 * 1024;
        }

        var parts = encoded[7..].Split('.');
        if (parts.Length != 3 || !TryBase64Url(parts[0], out var nonce) || !TryBase64Url(parts[1], out var tag) ||
            !TryBase64Url(parts[2], out var ciphertext)) return false;
        if (nonce.Length != 12 || tag.Length != 16 || ciphertext.Length is 0 or > 64 * 1024) return false;

        var username = Environment.UserName;
        var material = Encoding.UTF8.GetBytes($"zcode-credential-fallback:win32:{profile}:{username}");
        var key = SHA256.HashData(material);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            apiKey = new UTF8Encoding(false, true).GetString(plaintext).Trim();
            return apiKey.Length > 0 && apiKey.Length <= 64 * 1024;
        }
        catch (CryptographicException) { return false; }
        catch (DecoderFallbackException) { return false; }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static bool TryBase64Url(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Length is 0 or > 128 * 1024) return false;
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
        try { bytes = Convert.FromBase64String(normalized); return true; }
        catch (FormatException) { return false; }
    }

    private static string? DecodeSegment(string value)
    {
        try
        {
            var decoded = Uri.UnescapeDataString(value).Trim();
            return decoded.Length > 0 && decoded.Length <= 1024 ? decoded : null;
        }
        catch (UriFormatException) { return null; }
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

    private static bool TryGetString(JsonElement parent, string name, int maximumLength, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > maximumLength) return false;
        value = candidate.Trim();
        return true;
    }

    private static bool IsSafeHeaderValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var character in value)
            if (character < 0x21 || character > 0x7e) return false;
        return true;
    }

    private static string CreateCredentialStableId(string apiKey, string family, string accountKey,
        string? organization, string? project)
    {
        var keyBytes = Encoding.UTF8.GetBytes(apiKey);
        try
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(keyBytes));
            return $"zcode:{family}:{accountKey}:{organization}:{project}:{fingerprint}";
        }
        finally { CryptographicOperations.ZeroMemory(keyBytes); }
    }

    private readonly record struct Selection(ZCodeAuthStatus Status, string? Family, string? PlanKind,
        string? OrganizationId, string? ProjectId, ZCodeAuthRead Read)
    {
        public string? ProviderKey => Family is null ? null : $"builtin:{Family}-coding-plan";
        public static Selection Failed(ZCodeAuthRead read) => new(read.Status, null, null, null, null, read);
    }
}
