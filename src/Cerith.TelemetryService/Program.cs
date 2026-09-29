#nullable enable

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

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
        Console.WriteLine($"TelemetryService ready: pipe {MetricIpcProtocol.LocalPipeName}; hardware metrics publishing; " +
            (processId.HasValue ? "gaming target selected" : "gaming stopped (no target PID)"));

        try { await Task.WhenAny(server, publisher); }
        finally { await stop.CancelAsync(); }

        try { await Task.WhenAll(server, publisher, gamingPublisher); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Console.WriteLine("TelemetryService stopped.");
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
