#nullable enable

using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Cerith.Core;
using Microsoft.Win32.SafeHandles;

namespace Cerith.TelemetryService;

public sealed class NamedPipeMetricServer(string pipeName, MetricBus bus)
{
    private readonly string _pipeName = ValidateName(pipeName);
    private readonly MetricBus _bus = bus ?? throw new ArgumentNullException(nameof(bus));

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = CreatePipe(_pipeName);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await ServeAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (IOException) when (pipe.IsConnected) { continue; }
            catch (InvalidDataException) when (pipe.IsConnected) { continue; }
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
        try
        {
            await MetricIpcProtocol.WriteAcceptedAsync(pipe, session.Token).ConfigureAwait(false);
            while (true)
            {
                if (await Task.WhenAny(next, disconnected).ConfigureAwait(false) == disconnected) break;
                if (!await next.ConfigureAwait(false)) break;
                await MetricIpcProtocol.WriteSnapshotAsync(pipe, subscription.Current, session.Token).ConfigureAwait(false);
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
            var handle = CreateNamedPipe($@"\\.\pipe\{pipeName}", 0x40000003, 0x00000008, 1,
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
