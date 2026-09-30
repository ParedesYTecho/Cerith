#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cerith.TelemetryService;

internal enum CodexAuthStatus
{
    Ready,
    Unavailable,
    Unsupported,
    Error
}

internal sealed record CodexAuthIdentity(string AccessToken, string AccountId, string UserId, string StableId);

internal readonly record struct CodexAuthRead(CodexAuthStatus Status, CodexAuthIdentity? Identity,
    string? ErrorCode)
{
    public static CodexAuthRead Ready(CodexAuthIdentity identity) => new(CodexAuthStatus.Ready, identity, null);
    public static CodexAuthRead Unavailable() => new(CodexAuthStatus.Unavailable, null, null);
    public static CodexAuthRead Unsupported(string code) => new(CodexAuthStatus.Unsupported, null, code);
    public static CodexAuthRead Error(string code) => new(CodexAuthStatus.Error, null, code);
}

internal sealed class CodexQuotaAuthReader
{
    internal const int MaxFileBytes = 1024 * 1024;
    private readonly string? _codexHome;
    private readonly Func<byte[]> _loadIdentityKey;

    public CodexQuotaAuthReader(string? codexHome = null, Func<byte[]>? loadIdentityKey = null)
    {
        _codexHome = codexHome;
        _loadIdentityKey = loadIdentityKey ?? LoadOrCreateProtectedIdentityKey;
    }

    public CodexAuthRead Read()
    {
        string home;
        try { home = ResolveCodexHome(); }
        catch (Exception) { return CodexAuthRead.Error("auth_path_unavailable"); }

        try
        {
            if (HasManagedRouting(home) || HasNonstandardRouting(home))
                return CodexAuthRead.Unsupported("route_unsupported");
        }
        catch (InvalidDataException) { return CodexAuthRead.Error("config_invalid"); }
        catch (Exception) { return CodexAuthRead.Error("config_unavailable"); }

        var authPath = Path.Combine(home, "auth.json");
        if (!File.Exists(authPath)) return CodexAuthRead.Unavailable();

        try
        {
            var bytes = ReadBounded(authPath);
            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("auth_mode", out var mode) || mode.ValueKind != JsonValueKind.String ||
                    !StringComparer.Ordinal.Equals(mode.GetString(), "chatgpt"))
                    return CodexAuthRead.Unsupported("auth_mode_unsupported");

                if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object ||
                    !TryGetBoundedString(tokens, "access_token", 64 * 1024, out var accessToken) ||
                    !TryGetBoundedString(tokens, "account_id", 1024, out var accountId) ||
                    !TryGetBoundedString(tokens, "id_token", 64 * 1024, out var idToken) ||
                    !IsSafeHeaderValue(accessToken) || !IsSafeHeaderValue(accountId))
                    return CodexAuthRead.Error("auth_file_invalid");

                if (!TryReadIdentityClaims(idToken, out var subject, out var userId, out var claimAccountId) ||
                    !StringComparer.Ordinal.Equals(accountId, claimAccountId))
                    return CodexAuthRead.Error("auth_identity_invalid");

                var key = _loadIdentityKey();
                try
                {
                    if (key.Length != 32) return CodexAuthRead.Error("identity_key_invalid");
                    var stableId = ComputeStableId(key, subject, userId, accountId);
                    return CodexAuthRead.Ready(new CodexAuthIdentity(accessToken, accountId, userId, stableId));
                }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (FileNotFoundException) { return CodexAuthRead.Unavailable(); }
        catch (InvalidDataException) { return CodexAuthRead.Error("auth_file_invalid"); }
        catch (JsonException) { return CodexAuthRead.Error("auth_file_invalid"); }
        catch (Exception) { return CodexAuthRead.Error("auth_unavailable"); }
    }

    private string ResolveCodexHome()
    {
        if (!string.IsNullOrWhiteSpace(_codexHome)) return Path.GetFullPath(_codexHome);
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        var home = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : configured;
        return Path.GetFullPath(home);
    }

    private static bool HasManagedRouting(string home)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var systemCodex = Path.Combine(programData, "OpenAI", "Codex");
        return File.Exists(Path.Combine(home, "managed_config.toml")) ||
            File.Exists(Path.Combine(systemCodex, "requirements.toml")) ||
            File.Exists(Path.Combine(systemCodex, "config.toml"));
    }

    private static bool HasNonstandardRouting(string home)
    {
        if (IsSet("OPENAI_BASE_URL") || IsSet("CHATGPT_BASE_URL")) return true;

        var configPath = Path.Combine(home, "config.toml");
        if (!File.Exists(configPath)) return false;
        var config = new UTF8Encoding(false, true).GetString(ReadBounded(configPath));
        if (config.Contains("\"\"\"", StringComparison.Ordinal) ||
            config.Contains("'''", StringComparison.Ordinal)) return true;
        foreach (var rawLine in config.Split('\n'))
        {
            var line = StripTomlComment(rawLine).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                var arrayTable = line.StartsWith("[[", StringComparison.Ordinal);
                if (arrayTable ? !line.EndsWith("]]", StringComparison.Ordinal) : !line.EndsWith(']')) return true;
                var path = arrayTable ? line[2..^2] : line[1..^1];
                if (!TryParseTomlKeyPath(path, out var components) || components.Exists(IsRoutingKey)) return true;
                continue;
            }

            var equals = FindTomlAssignment(line);
            if (equals < 0 || !TryParseTomlKeyPath(line[..equals], out var keys)) return true;
            if (keys.Exists(IsRoutingKey)) return true;
            var value = line[(equals + 1)..].Trim();
            if (!IsBalancedTomlValue(value)) return true;
            var key = keys[^1];
            if (key.Equals("cli_auth_credentials_store", StringComparison.OrdinalIgnoreCase) &&
                !IsFileCredentialStore(value)) return true;
        }
        return false;
    }

    private static bool IsRoutingKey(string key) =>
        key.Equals("openai_base_url", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("chatgpt_base_url", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("model_provider", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("model_providers", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("profile", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("profiles", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("forced_chatgpt_workspace_id", StringComparison.OrdinalIgnoreCase);

    private static int FindTomlAssignment(string line)
    {
        var quote = '\0';
        var escaped = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (escaped) { escaped = false; continue; }
            if (quote == '"' && c == '\\') { escaped = true; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '=') return i;
        }
        return -1;
    }

    private static bool TryParseTomlKeyPath(string text, out List<string> components)
    {
        components = [];
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index >= text.Length) return false;

            string component;
            if (text[index] == '"')
            {
                if (!TryReadBasicTomlString(text, ref index, out component)) return false;
            }
            else if (text[index] == '\'')
            {
                var end = text.IndexOf('\'', index + 1);
                if (end < 0) return false;
                component = text[(index + 1)..end];
                index = end + 1;
            }
            else
            {
                var start = index;
                while (index < text.Length && IsBareTomlKeyCharacter(text[index])) index++;
                if (index == start) return false;
                component = text[start..index];
            }

            components.Add(component);
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index == text.Length) return components.Count > 0;
            if (text[index++] != '.') return false;
        }
        return components.Count > 0;
    }

    private static bool TryReadBasicTomlString(string text, ref int index, out string value)
    {
        var builder = new StringBuilder();
        index++;
        while (index < text.Length)
        {
            var c = text[index++];
            if (c == '"') { value = builder.ToString(); return true; }
            if (c != '\\')
            {
                if (char.IsControl(c)) break;
                builder.Append(c);
                continue;
            }

            if (index >= text.Length) break;
            var escape = text[index++];
            switch (escape)
            {
                case 'b': builder.Append('\b'); break;
                case 't': builder.Append('\t'); break;
                case 'n': builder.Append('\n'); break;
                case 'f': builder.Append('\f'); break;
                case 'r': builder.Append('\r'); break;
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case 'u':
                case 'U':
                    var digits = escape == 'u' ? 4 : 8;
                    if (index + digits > text.Length ||
                        !int.TryParse(text.AsSpan(index, digits), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out var scalar) ||
                        !Rune.TryCreate(scalar, out var rune))
                    {
                        value = string.Empty;
                        return false;
                    }
                    builder.Append(rune.ToString());
                    index += digits;
                    break;
                default:
                    value = string.Empty;
                    return false;
            }
        }
        value = string.Empty;
        return false;
    }

    private static bool IsBareTomlKeyCharacter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-';

    private static bool IsFileCredentialStore(string value)
    {
        if (value.StartsWith('"'))
        {
            var index = 0;
            return TryReadBasicTomlString(value, ref index, out var parsed) && index == value.Length &&
                parsed.Equals("file", StringComparison.OrdinalIgnoreCase);
        }

        return value.Length >= 2 && value[0] == '\'' && value[^1] == '\'' &&
            value.IndexOf('\'', 1) == value.Length - 1 &&
            value[1..^1].Equals("file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBalancedTomlValue(string value)
    {
        if (value.Length == 0) return false;
        var containers = new Stack<char>();
        var quote = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quote != '\0')
            {
                if (quote == '"' && c == '\\')
                {
                    if (++i >= value.Length) return false;
                    var escape = value[i];
                    if (escape is 'u' or 'U')
                    {
                        var digits = escape == 'u' ? 4 : 8;
                        if (i + digits >= value.Length) return false;
                        for (var j = i + 1; j <= i + digits; j++)
                            if (!char.IsAsciiHexDigit(value[j])) return false;
                        i += digits;
                    }
                    else if (escape is not ('b' or 't' or 'n' or 'f' or 'r' or '"' or '\\')) return false;
                    continue;
                }
                if (c == quote) quote = '\0';
                continue;
            }

            if (c is '"' or '\'') { quote = c; continue; }
            if (c is '[' or '{') containers.Push(c);
            else if (c is ']' or '}')
            {
                if (containers.Count == 0 || containers.Pop() != (c == ']' ? '[' : '{')) return false;
            }
        }
        return quote == '\0' && containers.Count == 0;
    }

    private static bool IsSet(string name) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    private static string StripTomlComment(string line)
    {
        var quote = '\0';
        var escaped = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (escaped) { escaped = false; continue; }
            if (quote == '"' && c == '\\') { escaped = true; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '#') return line[..i];
        }
        return line;
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
        foreach (var c in value)
            if (c < 0x21 || c > 0x7e) return false;
        return true;
    }

    private static bool TryReadIdentityClaims(string token, out string subject, out string userId,
        out string accountId)
    {
        subject = userId = accountId = string.Empty;
        var segments = token.Split('.');
        if (segments.Length != 3 || segments[1].Length is 0 or > 48 * 1024) return false;
        byte[]? bytes = null;
        try
        {
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            bytes = Convert.FromBase64String(payload);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!root.TryGetProperty("sub", out var sub) || sub.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(sub.GetString()) ||
                !root.TryGetProperty("https://api.openai.com/auth", out var auth) || auth.ValueKind != JsonValueKind.Object ||
                !TryGetBoundedString(auth, "chatgpt_user_id", 1024, out userId) ||
                !TryGetBoundedString(auth, "chatgpt_account_id", 1024, out accountId)) return false;
            subject = sub.GetString()!;
            return subject.Length <= 4096;
        }
        catch (Exception) { return false; }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string ComputeStableId(byte[] key, string subject, string userId, string accountId)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        AppendPart(hmac, "codex");
        AppendPart(hmac, subject);
        AppendPart(hmac, userId);
        AppendPart(hmac, accountId);
        var digest = hmac.GetHashAndReset();
        try { return Convert.ToHexString(digest); }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    private static void AppendPart(IncrementalHash hmac, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hmac.AppendData(length);
        hmac.AppendData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
    }

    private static byte[] LoadOrCreateProtectedIdentityKey()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) throw new IOException();
        var directory = Path.Combine(local, "Cerith");
        var path = Path.Combine(directory, "quota-identity-key.dpapi");
        if (File.Exists(path)) return ReadIdentityKey(path);

        Directory.CreateDirectory(directory);
        var key = RandomNumberGenerator.GetBytes(32);
        byte[] protectedKey;
        try { protectedKey = Protect(key); }
        finally { CryptographicOperations.ZeroMemory(key); }
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(protectedKey);
        }
        catch (IOException) when (File.Exists(path)) { }
        finally { CryptographicOperations.ZeroMemory(protectedKey); }
        return ReadIdentityKey(path);
    }

    private static byte[] ReadIdentityKey(string path)
    {
        var protectedKey = ReadBounded(path);
        try { return Unprotect(protectedKey); }
        finally { CryptographicOperations.ZeroMemory(protectedKey); }
    }

    private static byte[] Protect(byte[] bytes) => TransformDpapi(bytes, protect: true);
    private static byte[] Unprotect(byte[] bytes) => TransformDpapi(bytes, protect: false);

    private static byte[] TransformDpapi(byte[] bytes, bool protect)
    {
        var input = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, input, bytes.Length);
        var inputBlob = new DataBlob { Length = bytes.Length, Data = input };
        DataBlob outputBlob;
        try
        {
            var success = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                if (outputBlob.Length is <= 0 or > 4096) throw new CryptographicException();
                var output = new byte[outputBlob.Length];
                Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                return output;
            }
            finally
            {
                if (outputBlob.Data != IntPtr.Zero && outputBlob.Length is > 0 and <= 4096)
                    Marshal.Copy(new byte[outputBlob.Length], 0, outputBlob.Data, outputBlob.Length);
                _ = LocalFree(outputBlob.Data);
            }
        }
        finally
        {
            if (bytes.Length > 0) Marshal.Copy(new byte[bytes.Length], 0, input, bytes.Length);
            Marshal.FreeHGlobal(input);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
