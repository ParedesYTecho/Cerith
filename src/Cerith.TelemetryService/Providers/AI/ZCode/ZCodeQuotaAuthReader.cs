#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cerith.TelemetryService;

internal enum ZCodeAuthStatus
{
    Ready,
    Unavailable,
    Unsupported,
    Error
}

internal sealed record ZCodeQuotaAuthIdentity(string ApiKey, string Family, string CredentialScope,
    string? ProductId, string? OrganizationId, string? ProjectId, string StableId)
{
    public override string ToString() =>
        $"ZCodeQuotaAuthIdentity {{ Family = {Family}, ApiKey = <redacted>, CredentialScope = <redacted>, ProductId = <redacted>, OrganizationId = <redacted>, ProjectId = <redacted>, StableId = <redacted> }}";
}

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
                if (File.Exists(configPath)) return ReadConfiguredProvider(configPath, selection);

                var credentialsPath = Path.Combine(appDirectory, "credentials.json");
                return File.Exists(credentialsPath)
                    ? ZCodeAuthRead.Unsupported("account_selection_unproven")
                    : ZCodeAuthRead.Unavailable();
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

        var legacyPresent = false;
        string? legacyKind = null;
        string? productId = null;
        string? organizationId = null;
        string? projectId = null;
        if (root.TryGetProperty("modelProviderFamilySelectedKeys", out var legacySelections))
        {
            if (legacySelections.ValueKind != JsonValueKind.Object)
                return Selection.Failed(ZCodeAuthRead.Error("provider_selection_invalid"));
            if (legacySelections.TryGetProperty(family, out var legacyValue))
            {
                legacyPresent = true;
                if (legacyValue.ValueKind != JsonValueKind.String)
                    return Selection.Failed(ZCodeAuthRead.Error("provider_selection_invalid"));
                var raw = legacyValue.GetString()?.Trim() ?? string.Empty;
                var providerKey = $"builtin:{family}-coding-plan";
                if (StringComparer.Ordinal.Equals(raw, providerKey)) legacyKind = "individual";
                else if (raw.StartsWith($"team-plan:{providerKey}:", StringComparison.Ordinal))
                {
                    if (!TryReadLegacyTeamSelection(raw, family, out productId, out organizationId, out projectId))
                        return Selection.Failed(ZCodeAuthRead.Unsupported("team_selection_invalid"));
                    legacyKind = "team";
                }
            }
        }

        var hasCurrentMap = root.TryGetProperty("providerFamilyConnectionSelections", out var currentSelections);
        if (hasCurrentMap && currentSelections.ValueKind != JsonValueKind.Object)
            return Selection.Failed(ZCodeAuthRead.Error("provider_selection_invalid"));
        var currentSelection = default(JsonElement);
        var hasCurrentSelection = hasCurrentMap && currentSelections.TryGetProperty(family, out currentSelection);
        if (hasCurrentSelection)
        {
            if (currentSelection.ValueKind != JsonValueKind.Object || !TryGetString(currentSelection, "kind", 64, out var kind))
                return Selection.Failed(ZCodeAuthRead.Error("provider_selection_invalid"));
            var currentKind = kind switch
            {
                "individual-coding-plan" => "individual",
                "team-coding-plan" => "team",
                _ => null
            };
            if (currentKind is null)
                return Selection.Failed(ZCodeAuthRead.Unsupported(legacyPresent ? "provider_selection_ambiguous" : "coding_plan_not_selected"));
            if (legacyPresent && (!StringComparer.Ordinal.Equals(legacyKind, currentKind) || currentKind == "team"))
                return Selection.Failed(ZCodeAuthRead.Unsupported("provider_selection_ambiguous"));
            return new Selection(ZCodeAuthStatus.Ready, family, currentKind, null, null, null, default);
        }

        if (hasCurrentMap && legacyPresent)
            return Selection.Failed(ZCodeAuthRead.Unsupported("provider_selection_ambiguous"));
        if (legacyPresent && legacyKind is not null)
            return new Selection(ZCodeAuthStatus.Ready, family, legacyKind, productId, organizationId, projectId, default);
        return Selection.Failed(ZCodeAuthRead.Unsupported("coding_plan_not_selected"));
    }

    private static ZCodeAuthRead ReadConfiguredProvider(string path, Selection selection)
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
            if (selection.PlanKind == "team" &&
                (selection.ProductId is null || selection.OrganizationId is null || selection.ProjectId is null))
                return ZCodeAuthRead.Unsupported("team_context_missing");

            const string credentialScope = "configured-api-key";
            var stableId = CreateCredentialStableId(apiKey, selection.Family!, credentialScope,
                selection.ProductId, selection.OrganizationId, selection.ProjectId);
            return ZCodeAuthRead.Ready(new ZCodeQuotaAuthIdentity(apiKey, selection.Family!, credentialScope,
                selection.ProductId, selection.OrganizationId, selection.ProjectId, stableId));
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

    private static bool TryReadLegacyTeamSelection(string raw, string family, out string? product,
        out string? organization, out string? project)
    {
        product = organization = project = null;
        var prefix = $"team-plan:builtin:{family}-coding-plan:";
        if (!raw.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = raw[prefix.Length..].Split(':');
        if (parts.Length != 3) return false;
        var decoded = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var value = DecodeSegment(parts[i]);
            if (value is null || !IsSafeHeaderValue(value)) return false;
            decoded[i] = value;
        }
        product = decoded[0];
        organization = decoded[1];
        project = decoded[2];
        return true;
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

    private static string CreateCredentialStableId(string apiKey, string family, string credentialScope,
        string? product, string? organization, string? project)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendIdentityPart(hash, family);
        AppendIdentityPart(hash, credentialScope);
        AppendIdentityPart(hash, product ?? string.Empty);
        AppendIdentityPart(hash, organization ?? string.Empty);
        AppendIdentityPart(hash, project ?? string.Empty);
        AppendIdentityPart(hash, apiKey);
        return $"zcode:{family}:{Convert.ToHexString(hash.GetHashAndReset())}";
    }

    private static void AppendIdentityPart(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { hash.AppendData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        hash.AppendData(new byte[] { 0 });
    }

    private readonly record struct Selection(ZCodeAuthStatus Status, string? Family, string? PlanKind,
        string? ProductId, string? OrganizationId, string? ProjectId, ZCodeAuthRead Read)
    {
        public string? ProviderKey => Family is null ? null : $"builtin:{Family}-coding-plan";
        public static Selection Failed(ZCodeAuthRead read) => new(read.Status, null, null, null, null, null, read);
    }
}
