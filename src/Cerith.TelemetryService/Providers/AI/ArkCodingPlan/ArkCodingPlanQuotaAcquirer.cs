#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cerith.TelemetryService;

internal enum ArkCodingPlanCliCommand
{
    Identity,
    UsagePlan
}

internal sealed class ArkCodingPlanQuotaAcquirer : IDisposable
{
    internal static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(2);
    private static readonly string[] RemovedEnvironmentNames =
    [
        "ARK_API_KEY", "ARK_BASE_URL", "ARK_PROFILE", "ARK_ENV",
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
        "http_proxy", "https_proxy", "all_proxy", "no_proxy"
    ];

    private readonly string? _cliPath;
    private readonly string _homeDirectory;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<ProcessStartInfo, int, TimeSpan, CancellationToken, Task<byte[]>> _runProcess;
    private readonly byte[] _fingerprintKey;
    private bool _disposed;

    public ArkCodingPlanQuotaAcquirer()
        : this(ArkCodingPlanCliLocator.Resolve(), GetHomeDirectory(), Environment.GetEnvironmentVariable,
            RunProcessAsync, RandomNumberGenerator.GetBytes(32))
    {
    }

    internal ArkCodingPlanQuotaAcquirer(string? cliPath, string homeDirectory,
        Func<string, string?> getEnvironmentVariable,
        Func<ProcessStartInfo, int, TimeSpan, CancellationToken, Task<byte[]>> runProcess,
        byte[] fingerprintKey)
    {
        _cliPath = cliPath;
        _homeDirectory = Path.GetFullPath(homeDirectory ?? throw new ArgumentNullException(nameof(homeDirectory)));
        _getEnvironmentVariable = getEnvironmentVariable ?? throw new ArgumentNullException(nameof(getEnvironmentVariable));
        _runProcess = runProcess ?? throw new ArgumentNullException(nameof(runProcess));
        ArgumentNullException.ThrowIfNull(fingerprintKey);
        if (fingerprintKey.Length < 32) throw new ArgumentException("Fingerprint key must be at least 32 bytes.", nameof(fingerprintKey));
        _fingerprintKey = fingerprintKey;
    }

    internal async Task<ArkCodingPlanQuotaReading> AcquireAsync(DateTimeOffset observedAt,
        Action<string> observeOwner, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(observeOwner);
        var profile = ResolveProfile();
        EnsureCliAvailable();

        byte[]? beforePayload = null;
        byte[]? planPayload = null;
        byte[]? afterPayload = null;
        try
        {
            beforePayload = await RunCommandAsync(ArkCodingPlanCliCommand.Identity, profile, cancellationToken)
                .ConfigureAwait(false);
            var beforeIdentity = ArkCodingPlanQuotaParser.ParseIdentity(beforePayload, profile, _fingerprintKey);
            observeOwner(beforeIdentity.Fingerprint);

            planPayload = await RunCommandAsync(ArkCodingPlanCliCommand.UsagePlan, profile, cancellationToken)
                .ConfigureAwait(false);

            ArkCodingPlanQuotaReading? reading = null;
            ArkCodingPlanAcquisitionException? parseFailure = null;
            try
            {
                reading = ArkCodingPlanQuotaParser.Parse(planPayload, observedAt, profile, beforeIdentity);
            }
            catch (ArkCodingPlanAcquisitionException exception)
            {
                parseFailure = exception;
            }

            afterPayload = await RunCommandAsync(ArkCodingPlanCliCommand.Identity, profile, cancellationToken)
                .ConfigureAwait(false);
            var afterIdentity = ArkCodingPlanQuotaParser.ParseIdentity(afterPayload, profile, _fingerprintKey);
            observeOwner(afterIdentity.Fingerprint);
            if (!beforeIdentity.SamePrincipal(afterIdentity))
                throw new ArkCodingPlanAcquisitionException("identity_changed");
            if (parseFailure is not null) throw parseFailure;
            return reading!;
        }
        finally
        {
            Zero(beforePayload);
            Zero(planPayload);
            Zero(afterPayload);
        }
    }

    internal async Task ObserveIdentityAsync(Action<string> observeOwner, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(observeOwner);
        var profile = ResolveProfile();
        EnsureCliAvailable();

        byte[]? payload = null;
        try
        {
            payload = await RunCommandAsync(ArkCodingPlanCliCommand.Identity, profile, cancellationToken)
                .ConfigureAwait(false);
            var identity = ArkCodingPlanQuotaParser.ParseIdentity(payload, profile, _fingerprintKey);
            observeOwner(identity.Fingerprint);
        }
        finally
        {
            Zero(payload);
        }
    }

    internal string ResolveProfile()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var configured = _getEnvironmentVariable("ARK_PROFILE");
        if (!string.IsNullOrWhiteSpace(configured))
            return ValidateProfile(configured);

        var configPath = Path.Combine(_homeDirectory, ".arkcli", "config.yaml");
        var profile = ReadDefaultProfile(configPath);
        if (profile is null) throw new ArkCodingPlanAcquisitionException("profile_unresolved");
        return ValidateProfile(profile);
    }

    internal static string? ReadDefaultProfile(string configPath)
    {
        try
        {
            using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > 64 * 1024) return null;
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            string? found = null;
            var foundKey = false;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length > 4096) return null;
                if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#') continue;
                var separator = line.IndexOf(':');
                if (separator < 0 || !StringComparer.Ordinal.Equals(line[..separator], "default_profile")) continue;
                if (foundKey) return null;
                foundKey = true;
                found = ParseYamlScalar(line[(separator + 1)..]);
                if (found is null) return null;
            }
            return found;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           DecoderFallbackException or ArgumentException)
        {
            return null;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(ArkCodingPlanCliCommand command, string profile,
        string cliPath, IReadOnlyDictionary<string, string?>? syntheticEnvironment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(cliPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (syntheticEnvironment is not null)
        {
            foreach (var pair in syntheticEnvironment)
            {
                if (pair.Value is null) startInfo.Environment.Remove(pair.Key);
                else startInfo.Environment[pair.Key] = pair.Value;
            }
        }
        SanitizeEnvironment(startInfo.Environment);

        switch (command)
        {
            case ArkCodingPlanCliCommand.Identity:
                startInfo.ArgumentList.Add("auth");
                startInfo.ArgumentList.Add("whoami");
                break;
            case ArkCodingPlanCliCommand.UsagePlan:
                startInfo.ArgumentList.Add("usage");
                startInfo.ArgumentList.Add("plan");
                startInfo.ArgumentList.Add("--product");
                startInfo.ArgumentList.Add("coding-plan");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }

        startInfo.ArgumentList.Add("--profile");
        startInfo.ArgumentList.Add(profile);
        startInfo.ArgumentList.Add("--env");
        startInfo.ArgumentList.Add("prod");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("json");
        return startInfo;
    }

    private async Task<byte[]> RunCommandAsync(ArkCodingPlanCliCommand command, string profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var maximumOutputBytes = command == ArkCodingPlanCliCommand.Identity
            ? 16 * 1024
            : ArkCodingPlanQuotaParser.MaximumPayloadBytes;
        var startInfo = CreateStartInfo(command, profile, _cliPath!, syntheticEnvironment: null);
        return await _runProcess(startInfo, maximumOutputBytes, ProcessTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private void EnsureCliAvailable()
    {
        if (_cliPath is null || !Path.IsPathFullyQualified(_cliPath) || !File.Exists(_cliPath))
            throw new ArkCodingPlanAcquisitionException("cli_unavailable");
    }

    private static string ValidateProfile(string profile)
    {
        var value = profile.Trim();
        if (value.Length is 0 or > 128 || value[0] == '-' ||
            value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) ||
                                   character is '/' or '\\' or '\0'))
            throw new ArkCodingPlanAcquisitionException("profile_unresolved");
        return value;
    }

    private static string? ParseYamlScalar(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value[0] == '#' || value is "null" or "~") return null;

        if (value[0] is '\'' or '"')
        {
            var quote = value[0];
            var end = value.IndexOf(quote, 1);
            if (end <= 1) return null;
            var trailing = value[(end + 1)..].Trim();
            if (trailing.Length > 0 && trailing[0] != '#') return null;
            return value[1..end];
        }

        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0) value = value[..comment].TrimEnd();
        return value;
    }

    private static void SanitizeEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.ToArray())
        {
            var upper = name.ToUpperInvariant();
            if (RemovedEnvironmentNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                upper.StartsWith("VOLC_INIT_", StringComparison.Ordinal) ||
                upper.StartsWith("VOLCENGINE_", StringComparison.Ordinal) ||
                upper.StartsWith("VOLCSTACK_", StringComparison.Ordinal))
                environment.Remove(name);
        }
    }

    private static async Task<byte[]> RunProcessAsync(ProcessStartInfo startInfo, int maximumOutputBytes,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new ArkCodingPlanAcquisitionException("cli_unavailable");
            process.StandardInput.Close();
        }
        catch (ArkCodingPlanAcquisitionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or
                                           System.ComponentModel.Win32Exception)
        {
            throw new ArkCodingPlanAcquisitionException("cli_unavailable");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        Task<byte[]> stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, maximumOutputBytes, deadline.Token);
        Task stderrTask = DrainAsync(process.StandardError.BaseStream, deadline.Token);
        Task exitTask = process.WaitForExitAsync(deadline.Token);
        try
        {
            await exitTask.ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TryKill(process);
            Exception? cleanupFailure = null;
            var cleanup = Task.WhenAll(process.WaitForExitAsync(), Task.WhenAll(stdoutTask, stderrTask));
            try
            {
                await cleanup.WaitAsync(ProcessCleanupTimeout).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                cleanupFailure = failure;
                if (!cleanup.IsCompleted) ObserveFault(cleanup);
            }

            if (stdoutTask.IsCompletedSuccessfully) Zero(stdoutTask.Result);
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (deadline.IsCancellationRequested) throw new ArkCodingPlanAcquisitionException("process_timeout");
            if (exception is ArkCodingPlanAcquisitionException acquisitionException) throw acquisitionException;
            if (IsExpectedProcessFailure(exception))
                throw new ArkCodingPlanAcquisitionException("process_failed");
            if (cleanupFailure is not null && !IsExpectedProcessFailure(cleanupFailure))
                throw new AggregateException(exception, cleanupFailure);
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw new InvalidOperationException("Unreachable process failure path.");
        }

        var output = await stdoutTask.ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new ArkCodingPlanAcquisitionException("process_failed");
            return output;
        }
        catch
        {
            Zero(output);
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 4096));
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) return output.ToArray();
                if (output.Length + count > maximumBytes)
                    throw new ArkCodingPlanAcquisitionException("response_too_large");
                output.Write(buffer, 0, count);
            }
        }
        finally
        {
            Zero(buffer);
            if (output.TryGetBuffer(out var segment) && segment.Array is not null)
                CryptographicOperations.ZeroMemory(segment.Array.AsSpan(segment.Offset, segment.Count));
        }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        try
        {
            while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0) { }
        }
        finally
        {
            Zero(buffer);
        }
    }

    private static bool IsExpectedProcessFailure(Exception exception) => exception is IOException or
        UnauthorizedAccessException or System.ComponentModel.Win32Exception or
        ObjectDisposedException or OperationCanceledException or TimeoutException;

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string GetHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home)) return home;
        home = Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrWhiteSpace(home) ? AppContext.BaseDirectory : home;
    }

    private static void Zero(byte[]? value)
    {
        if (value is not null) CryptographicOperations.ZeroMemory(value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_fingerprintKey);
    }
}

internal static class ArkCodingPlanCliLocator
{
    private const int MaximumMetadataBytes = 128 * 1024;
    private const string ExpectedPackageName = "@volcengine/ark-cli";
    private const string ExpectedRepository = "https://github.com/volcengine/ark-cli.git";

    public static string? Resolve()
    {
        var roots = EnumeratePackageRoots();
        foreach (var root in roots)
        {
            var path = TryResolvePackage(root);
            if (path is not null) return path;
        }
        return null;
    }

    private static IEnumerable<string> EnumeratePackageRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            Add(Path.Combine(prefix, "node_modules", "@volcengine", "ark-cli"));
            Add(Path.Combine(prefix, "lib", "node_modules", "@volcengine", "ark-cli"));
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
            Add(Path.Combine(appData, "npm", "node_modules", "@volcengine", "ark-cli"));

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathFullyQualified(entry)) continue;
            var fullEntry = Path.GetFullPath(entry);
            Add(Path.Combine(fullEntry, "node_modules", "@volcengine", "ark-cli"));
            Add(Path.Combine(fullEntry, "lib", "node_modules", "@volcengine", "ark-cli"));
            var parent = Directory.GetParent(fullEntry)?.FullName;
            if (Path.GetFileName(fullEntry).Equals(".bin", StringComparison.OrdinalIgnoreCase) && parent is not null)
                Add(Path.Combine(parent, "@volcengine", "ark-cli"));
            if (parent is not null)
            {
                Add(Path.Combine(parent, "node_modules", "@volcengine", "ark-cli"));
                Add(Path.Combine(parent, "lib", "node_modules", "@volcengine", "ark-cli"));
            }
        }

        foreach (var root in seen) yield return root;

        void Add(string root)
        {
            try
            {
                if (Path.IsPathFullyQualified(root)) seen.Add(Path.GetFullPath(root));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
    }

    private static string? TryResolvePackage(string root)
    {
        try
        {
            var packagePath = Path.Combine(root, "package.json");
            var manifestPath = Path.Combine(root, "manifest.json");
            using var package = ReadJson(packagePath);
            using var manifest = ReadJson(manifestPath);
            if (package is null || manifest is null) return null;

            var packageRoot = package.RootElement;
            if (!HasString(packageRoot, "name", ExpectedPackageName) ||
                !HasOfficialRepository(packageRoot) ||
                !TryString(packageRoot, "version", out var version) ||
                !HasString(manifest.RootElement, "version", version))
                return null;

            var platform = GetPlatformName();
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "amd64",
                Architecture.Arm64 => "arm64",
                _ => null
            };
            if (architecture is null) return null;
            var platformKey = $"{platform}-{architecture}";
            if (!manifest.RootElement.TryGetProperty("platforms", out var platforms) ||
                !platforms.TryGetProperty(platformKey, out var metadata) ||
                !TryString(metadata, "sha256", out var expectedHash) || expectedHash.Length != 64 ||
                expectedHash.Any(character => !Uri.IsHexDigit(character)) ||
                !TryString(metadata, "url", out var sourceUrl) || !IsOfficialSource(sourceUrl))
                return null;

            var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            var binaryName = $"arkcli-{platform}-{architecture}{extension}";
            var binaryPath = Path.GetFullPath(Path.Combine(root, "bin", binaryName));
            var binRoot = Path.GetFullPath(Path.Combine(root, "bin") + Path.DirectorySeparatorChar);
            if (!binaryPath.StartsWith(binRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(binaryPath))
                return null;

            using var stream = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            return StringComparer.OrdinalIgnoreCase.Equals(actualHash, expectedHash) ? binaryPath : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or ArgumentException or InvalidOperationException or
                                           NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static JsonDocument? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumMetadataBytes) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
    }

    private static bool HasOfficialRepository(JsonElement package)
    {
        if (!package.TryGetProperty("repository", out var repository)) return false;
        var url = repository.ValueKind == JsonValueKind.String
            ? repository.GetString()
            : repository.ValueKind == JsonValueKind.Object && TryString(repository, "url", out var nested)
                ? nested
                : null;
        if (url is null) return false;
        if (url.StartsWith("git+", StringComparison.Ordinal)) url = url[4..];
        return StringComparer.OrdinalIgnoreCase.Equals(url, ExpectedRepository);
    }

    private static bool IsOfficialSource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        return uri.IsDefaultPort &&
               (StringComparer.OrdinalIgnoreCase.Equals(uri.Host, "lf3-static.bytednsdoc.com") ||
               (StringComparer.OrdinalIgnoreCase.Equals(uri.Host, "github.com") &&
                uri.AbsolutePath.StartsWith("/volcengine/ark-cli/releases/download/", StringComparison.Ordinal)));
    }

    private static string? GetPlatformName()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "darwin";
        if (OperatingSystem.IsLinux()) return "linux";
        return null;
    }

    private static bool HasString(JsonElement element, string name, string expected) =>
        TryString(element, name, out var value) && StringComparer.Ordinal.Equals(value, expected);

    private static bool TryString(JsonElement element, string name, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String && property.GetString() is string text)
        {
            value = text;
            return true;
        }
        value = string.Empty;
        return false;
    }
}
