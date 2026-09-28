#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal static class Program
{
    private static async Task Main()
    {
        try { await RunAsync(); }
        catch (Exception)
        {
            Console.Error.WriteLine("TelemetryService failed: runtime_failure");
            Environment.ExitCode = 1;
        }
    }

    private static async Task RunAsync()
    {
        using var stop = new CancellationTokenSource();
        using var bus = new MetricBus(1);
        using var provider = new HardwareTelemetryProvider(new MetricRegistry(), bus);

        Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.Cancel(); };
        var server = new NamedPipeMetricServer(MetricIpcProtocol.LocalPipeName, bus).RunAsync(stop.Token);
        if (server.IsCompleted) await server;
        var discovered = provider.Discovered;
        Console.WriteLine($"Hardware discovered: cpu={discovered.Cpu} gpu={discovered.Gpu} storage={discovered.Storage} network={discovered.Network}; zero means not discovered, cause unknown.");
        Console.WriteLine("Hardware diagnostics cannot detect all internal LibreHardwareMonitor failures or independently validate sensor truth.");
        var first = (await provider.PublishNextAsync(stop.Token)).ToString();
        Console.WriteLine($"Hardware sample: {first}");
        var publisher = PublishAsync(provider, first, stop.Token);
        Console.WriteLine($"TelemetryService ready: pipe {MetricIpcProtocol.LocalPipeName}; hardware metrics publishing");

        try { await Task.WhenAny(server, publisher); }
        finally { await stop.CancelAsync(); }

        try { await Task.WhenAll(server, publisher); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Console.WriteLine("TelemetryService stopped.");
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
}
