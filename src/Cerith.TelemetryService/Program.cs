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
        using var stop = new CancellationTokenSource();
        using var bus = new MetricBus(1);
        using var provider = new HardwareTelemetryProvider(new MetricRegistry(), bus);

        Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.Cancel(); };
        var server = new NamedPipeMetricServer(MetricIpcProtocol.LocalPipeName, bus).RunAsync(stop.Token);
        if (server.IsCompleted) await server;
        await provider.PublishNextAsync(stop.Token);
        var publisher = PublishAsync(provider, stop.Token);
        Console.WriteLine($"TelemetryService ready: pipe {MetricIpcProtocol.LocalPipeName}; hardware metrics publishing");

        try { await Task.WhenAny(server, publisher); }
        finally { await stop.CancelAsync(); }

        try { await Task.WhenAll(server, publisher); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Console.WriteLine("TelemetryService stopped.");
    }

    private static async Task PublishAsync(HardwareTelemetryProvider provider, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await provider.PublishNextAsync(cancellationToken);
    }
}
