#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cerith.TelemetryService.Providers.AI.CommandCode;

internal enum CommandCodeAuthStatus
{
    Available,
    Missing,
    Invalid,
    AmbiguousHome
}

internal sealed class CommandCodeCredential
{
    internal CommandCodeCredential(string source, string apiKey, string? selectedAuthFileIdentity = null)
    {
        Source = source;
        ApiKey = apiKey;
        Fingerprint = FingerprintOf(apiKey);
        Identity = new OwnerIdentity(Source, Fingerprint, selectedAuthFileIdentity);
    }

    internal string Source { get; }
    internal string ApiKey { get; }
    internal string Fingerprint { get; }
    internal OwnerIdentity Identity { get; }

    internal sealed class OwnerIdentity
    {
        private readonly string _source;
        private readonly string _fingerprint;
        private readonly string? _selectedAuthFileIdentity;

        internal OwnerIdentity(string source, string fingerprint, string? selectedAuthFileIdentity)
        {
            _source = source;
            _fingerprint = fingerprint;
            _selectedAuthFileIdentity = selectedAuthFileIdentity;
        }

        internal bool Matches(OwnerIdentity other) =>
            string.Equals(_source, other._source, StringComparison.Ordinal) &&
            string.Equals(_fingerprint, other._fingerprint, StringComparison.Ordinal) &&
            string.Equals(_selectedAuthFileIdentity, other._selectedAuthFileIdentity, StringComparison.Ordinal);
    }

    private static string FingerprintOf(string apiKey)
    {
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        try
        {
            var hash = SHA256.HashData(bytes);
            try { return Convert.ToHexString(hash); }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal sealed class CommandCodeAuthRead
{
    private CommandCodeAuthRead(CommandCodeAuthStatus status, CommandCodeCredential? credential, string? errorCode)
    {
        Status = status;
        Credential = credential;
        ErrorCode = errorCode;
    }

    internal CommandCodeAuthStatus Status { get; }
    internal CommandCodeCredential? Credential { get; }
    internal string? ErrorCode { get; }

    internal static CommandCodeAuthRead Ready(CommandCodeCredential credential) =>
        new(CommandCodeAuthStatus.Available, credential, null);

    internal static CommandCodeAuthRead Missing() =>
        new(CommandCodeAuthStatus.Missing, null, "auth_unavailable");

    internal static CommandCodeAuthRead Invalid() =>
        new(CommandCodeAuthStatus.Invalid, null, "auth_invalid");

    internal static CommandCodeAuthRead AmbiguousHome() =>
        new(CommandCodeAuthStatus.AmbiguousHome, null, "auth_source_ambiguous");
}

internal sealed class CommandCodeQuotaAuthReader
{
    private const int MaxAuthFileBytes = 16 * 1024;
    private const string EnvironmentSource = "environment";
    private const string CliAuthFileSource = "cli-auth-file";
    private readonly Func<string?> _readEnvironmentKey;
    private readonly Func<string?> _readHome;
    private readonly Func<string?> _readUserProfile;
    private readonly Func<string, string?> _readAuthFile;

    internal CommandCodeQuotaAuthReader()
        : this(
            () => Environment.GetEnvironmentVariable("COMMAND_CODE_API_KEY"),
            () => Environment.GetEnvironmentVariable("HOME"),
            () => Environment.GetEnvironmentVariable("USERPROFILE"),
            ReadAuthFile)
    {
    }

    internal CommandCodeQuotaAuthReader(
        Func<string?> readEnvironmentKey,
        Func<string?> readHome,
        Func<string?> readUserProfile,
        Func<string, string?> readAuthFile)
    {
        _readEnvironmentKey = readEnvironmentKey ?? throw new ArgumentNullException(nameof(readEnvironmentKey));
        _readHome = readHome ?? throw new ArgumentNullException(nameof(readHome));
        _readUserProfile = readUserProfile ?? throw new ArgumentNullException(nameof(readUserProfile));
        _readAuthFile = readAuthFile ?? throw new ArgumentNullException(nameof(readAuthFile));
    }

    internal CommandCodeAuthRead Read()
    {
        string? environmentKey;
        try { environmentKey = _readEnvironmentKey(); }
        catch { return CommandCodeAuthRead.Invalid(); }

        if (!string.IsNullOrEmpty(environmentKey))
        {
            if (string.IsNullOrWhiteSpace(environmentKey) || environmentKey.Length > MaxAuthFileBytes)
                return CommandCodeAuthRead.Invalid();
            return CommandCodeAuthRead.Ready(new CommandCodeCredential(EnvironmentSource, environmentKey));
        }

        string? home;
        string? userProfile;
        try
        {
            home = NormalizeHome(_readHome());
            userProfile = NormalizeHome(_readUserProfile());
        }
        catch { return CommandCodeAuthRead.Invalid(); }

        if (home is not null && userProfile is not null &&
            !string.Equals(home, userProfile, StringComparison.OrdinalIgnoreCase))
            return CommandCodeAuthRead.AmbiguousHome();

        var selectedHome = home ?? userProfile;
        if (selectedHome is null) return CommandCodeAuthRead.Missing();

        string authFilePath;
        string? contents;
        try
        {
            authFilePath = Path.GetFullPath(Path.Combine(selectedHome, ".commandcode", "auth.json"));
            contents = _readAuthFile(authFilePath);
        }
        catch (FileNotFoundException) { return CommandCodeAuthRead.Missing(); }
        catch (DirectoryNotFoundException) { return CommandCodeAuthRead.Missing(); }
        catch { return CommandCodeAuthRead.Invalid(); }

        if (contents is null) return CommandCodeAuthRead.Missing();
        if (contents.Length > MaxAuthFileBytes) return CommandCodeAuthRead.Invalid();

        try
        {
            using var document = JsonDocument.Parse(contents, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("apiKey", out var apiKeyElement) ||
                apiKeyElement.ValueKind != JsonValueKind.String)
                return CommandCodeAuthRead.Invalid();

            var apiKey = apiKeyElement.GetString();
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > MaxAuthFileBytes)
                return CommandCodeAuthRead.Invalid();

            return CommandCodeAuthRead.Ready(new CommandCodeCredential(
                CliAuthFileSource, apiKey, AuthFileIdentityOf(authFilePath)));
        }
        catch (JsonException) { return CommandCodeAuthRead.Invalid(); }
        catch { return CommandCodeAuthRead.Invalid(); }
    }

    private static string? NormalizeHome(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var home = value.Trim();
        if (!Path.IsPathFullyQualified(home)) throw new ArgumentException("Home path must be absolute.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
    }

    private static string AuthFileIdentityOf(string path)
    {
        var absolutePath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(absolutePath)) throw new ArgumentException("Auth file path must be absolute.");
        var bytes = Encoding.UTF8.GetBytes(absolutePath.ToUpperInvariant());
        try
        {
            var hash = SHA256.HashData(bytes);
            try { return Convert.ToHexString(hash); }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string? ReadAuthFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxAuthFileBytes) throw new InvalidDataException();

        var bytes = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }

        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
