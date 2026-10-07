#nullable enable

using System;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Microsoft.Win32.SafeHandles;

namespace Cerith.TelemetryService;

public sealed class NamedPipeMetricServer(string pipeName, MetricBus bus, MetricRegistry registry)
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;

    private readonly string _pipeName = ValidateName(pipeName);
    private readonly MetricBus _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    private readonly MetricRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sessions = new ConcurrentDictionary<long, Task>();
        long sequence = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var pipe = CreatePipe(_pipeName);
                try
                {
                    await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                    var key = ++sequence;
                    var session = HandleSessionAsync(pipe, stop.Token);
                    sessions[key] = session;
                    _ = session.ContinueWith(completed =>
                    {
                        _ = completed.Exception;
                        sessions.TryRemove(key, out _);
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                catch
                {
                    pipe.Dispose();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(sessions.Values).ConfigureAwait(false);
        }
    }

    private async Task HandleSessionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using (pipe)
        {
            try { await ServeAsync(pipe, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }
            catch (InvalidDataException) { }
            catch (Exception) { Console.Error.WriteLine("Metric IPC session failed."); }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var id = await MetricIpcProtocol.ReadSubscribeAsync(pipe, cancellationToken).ConfigureAwait(false);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var subscription = _bus.Subscribe(id, session.Token).GetAsyncEnumerator(session.Token);
        // MoveNextAsync enters MetricBus.Subscribe and installs its subscription before accepted.
        var next = subscription.MoveNextAsync().AsTask();
        var disconnected = pipe.ReadAsync(new byte[1], session.Token).AsTask();
        _registry.TryGet(id, out var descriptor);
        try
        {
            await MetricIpcProtocol.WriteAcceptedAsync(pipe, descriptor, session.Token).ConfigureAwait(false);
            while (true)
            {
                if (await Task.WhenAny(next, disconnected).ConfigureAwait(false) == disconnected) break;
                if (!await next.ConfigureAwait(false)) break;
                MetricDescriptor? lateDescriptor = null;
                if (descriptor is null)
                {
                    _registry.TryGet(id, out lateDescriptor);
                    descriptor = lateDescriptor;
                }
                await MetricIpcProtocol.WriteSnapshotAsync(pipe, subscription.Current,
                    lateDescriptor, session.Token).ConfigureAwait(false);
                next = subscription.MoveNextAsync().AsTask();
            }
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            await DrainAsync(next, session.Token).ConfigureAwait(false);
            await DrainAsync(disconnected, session.Token).ConfigureAwait(false);
        }
    }

    private static async Task DrainAsync(Task pending, CancellationToken cancellationToken)
    {
        try { await pending.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (IOException) when (cancellationToken.IsCancellationRequested) { return; }
    }

    private static string ValidateName(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (pipeName.Contains('\\') || pipeName.Length > 200) throw new ArgumentException("Invalid pipe name.", nameof(pipeName));
        return pipeName;
    }

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current user SID unavailable.");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;{sid})", 1, out var descriptor, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = descriptor };
            var handle = CreateNamedPipe($@"\\.\pipe\{pipeName}",
                PipeAccessDuplex | FileFlagOverlapped, PipeRejectRemoteClients, 255,
                MetricIpcProtocol.MaxFrameSize, MetricIpcProtocol.MaxFrameSize, 0, ref security);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { _ = LocalFree(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes security);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
