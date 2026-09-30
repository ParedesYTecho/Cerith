#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Cerith.TelemetryService;

internal enum CursorAuthStatus
{
    Ready,
    Unavailable,
    Error
}

internal sealed record CursorAuthIdentity(string AccessToken, string AccountId, string CookieValue)
{
    public override string ToString() => nameof(CursorAuthIdentity);
}

internal readonly record struct CursorAuthRead(CursorAuthStatus Status, CursorAuthIdentity? Identity,
    string? ErrorCode)
{
    public static CursorAuthRead Ready(CursorAuthIdentity identity) => new(CursorAuthStatus.Ready, identity, null);
    public static CursorAuthRead Unavailable() => new(CursorAuthStatus.Unavailable, null, null);
    public static CursorAuthRead Error(string code) => new(CursorAuthStatus.Error, null, code);
}

internal sealed class CursorQuotaAuthReader
{
    internal const int MaxConfigBytes = 256 * 1024;
    internal const int MaxAccessTokenBytes = 64 * 1024;
    private const int SqliteOpenReadOnly = 0x00000001;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private readonly string? _appData;
    private readonly string? _profile;

    public CursorQuotaAuthReader(string? appData = null, string? profile = null)
    {
        _appData = appData;
        _profile = profile;
    }

    public CursorAuthRead Read()
    {
        if (!OperatingSystem.IsWindows()) return CursorAuthRead.Error("platform_unsupported");

        try
        {
            var appData = _appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var profile = _profile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(appData) || string.IsNullOrWhiteSpace(profile))
                return CursorAuthRead.Error("auth_path_unavailable");

            var databasePath = Path.Combine(appData, "Cursor", "User", "globalStorage", "state.vscdb");
            var configPath = Path.Combine(profile, ".cursor", "cli-config.json");
            if (!File.Exists(databasePath)) return CursorAuthRead.Unavailable();

            var accessToken = ReadAccessToken(databasePath);
            if (accessToken is null) return CursorAuthRead.Unavailable();
            if (!TryReadTokenSubject(accessToken, out var tokenSubject) ||
                tokenSubject.Length == 0)
                return CursorAuthRead.Error("auth_invalid");

            var configuredSubject = ReadConfiguredSubject(configPath);
            if (configuredSubject is not null &&
                !StringComparer.Ordinal.Equals(configuredSubject, tokenSubject))
                return CursorAuthRead.Error("auth_identity_mismatch");

            var cookie = $"WorkosCursorSessionToken={tokenSubject}%3A%3A{accessToken}";
            return CursorAuthRead.Ready(new CursorAuthIdentity(accessToken, tokenSubject, cookie));
        }
        catch (InvalidDataException) { return CursorAuthRead.Error("auth_invalid"); }
        catch (JsonException) { return CursorAuthRead.Error("config_invalid"); }
        catch (DllNotFoundException) { return CursorAuthRead.Error("sqlite_unavailable"); }
        catch (EntryPointNotFoundException) { return CursorAuthRead.Error("sqlite_unavailable"); }
        catch (Exception) { return CursorAuthRead.Error("auth_unavailable"); }
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maximumBytes) throw new InvalidDataException();
        var buffer = new byte[checked((int)stream.Length)];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static string? ReadConfiguredSubject(string path)
    {
        byte[]? configBytes = null;
        try
        {
            if (!File.Exists(path)) return null;
            configBytes = ReadBounded(path, MaxConfigBytes);
            using var config = JsonDocument.Parse(configBytes, new JsonDocumentOptions { MaxDepth = 16 });
            if (config.RootElement.ValueKind != JsonValueKind.Object ||
                !config.RootElement.TryGetProperty("authInfo", out var authInfo) ||
                authInfo.ValueKind != JsonValueKind.Object ||
                !authInfo.TryGetProperty("authId", out var authId) ||
                authId.ValueKind != JsonValueKind.String)
                return null;
            return NormalizeSubject(authId.GetString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
        finally
        {
            if (configBytes is not null) Array.Clear(configBytes);
        }
    }

    private static string? ReadAccessToken(string path)
    {
        var result = SqliteOpen(path, out var database, SqliteOpenReadOnly, IntPtr.Zero);
        if (result != 0 || database == IntPtr.Zero)
        {
            if (database != IntPtr.Zero) _ = SqliteClose(database);
            throw new InvalidDataException();
        }

        try
        {
            _ = SqliteBusyTimeout(database, 1000);
            const string sql = "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken' LIMIT 1;";
            result = SqlitePrepare(database, sql, -1, out var statement, IntPtr.Zero);
            if (result != 0 || statement == IntPtr.Zero) throw new InvalidDataException();
            try
            {
                result = SqliteStep(statement);
                if (result == SqliteDone) return null;
                if (result != SqliteRow || SqliteColumnType(statement, 0) != 3)
                    throw new InvalidDataException();

                var length = SqliteColumnBytes(statement, 0);
                if (length is <= 0 or > MaxAccessTokenBytes) throw new InvalidDataException();
                var pointer = SqliteColumnText(statement, 0);
                if (pointer == IntPtr.Zero) throw new InvalidDataException();
                var value = Marshal.PtrToStringUTF8(pointer, length);
                if (value is null || value.Length > MaxAccessTokenBytes || !IsSafeToken(value))
                    throw new InvalidDataException();
                return value;
            }
            finally { _ = SqliteFinalize(statement); }
        }
        finally { _ = SqliteClose(database); }
    }

    private static bool TryReadTokenSubject(string token, out string subject)
    {
        subject = string.Empty;
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[1].Length is 0 or > 32 * 1024) return false;
        try
        {
            var segment = parts[1].Replace('-', '+').Replace('_', '/');
            segment = segment.PadRight((segment.Length + 3) / 4 * 4, '=');
            using var payload = JsonDocument.Parse(Convert.FromBase64String(segment),
                new JsonDocumentOptions { MaxDepth = 16 });
            if (payload.RootElement.ValueKind != JsonValueKind.Object ||
                !payload.RootElement.TryGetProperty("sub", out var claim) || claim.ValueKind != JsonValueKind.String)
                return false;
            var normalized = NormalizeSubject(claim.GetString());
            if (normalized is null) return false;
            subject = normalized;
            return true;
        }
        catch (FormatException) { return false; }
        catch (JsonException) { return false; }
    }

    private static string? NormalizeSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256) return null;
        var separator = subject.IndexOf('|');
        if (separator >= 0)
        {
            if (separator != subject.LastIndexOf('|')) return null;
            var provider = subject[..separator];
            var id = subject[(separator + 1)..];
            if (id.Length == 0 || !IsSubjectPart(id)) return null;
            if (id.StartsWith("user_", StringComparison.Ordinal) &&
                provider.Length > 0 && IsNativeSubjectPart(id))
                return id;
            if (provider is not ("auth0" or "google-oauth2" or "github" or "oidc")) return null;
        }
        else
        {
            return null;
        }

        return subject;
    }

    private static bool IsSubjectPart(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.' or '@')) return false;
        }
        return true;
    }

    private static bool IsNativeSubjectPart(string value)
    {
        if (!value.StartsWith("user_", StringComparison.Ordinal) || value.Length == "user_".Length) return false;
        for (var index = "user_".Length; index < value.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(value[index]) && value[index] != '_') return false;
        }
        return true;
    }

    private static bool IsSafeToken(string token)
    {
        if (token.Length < 10) return false;
        foreach (var character in token)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')) return false;
        }
        return true;
    }

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string fileName, out IntPtr database,
        int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqlitePrepare(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        int byteCount, out IntPtr statement, IntPtr tail);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteStep(IntPtr statement);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_type", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteColumnType(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteColumnBytes(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SqliteColumnText(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteFinalize(IntPtr statement);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteClose(IntPtr database);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteBusyTimeout(IntPtr database, int milliseconds);
}
