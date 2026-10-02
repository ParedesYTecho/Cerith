#nullable enable

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Cerith.TelemetryService.Providers.AI.Antigravity;
using Cerith.TelemetryService.Providers.AI.CommandCode;
using Cerith.TelemetryService.Providers.AI.GitHubCopilot;
using Cerith.TelemetryService.Providers.AI.Kiro;

namespace Cerith.TelemetryService;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (!TryParseTarget(args, out var processId))
        {
            Console.Error.WriteLine("TelemetryService failed: invalid_target_pid");
            Environment.ExitCode = 2;
            return;
        }

        try { await RunAsync(processId); }
        catch (Exception)
        {
            Console.Error.WriteLine("TelemetryService failed: runtime_failure");
            Environment.ExitCode = 1;
        }
    }

    private static async Task RunAsync(int? processId)
    {
        using var stop = new CancellationTokenSource();
        using var bus = new MetricBus(1);
        var registry = new MetricRegistry();
        using var provider = new HardwareTelemetryProvider(registry, bus);
        using var gamingProvider = new PresentMonTelemetryProvider(registry, bus, processId);

        Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.Cancel(); };
        var server = new NamedPipeMetricServer(MetricIpcProtocol.LocalPipeName, bus).RunAsync(stop.Token);
        if (server.IsCompleted) await server;
        var discovered = provider.Discovered;
        Console.WriteLine($"Hardware discovered: cpu={discovered.Cpu} gpu={discovered.Gpu} storage={discovered.Storage} network={discovered.Network}; zero means not discovered, cause unknown.");
        Console.WriteLine("Hardware diagnostics cannot detect all internal LibreHardwareMonitor failures or independently validate sensor truth.");
        var first = (await provider.PublishNextAsync(stop.Token)).ToString();
        Console.WriteLine($"Hardware sample: {first}");
        var publisher = PublishAsync(provider, first, stop.Token);
        await gamingProvider.PublishNextAsync(stop.Token);
        var gamingPublisher = PublishGamingAsync(gamingProvider, stop.Token);
        var codexQuotaRunner = KeepServiceAliveAfterProviderExitAsync(async () =>
        {
            using var codexQuotaProvider = new CodexQuotaProvider(registry, bus);
            await codexQuotaProvider.RunAsync(stop.Token).ConfigureAwait(false);
        }, "codex", stop.Token);
        var cursorQuotaRunner = KeepServiceAliveAfterProviderExitAsync(async () =>
        {
            using var cursorQuotaProvider = new CursorQuotaProvider(registry, bus);
            await cursorQuotaProvider.RunAsync(stop.Token).ConfigureAwait(false);
        }, "cursor", stop.Token);
        var antigravityQuotaRunner = KeepServiceAliveAfterProviderExitAsync(async () =>
        {
            using var antigravityQuotaProvider = new AntigravityQuotaProvider(registry, bus);
            await antigravityQuotaProvider.RunAsync(stop.Token).ConfigureAwait(false);
        }, "antigravity", stop.Token);
        var zcodeQuotaRunner = KeepServiceAliveAfterProviderExitAsync(async () =>
        {
            using var zcodeQuotaProvider = new ZCodeQuotaProvider(registry, bus);
            await zcodeQuotaProvider.RunAsync(stop.Token).ConfigureAwait(false);
        }, "zcode", stop.Token);
        var copilotQuotaRunner = KeepServiceAliveAfterProviderExitAsync(async () =>
        {
            using var copilotQuotaProvider = new CopilotQuotaProvider(registry, bus);
            await copilotQuotaProvider.RunAsync(stop.Token).ConfigureAwait(false);
        }, "github-copilot", stop.Token);
        var openCodeGoQuotaRunner = KeepServiceAliveAfterProviderExitAsync(async () =>
        {
            using var openCodeGoQuotaProvider = new OpenCodeGoQuotaProvider(registry, bus);
            await openCodeGoQuotaProvider.RunAsync(stop.Token).ConfigureAwait(false);
        }, "opencode-go", stop.Token);
        var kiroQuotaRunner = KeepServiceAliveAfterProviderExitAsync(async () =>
        {
            using var kiroQuotaProvider = new KiroQuotaProvider(registry, bus);
            await kiroQuotaProvider.RunAsync(stop.Token).ConfigureAwait(false);
        }, "kiro", stop.Token);
        var commandCodeQuotaRunner = KeepServiceAliveAfterProviderExitAsync(async () =>
        {
            using var commandCodeQuotaProvider = new CommandCodeQuotaProvider(registry, bus);
            await commandCodeQuotaProvider.RunAsync(stop.Token).ConfigureAwait(false);
        }, "command-code", stop.Token);
        var qoderCnQuotaRunner = KeepServiceAliveAfterProviderExitAsync(async () =>
        {
            var qoderCnQuotaProvider = new QoderCnQuotaProvider(registry, bus);
            await qoderCnQuotaProvider.RunAsync(stop.Token).ConfigureAwait(false);
        }, "qoder-cn", stop.Token);
        Console.WriteLine($"TelemetryService ready: pipe {MetricIpcProtocol.LocalPipeName}; hardware metrics publishing; " +
            (processId.HasValue ? "gaming target selected" : "gaming stopped (no target PID)") +
            "; Codex file-auth, Cursor desktop-auth, Antigravity local-service, ZCode coding-plan, " +
            "GitHub Copilot CLI, OpenCode Go, Kiro ACP, Command Code, and Qoder CN quota providers active");

        try { await Task.WhenAny(server, publisher, gamingPublisher, codexQuotaRunner, cursorQuotaRunner,
            antigravityQuotaRunner, zcodeQuotaRunner, copilotQuotaRunner, openCodeGoQuotaRunner,
            kiroQuotaRunner, commandCodeQuotaRunner, qoderCnQuotaRunner); }
        finally { await stop.CancelAsync(); }

        try { await Task.WhenAll(server, publisher, gamingPublisher, codexQuotaRunner, cursorQuotaRunner,
            antigravityQuotaRunner, zcodeQuotaRunner, copilotQuotaRunner, openCodeGoQuotaRunner,
            kiroQuotaRunner, commandCodeQuotaRunner, qoderCnQuotaRunner); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Console.WriteLine("TelemetryService stopped.");
    }

    internal static async Task KeepServiceAliveAfterProviderExitAsync(Func<Task> startProvider, string providerId,
        CancellationToken serviceStop)
    {
        var errorCode = "provider_stopped";
        try { await startProvider().ConfigureAwait(false); }
        catch (OperationCanceledException) when (serviceStop.IsCancellationRequested) { return; }
        catch (Exception) { errorCode = "provider_failure"; }

        if (serviceStop.IsCancellationRequested) return;
        Console.Error.WriteLine($"AI quota provider stopped: {providerId}; {errorCode}");
        try { await Task.Delay(Timeout.InfiniteTimeSpan, serviceStop).ConfigureAwait(false); }
        catch (OperationCanceledException) when (serviceStop.IsCancellationRequested) { }
    }

    private static bool TryParseTarget(string[] args, out int? processId)
    {
        processId = null;
        if (args.Length == 0) return true;
        if (args.Length != 2 || !StringComparer.Ordinal.Equals(args[0], "--pid") ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            return false;
        processId = value;
        return true;
    }

    private static async Task PublishAsync(HardwareTelemetryProvider provider, string previous,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var current = (await provider.PublishNextAsync(cancellationToken)).ToString();
            if (current != previous) Console.WriteLine($"Hardware sample: {current}");
            previous = current;
        }
    }

    private static async Task PublishGamingAsync(PresentMonTelemetryProvider provider,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await provider.PublishNextAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
