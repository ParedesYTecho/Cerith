#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService.Providers.AI.Antigravity;

internal sealed class AntigravityOwnerKey : IEquatable<AntigravityOwnerKey>
{
    public string Email { get; }
    public int ProcessId { get; }
    public string ProcessStartIdentity { get; }
    public string? SessionGeneration { get; }

    public AntigravityOwnerKey(string email, int processId, string processStartIdentity,
        string? sessionGeneration = null)
    {
        Email = email;
        ProcessId = processId;
        ProcessStartIdentity = processStartIdentity;
        SessionGeneration = sessionGeneration;
    }

    public bool Equals(AntigravityOwnerKey? other) => other is not null &&
        ProcessId == other.ProcessId &&
        StringComparer.Ordinal.Equals(ProcessStartIdentity, other.ProcessStartIdentity) &&
        StringComparer.Ordinal.Equals(SessionGeneration, other.SessionGeneration) &&
        StringComparer.Ordinal.Equals(Email, other.Email);

    public override bool Equals(object? obj) => obj is AntigravityOwnerKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(ProcessId,
        StringComparer.Ordinal.GetHashCode(ProcessStartIdentity),
        SessionGeneration is null ? 0 : StringComparer.Ordinal.GetHashCode(SessionGeneration),
        StringComparer.Ordinal.GetHashCode(Email));
}

internal sealed record AntigravityProcessOwner(int ProcessId, string ProcessStartIdentity, string CsrfToken,
    int[] ListenerPorts);

internal sealed record AntigravityQuotaAcquisition(AntigravityOwnerKey? Owner, AntigravityQuotaReading? Reading,
    MetricState FailureState, string? ErrorCode, TimeSpan? RetryAfter = null)
{
    public static AntigravityQuotaAcquisition Failure(string code, MetricState state,
        AntigravityOwnerKey? owner = null, TimeSpan? retryAfter = null) => new(owner, null, state, code, retryAfter);
}

internal enum AntigravityOwnerVerificationState
{
    Verified,
    Changed
}

internal sealed record AntigravityOwnerVerification(AntigravityOwnerVerificationState State,
    AntigravityOwnerKey? Owner);

internal sealed class AntigravityQuotaAcquirer : IDisposable
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    internal const int MaxResponseBytes = 1024 * 1024;
    private const int MaxProcessOutputBytes = 64 * 1024;
    private const int MaxListenerRows = 4096;
    private const int ProcessQueryTimeoutMilliseconds = 1800;
    private const string StatusPath = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
    private const string SummaryPath = "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";

    private static readonly byte[] RequestBody = Encoding.UTF8.GetBytes(
        "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"ideVersion\":\"unknown\",\"locale\":\"en\"}}");

    private static readonly string ProcessQuery = """
        $ErrorActionPreference='Stop'
        $currentSid=[System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        # Read CommandLine only after confirming the process belongs to this Windows user.
        $rows=@(Get-CimInstance -ClassName Win32_Process -Filter "Name LIKE 'language_server%.exe'" -Property Handle,ProcessId,CreationDate | ForEach-Object {
          $candidate=$_
          $owner=Invoke-CimMethod -InputObject $candidate -MethodName GetOwnerSid
          if ($owner.ReturnValue -ne 0 -or $owner.Sid -ne $currentSid) { return }
          $handle=[string]$candidate.Handle
          $started=[System.Management.ManagementDateTimeConverter]::ToDmtfDateTime([datetime]$candidate.CreationDate)
          $filter="Handle = '$handle' AND CreationDate = '$started'"
          $process=Get-CimInstance -ClassName Win32_Process -Filter $filter -Property Handle,ProcessId,CreationDate,CommandLine
          if ($null -eq $process -or [int]$process.ProcessId -ne [int]$candidate.ProcessId) { return }
          $line=[string]$process.CommandLine
          $isAntigravity=($line -match '(?i)(--app_data_dir(?:=|\s+)[^\"''\s]*antigravity|[/\\]antigravity(?:\.app)?[/\\]|--override_ide_name(?:=|\s+)[\"'']?antigravity\b)')
          if (-not $isAntigravity) { return }
          $match=[regex]::Match($line,'(?i)(?:^|\s)--csrf_token(?:=|\s+)[\"'']?([^\s\"'']+)')
          if (-not $match.Success) { return }
          [pscustomobject]@{ ProcessId=[int]$process.ProcessId; Started=$started; CsrfToken=$match.Groups[1].Value }
        })
        ConvertTo-Json -InputObject $rows -Compress
        """;

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly Func<CancellationToken, Task<AntigravityProcessOwner[]>> _ownerReader;
    private readonly bool _ownsHttpClient;
    private int _disposed;

    public AntigravityQuotaAcquirer()
        : this(CreateHttpClient(), TimeProvider.System, ReadLocalOwnersAsync, ownsHttpClient: true)
    {
    }

    internal AntigravityQuotaAcquirer(HttpClient httpClient, TimeProvider timeProvider,
        Func<CancellationToken, Task<AntigravityProcessOwner[]>> ownerReader)
        : this(httpClient, timeProvider, ownerReader, ownsHttpClient: false)
    {
    }

    private AntigravityQuotaAcquirer(HttpClient httpClient, TimeProvider timeProvider,
        Func<CancellationToken, Task<AntigravityProcessOwner[]>> ownerReader, bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _ownerReader = ownerReader ?? throw new ArgumentNullException(nameof(ownerReader));
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<AntigravityQuotaAcquisition> AcquireOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var deadline = new CancellationTokenSource(RequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        try
        {
            var candidates = await _ownerReader(linked.Token).ConfigureAwait(false);
            if (candidates.Length == 0)
                return AntigravityQuotaAcquisition.Failure("source_unavailable", MetricState.Unavailable);
            if (candidates.Length != 1)
                return AntigravityQuotaAcquisition.Failure("source_ambiguous", MetricState.Error);

            var owner = candidates[0];
            if (!IsValidOwner(owner))
                return AntigravityQuotaAcquisition.Failure("source_unavailable", MetricState.Unavailable);

            string? lastCode = null;
            var lastState = MetricState.Unavailable;
            foreach (var port in owner.ListenerPorts)
            {
                string email;
                try
                {
                    email = await ReadAccountEmailAsync(owner, port, linked.Token).ConfigureAwait(false);
                }
                catch (SourceException exception) when (exception.ErrorCode == "route_not_found")
                {
                    lastCode = exception.ErrorCode;
                    lastState = exception.State;
                    continue;
                }

                if (!IsValidEmail(email))
                    return AntigravityQuotaAcquisition.Failure("identity_unavailable", MetricState.Error);

                var key = CreateOwnerKey(email, owner);
                AntigravityQuotaReading? reading = null;
                string? failureCode = null;
                TimeSpan? retryAfter = null;
                var failureState = MetricState.Error;
                try
                {
                    var body = await SendJsonAsync(owner, port, SummaryPath, owner.CsrfToken, linked.Token)
                        .ConfigureAwait(false);
                    reading = AntigravityQuotaParser.Parse(body, _timeProvider.GetUtcNow());
                    if (reading.Points.All(point => point.State == MetricState.Error))
                        failureCode = reading.Points[0].ErrorCode ?? "schema_invalid";
                }
                catch (SourceException exception)
                {
                    failureCode = exception.ErrorCode;
                    retryAfter = exception.RetryAfter;
                    failureState = exception.State;
                }

                var current = await ReadSingleOwnerAsync(linked.Token).ConfigureAwait(false);
                if (current is null || !SameSource(owner, current) || !current.ListenerPorts.Contains(port))
                    return AntigravityQuotaAcquisition.Failure("identity_mismatch", MetricState.Error);

                var verifiedEmail = await ReadAccountEmailAsync(current, port, linked.Token).ConfigureAwait(false);
                var finalOwner = await ReadSingleOwnerAsync(linked.Token).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(email, verifiedEmail) || finalOwner is null ||
                    !SameSource(current, finalOwner) || !finalOwner.ListenerPorts.Contains(port))
                    return AntigravityQuotaAcquisition.Failure("identity_mismatch", MetricState.Error);

                if (failureCode is not null)
                    return AntigravityQuotaAcquisition.Failure(failureCode, failureState, key, retryAfter);
                return new AntigravityQuotaAcquisition(key, reading, MetricState.Error, null);
            }

            return AntigravityQuotaAcquisition.Failure(lastCode ?? "source_unavailable", lastState);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return AntigravityQuotaAcquisition.Failure("timeout", MetricState.Unavailable);
        }
        catch (SourceException exception)
        {
            return AntigravityQuotaAcquisition.Failure(exception.ErrorCode, exception.State,
                retryAfter: exception.RetryAfter);
        }
        catch (HttpRequestException)
        {
            return AntigravityQuotaAcquisition.Failure("offline", MetricState.Unavailable);
        }
        catch (IOException)
        {
            return AntigravityQuotaAcquisition.Failure("offline", MetricState.Unavailable);
        }
        catch (Exception)
        {
            return AntigravityQuotaAcquisition.Failure("source_unavailable", MetricState.Unavailable);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsHttpClient)
            _httpClient.Dispose();
    }

    public async Task<AntigravityOwnerVerification> VerifyOwnerAsync(AntigravityOwnerKey expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var deadline = new CancellationTokenSource(RequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var candidates = await _ownerReader(linked.Token).ConfigureAwait(false);
            if (candidates.Length != 1 || !IsValidOwner(candidates[0]))
                return new AntigravityOwnerVerification(AntigravityOwnerVerificationState.Changed, null);

            var current = candidates[0];
            var email = await ReadAccountEmailAsync(current, current.ListenerPorts[0], linked.Token)
                .ConfigureAwait(false);
            if (!IsValidEmail(email))
                return new AntigravityOwnerVerification(AntigravityOwnerVerificationState.Changed, null);

            var finalOwner = await ReadSingleOwnerAsync(linked.Token).ConfigureAwait(false);
            if (finalOwner is null || !SameSource(current, finalOwner) ||
                !finalOwner.ListenerPorts.Contains(current.ListenerPorts[0]))
                return new AntigravityOwnerVerification(AntigravityOwnerVerificationState.Changed, null);

            var owner = CreateOwnerKey(email, current);
            return new AntigravityOwnerVerification(expected.Equals(owner)
                ? AntigravityOwnerVerificationState.Verified
                : AntigravityOwnerVerificationState.Changed, owner);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new AntigravityOwnerVerification(AntigravityOwnerVerificationState.Changed, null);
        }
    }

    private async Task<string> ReadAccountEmailAsync(AntigravityProcessOwner owner, int port,
        CancellationToken cancellationToken)
    {
        var body = await SendJsonAsync(owner, port, StatusPath, owner.CsrfToken, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasSuccessfulCode(root) ||
                !root.TryGetProperty("userStatus", out var status) || status.ValueKind != JsonValueKind.Object ||
                !status.TryGetProperty("email", out var email) || email.ValueKind != JsonValueKind.String)
                throw new SourceException("identity_unavailable", MetricState.Error);
            return email.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            throw new SourceException("schema_invalid", MetricState.Error);
        }
    }

    private async Task<byte[]> SendJsonAsync(AntigravityProcessOwner expectedOwner, int port, string path,
        string csrfToken,
        CancellationToken cancellationToken)
    {
        // Recheck the listener owner immediately before sending its process-scoped CSRF token.
        var owners = await _ownerReader(cancellationToken).ConfigureAwait(false);
        if (owners.Length != 1 || !SameSource(expectedOwner, owners[0]) || !IsValidOwner(owners[0]) ||
            !owners[0].ListenerPorts.Contains(port))
            throw new SourceException("identity_mismatch", MetricState.Error);

        var uri = new UriBuilder(Uri.UriSchemeHttps, "127.0.0.1", port, path).Uri;
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(RequestBody)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        request.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrfToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new SourceException("offline", MetricState.Unavailable);
        }
        catch (IOException)
        {
            throw new SourceException("offline", MetricState.Unavailable);
        }

        using (response)
        {
            if (response.RequestMessage?.RequestUri is Uri actual && actual != uri)
                throw new SourceException("redirect_rejected", MetricState.Error);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SourceException(response.StatusCode == HttpStatusCode.Unauthorized ? "auth_required" : "auth_forbidden",
                    MetricState.Error);
            if ((int)response.StatusCode == 429)
                throw new SourceException("rate_limited", MetricState.Unavailable,
                    ReadRetryAfter(response, _timeProvider.GetUtcNow()));
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new SourceException("redirect_rejected", MetricState.Error);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new SourceException("route_not_found", MetricState.Error);
            if (!response.IsSuccessStatusCode)
                throw new SourceException((int)response.StatusCode >= 500 ? "upstream_unavailable" : "http_failure",
                    (int)response.StatusCode >= 500 ? MetricState.Unavailable : MetricState.Error);

            return await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > MaxResponseBytes)
            throw new SourceException("response_too_large", MetricState.Error);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > MaxResponseBytes)
                throw new SourceException("response_too_large", MetricState.Error);
            output.Write(buffer, 0, read);
        }
    }

    private static bool HasSuccessfulCode(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var code) || code.ValueKind == JsonValueKind.Null)
            return true;
        if (code.ValueKind == JsonValueKind.Number)
            return code.TryGetInt32(out var number) && number == 0;
        if (code.ValueKind != JsonValueKind.String)
            return false;
        var text = code.GetString();
        return StringComparer.OrdinalIgnoreCase.Equals(text, "OK") ||
            StringComparer.OrdinalIgnoreCase.Equals(text, "SUCCESS") ||
            StringComparer.Ordinal.Equals(text, "0");
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        try
        {
            var retry = response.Headers.RetryAfter;
            if (retry?.Delta is TimeSpan delta)
                return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            if (retry?.Date is DateTimeOffset date)
                return date <= now ? TimeSpan.Zero : date - now;
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool IsValidEmail(string email) => email.Length is > 0 and <= 320 &&
        StringComparer.Ordinal.Equals(email, email.Trim()) && !email.Any(char.IsControl);

    private static AntigravityOwnerKey CreateOwnerKey(string email, AntigravityProcessOwner owner)
    {
        var token = Encoding.UTF8.GetBytes(owner.CsrfToken);
        try
        {
            var generation = Convert.ToHexString(SHA256.HashData(token));
            return new AntigravityOwnerKey(email, owner.ProcessId, owner.ProcessStartIdentity, generation);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    private static bool IsValidOwner(AntigravityProcessOwner owner) => owner.ProcessId > 0 &&
        !string.IsNullOrWhiteSpace(owner.ProcessStartIdentity) && !string.IsNullOrWhiteSpace(owner.CsrfToken) &&
        owner.CsrfToken.Length <= 4096 && !owner.CsrfToken.Any(char.IsControl) &&
        owner.ListenerPorts.Length > 0 && owner.ListenerPorts.All(port => port is > 0 and <= 65535);

    private async Task<AntigravityProcessOwner?> ReadSingleOwnerAsync(CancellationToken cancellationToken)
    {
        var owners = await _ownerReader(cancellationToken).ConfigureAwait(false);
        return owners.Length == 1 ? owners[0] : null;
    }

    private static bool SameSource(AntigravityProcessOwner left, AntigravityProcessOwner right) =>
        left.ProcessId == right.ProcessId &&
        StringComparer.Ordinal.Equals(left.ProcessStartIdentity, right.ProcessStartIdentity) &&
        StringComparer.Ordinal.Equals(left.CsrfToken, right.CsrfToken);

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            Credentials = null,
            ServerCertificateCustomValidationCallback = static (request, certificate, _, _) =>
                request.RequestUri?.Scheme == Uri.UriSchemeHttps &&
                StringComparer.Ordinal.Equals(request.RequestUri.Host, "127.0.0.1") && certificate is not null
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async Task<AntigravityProcessOwner[]> ReadLocalOwnersAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            return [];

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = powershell,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(ProcessQuery);

        try
        {
            if (!process.Start())
                return [];
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(ProcessQueryTimeoutMilliseconds),
                TimeProvider.System);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var readOutput = ReadBoundedTextAsync(process.StandardOutput.BaseStream, MaxProcessOutputBytes, linked.Token);
            var drainError = ReadBoundedTextAsync(process.StandardError.BaseStream, 8192, linked.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var json = await readOutput.ConfigureAwait(false);
            await drainError.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                return [];

            var rows = ParseOwnerRows(Encoding.UTF8.GetBytes(json));
            if (rows.Length != 1)
                return rows;
            var ports = ReadLoopbackListenerPorts(rows[0].ProcessId);
            return [rows[0] with { ListenerPorts = ports }];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException)
            {
                if (!process.HasExited)
                    throw;
            }
            throw;
        }
        catch (Exception)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException)
            {
                if (!process.HasExited)
                    throw;
            }
            return [];
        }
    }

    private static async Task<string> ReadBoundedTextAsync(Stream stream, int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return Encoding.UTF8.GetString(output.ToArray());
            if (output.Length + read > maximumBytes)
                throw new IOException("Process output limit exceeded.");
            output.Write(buffer, 0, read);
        }
    }

    private static AntigravityProcessOwner[] ParseOwnerRows(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 16)
                return [];
            var owners = new List<AntigravityProcessOwner>();
            foreach (var row in root.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("ProcessId", out var pid) || !pid.TryGetInt32(out var processId) || processId <= 0 ||
                    !row.TryGetProperty("Started", out var started) || started.ValueKind != JsonValueKind.String ||
                    !row.TryGetProperty("CsrfToken", out var csrf) || csrf.ValueKind != JsonValueKind.String)
                    return [];
                owners.Add(new AntigravityProcessOwner(processId, started.GetString() ?? string.Empty,
                    csrf.GetString() ?? string.Empty, []));
            }
            return owners.ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static int[] ReadLoopbackListenerPorts(int processId)
    {
        const int addressFamilyIpv4 = 2;
        const int ownerPidListeners = 3;
        const int tcpStateListen = 2;
        const uint insufficientBuffer = 122;
        uint size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, true, addressFamilyIpv4, ownerPidListeners, 0);
        if (result != insufficientBuffer || size < sizeof(uint) || size > 1024 * 1024)
            return [];

        var table = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            result = GetExtendedTcpTable(table, ref size, true, addressFamilyIpv4, ownerPidListeners, 0);
            if (result != 0 || size < sizeof(uint))
                return [];

            var count = Marshal.ReadInt32(table);
            if (count is < 0 or > MaxListenerRows || sizeof(uint) + (long)count * 24 > size)
                return [];

            var ports = new SortedSet<int>();
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(table, sizeof(uint) + index * 24);
                var state = unchecked((uint)Marshal.ReadInt32(row));
                var address = unchecked((uint)Marshal.ReadInt32(row, 4));
                var networkPort = unchecked((ushort)Marshal.ReadInt32(row, 8));
                var pid = unchecked((uint)Marshal.ReadInt32(row, 20));
                var localAddress = new IPAddress(BitConverter.GetBytes(address));
                var port = unchecked((ushort)IPAddress.NetworkToHostOrder((short)networkPort));
                if (state == tcpStateListen && pid == processId && port > 0 &&
                    (localAddress.Equals(IPAddress.Loopback) || localAddress.Equals(IPAddress.Any)))
                    ports.Add(port);
            }
            return ports.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr tcpTable, ref uint outBufferLength, bool sort,
        int ipVersion, int tableClass, uint reserved);

    private sealed class SourceException(string errorCode, MetricState state, TimeSpan? retryAfter = null) : Exception
    {
        public string ErrorCode { get; } = errorCode;
        public MetricState State { get; } = state;
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}
