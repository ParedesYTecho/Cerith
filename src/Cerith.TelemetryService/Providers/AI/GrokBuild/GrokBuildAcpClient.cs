#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Cerith.TelemetryService.Providers.AI.GrokBuild;

internal sealed record GrokBuildAuthIdentity(
    string MethodId,
    string? Email,
    string? PrincipalType,
    string? PrincipalId,
    string? TeamId,
    string? OrganizationId);

internal sealed record GrokBuildAuthObservation(
    GrokBuildAuthIdentity? Identity,
    string? AccessToken,
    string? ClientVersion,
    string? ErrorCode)
{
    public static GrokBuildAuthObservation Unavailable(string code) => new(null, null, null, code);
}

internal sealed class GrokBuildAcpClient : IDisposable
{
    private const string AuthInfoMethod = "_x.ai/auth/info";
    private const string BearerMethod = "_x.ai/auth/getBearerToken";
    private const int MaximumMessageCharacters = 1024 * 1024;
    private const int MaximumTokenCharacters = 16 * 1024;
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProcessStartTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private string? _clientVersion;
    private string? _leaderSocketPath;
    private long _nextRequestId;
    private bool _initialized;
    private bool _disposed;

    public async Task<GrokBuildAuthObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ObservationTimeout);
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                await EnsureStartedAsync(deadline.Token).ConfigureAwait(false);
                var before = await ReadAuthInfoAsync(deadline.Token).ConfigureAwait(false);
                if (before is null)
                    return GrokBuildAuthObservation.Unavailable("auth_unavailable");
                if (!IsSessionMethod(before.MethodId))
                    return GrokBuildAuthObservation.Unavailable("auth_unsupported");

                var bearer = await SendRequestAsync(BearerMethod, new { }, deadline.Token).ConfigureAwait(false);
                if (!bearer.TryGetProperty("token", out var tokenElement) ||
                    tokenElement.ValueKind != JsonValueKind.String ||
                    tokenElement.GetString() is not { Length: > 0 and <= MaximumTokenCharacters } token)
                {
                    return GrokBuildAuthObservation.Unavailable("auth_unavailable");
                }

                var after = await ReadAuthInfoAsync(deadline.Token).ConfigureAwait(false);
                if (after is null || !before.SameMethodAndPrincipal(after))
                    return GrokBuildAuthObservation.Unavailable("auth_changed");

                if (!HasSessionOwnerEvidence(after))
                    return GrokBuildAuthObservation.Unavailable("auth_owner_unavailable");

                return new GrokBuildAuthObservation(
                    new GrokBuildAuthIdentity(after.MethodId!, after.Email, after.PrincipalType,
                        after.PrincipalId, after.TeamId, after.OrganizationId),
                    token,
                    _clientVersion,
                    null);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            StopProcess();
            return GrokBuildAuthObservation.Unavailable("auth_timeout");
        }
        catch (Exception)
        {
            StopProcess();
            return GrokBuildAuthObservation.Unavailable("auth_unavailable");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopProcess();
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _initialized)
            return;

        StopProcess();
        _clientVersion = await ReadClientVersionAsync(cancellationToken).ConfigureAwait(false);
        if (_clientVersion is null)
            throw new InvalidOperationException("Grok CLI version unavailable.");

        _leaderSocketPath = Path.Combine(Path.GetTempPath(), $"cerith-grok-{Guid.NewGuid():N}.sock");
        var startInfo = new ProcessStartInfo
        {
            FileName = "grok",
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("agent");
        startInfo.ArgumentList.Add("stdio");
        startInfo.ArgumentList.Add("--leader-socket");
        startInfo.ArgumentList.Add(_leaderSocketPath);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.ErrorDataReceived += static (_, _) => { };
        if (!process.Start())
            throw new InvalidOperationException("Grok CLI process did not start.");
        process.BeginErrorReadLine();
        _process = process;

        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(ProcessStartTimeout);
        var initialize = await SendRequestAsync("initialize", new
        {
            protocolVersion = 1,
            clientInfo = new { name = "cerith-telemetry", version = "1.0" },
            clientCapabilities = new { }
        }, startup.Token).ConfigureAwait(false);
        if (initialize.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid ACP initialize response.");

        await WriteNotificationAsync("initialized", new { }, startup.Token).ConfigureAwait(false);
        _initialized = true;
    }

    private async Task<string?> ReadClientVersionAsync(CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "grok",
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--version");
        process.ErrorDataReceived += static (_, _) => { };
        if (!process.Start())
            return null;
        process.BeginErrorReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(VersionTimeout);
        string? output;
        try
        {
            output = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
                process.Close();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                process.Close();
            }
            throw;
        }
        if (process.ExitCode != 0)
            return null;

        var match = Regex.Match(output ?? string.Empty, @"\Agrok\s+([0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?)",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private async Task<GrokBuildAuthInfo?> ReadAuthInfoAsync(CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync(AuthInfoMethod, new { }, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object)
            return null;

        return new GrokBuildAuthInfo(ReadString(result, "methodId"), ReadString(result, "email"),
            ReadString(result, "principalType"), ReadString(result, "principalId"),
            ReadString(result, "teamId"), ReadString(result, "organizationId"));
    }

    private async Task<JsonElement> SendRequestAsync(string method, object parameters,
        CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("Grok CLI process unavailable.");
        if (process.HasExited)
            throw new IOException("Grok CLI process exited.");

        var id = Interlocked.Increment(ref _nextRequestId);
        var message = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        });
        await process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null || line.Length > MaximumMessageCharacters)
                throw new InvalidDataException("Invalid ACP message.");

            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("id", out var responseId) ||
                responseId.ValueKind != JsonValueKind.Number ||
                !responseId.TryGetInt64(out var responseNumber) ||
                responseNumber != id)
            {
                continue;
            }

            if (root.TryGetProperty("error", out _))
                throw new InvalidDataException("ACP request failed.");
            if (!root.TryGetProperty("result", out var result))
                throw new InvalidDataException("ACP response is missing its result.");
            return result.Clone();
        }
    }

    private async Task WriteNotificationAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("Grok CLI process unavailable.");
        var message = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });
        await process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSessionMethod(string? methodId) =>
        methodId is "cached_token" or "grok.com" or "oidc";

    private static bool HasSessionOwnerEvidence(GrokBuildAuthInfo info)
    {
        if (StringComparer.Ordinal.Equals(info.PrincipalType, "Team"))
            return IsBoundedOwnerValue(info.PrincipalId);

        return IsBoundedOwnerValue(info.Email);
    }

    private static bool IsBoundedOwnerValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
            return false;
        foreach (var character in value)
            if (char.IsControl(character))
                return false;
        return true;
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        _initialized = false;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1000);
                }
            }
            catch (InvalidOperationException)
            {
                process.Close();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                process.Close();
            }
            finally
            {
                process.Dispose();
            }
        }

        var socketPath = _leaderSocketPath;
        _leaderSocketPath = null;
        if (socketPath is not null)
        {
            try
            {
                if (File.Exists(socketPath))
                    File.Delete(socketPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record GrokBuildAuthInfo(
        string? MethodId,
        string? Email,
        string? PrincipalType,
        string? PrincipalId,
        string? TeamId,
        string? OrganizationId)
    {
        public bool SameMethodAndPrincipal(GrokBuildAuthInfo other) =>
            StringComparer.Ordinal.Equals(MethodId, other.MethodId) &&
            StringComparer.Ordinal.Equals(Email, other.Email) &&
            StringComparer.Ordinal.Equals(PrincipalType, other.PrincipalType) &&
            StringComparer.Ordinal.Equals(PrincipalId, other.PrincipalId) &&
            StringComparer.Ordinal.Equals(TeamId, other.TeamId) &&
            StringComparer.Ordinal.Equals(OrganizationId, other.OrganizationId);
    }
}
