#nullable enable

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Cerith.Core;

namespace Cerith.TelemetryService;

internal interface IPresentMonCapture : IDisposable
{
    PresentMonReading Poll();
}

internal readonly record struct PresentMonReading(
    MetricState State,
    double? Fps = null,
    double? FrameTimeMilliseconds = null,
    string? ErrorCode = null);

internal sealed class PresentMonCaptureException(MetricState state, string errorCode) : Exception
{
    public MetricState State { get; } = state;
    public string ErrorCode { get; } = errorCode;
}

internal sealed class PresentMonCapture : IPresentMonCapture
{
    private const int ApiMajor = 3;
    private const int ApiMinor = 4;
    private const int MetricTypeDynamic = 0;
    private const int MetricTypeFrameEvent = 2;
    private const int MetricTypeDynamicFrame = 3;
    private const int DataTypeDouble = 0;
    private const int DataTypeUInt64 = 5;
    private const int DataTypeVoid = 7;
    private const int UnitFramesPerSecond = 4;
    private const int UnitMilliseconds = 6;
    private const int UnitQpc = 30;
    private const int MetricAvailable = 0;
    private const uint DefaultDeviceId = 0;
    private const int MetricDisplayedFps = 11;
    private const int MetricDisplayedFrameTime = 85;
    private const int MetricPresentStartQpc = 77;
    private const int MetricBetweenDisplayChange = 80;
    private const int StatisticNone = 0;
    private const int StatisticAverage = 1;
    private const int StatusSuccess = 0;
    private const int StatusInvalidPid = 6;
    private const int StatusPipeError = 12;
    private const int StatusMiddlewareMissingPath = 14;
    private const int StatusMissingFile = 15;
    private const int StatusMiddlewareMissingEndpoint = 17;
    private const int StatusMiddlewareVersionLow = 18;
    private const int StatusMiddlewareVersionHigh = 19;
    private const int StatusMiddlewareServiceMismatch = 20;
    private const int StatusFeatureDisabled = 23;
    private const int MaxSwapChains = 16;
    private const int FrameCapacity = 4096;
    private static readonly long MaxFrameAge = Stopwatch.Frequency * 2;

    private readonly uint _processId;
    private Process? _process;
    private IntPtr _session;
    private IntPtr _dynamicQuery;
    private IntPtr _frameQuery;
    private QueryElement[] _dynamicElements = [];
    private QueryElement[] _frameElements = [];
    private byte[] _dynamicBlob = [];
    private byte[] _frameBlob = [];
    private int _frameRowSize;
    private long _lastDisplayedQpc;
    private bool _tracking;
    private bool _disposed;

    public PresentMonCapture(int processId)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        _processId = (uint)processId;

        try
        {
            try { _process = Process.GetProcessById(processId); }
            catch (ArgumentException) { throw Stopped(); }
            if (TargetHasExited()) throw Stopped();

            Check(Native.pmGetApiVersion(out var version));
            if (version.Major != ApiMajor || version.Minor < ApiMinor)
                throw Failure(MetricState.Error, "presentmon_api_mismatch");

            Check(Native.pmOpenSession(out _session));
            ValidateCapabilities();

            var trackStatus = Native.pmStartTrackingProcess(_session, _processId);
            Check(trackStatus);
            _tracking = true;

            _dynamicElements =
            [
                new QueryElement { Metric = MetricDisplayedFps, Statistic = StatisticAverage },
                new QueryElement { Metric = MetricDisplayedFrameTime, Statistic = StatisticAverage }
            ];
            Check(Native.pmRegisterDynamicQuery(_session, out _dynamicQuery, _dynamicElements,
                (ulong)_dynamicElements.Length, 1000d, 0d));
            var dynamicRowSize = GetRowSize(_dynamicElements, 64);
            if (dynamicRowSize == 0 || _dynamicElements[0].DataSize != sizeof(double) ||
                _dynamicElements[1].DataSize != sizeof(double))
                throw Failure(MetricState.Error, "presentmon_query_layout");
            _dynamicBlob = new byte[checked(dynamicRowSize * MaxSwapChains)];

            _frameElements =
            [
                new QueryElement { Metric = MetricPresentStartQpc, Statistic = StatisticNone },
                new QueryElement { Metric = MetricBetweenDisplayChange, Statistic = StatisticNone }
            ];
            Check(Native.pmRegisterFrameQuery(_session, out _frameQuery, _frameElements,
                (ulong)_frameElements.Length, out var frameRowSize));
            if (frameRowSize == 0 || frameRowSize > 64 || GetRowSize(_frameElements, 64) > frameRowSize ||
                _frameElements[0].DataSize != sizeof(ulong) || _frameElements[1].DataSize != sizeof(double))
                throw Failure(MetricState.Error, "presentmon_query_layout");
            _frameRowSize = checked((int)frameRowSize);
            _frameBlob = new byte[checked(_frameRowSize * FrameCapacity)];
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public PresentMonReading Poll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (TargetHasExited()) return new PresentMonReading(MetricState.Stopped);

        uint swapChains = MaxSwapChains;
        Check(Native.pmPollDynamicQuery(_dynamicQuery, _processId, _dynamicBlob, ref swapChains));

        uint frameCount = FrameCapacity;
        Check(Native.pmConsumeFrames(_frameQuery, _processId, _frameBlob, ref frameCount));
        if (swapChains > 1)
        {
            if (TargetHasExited()) return new PresentMonReading(MetricState.Stopped);
            return new PresentMonReading(MetricState.Error, ErrorCode: "multiple_swap_chains");
        }
        if (frameCount >= FrameCapacity) return new PresentMonReading(MetricState.Unavailable);

        for (var i = 0; i < frameCount; i++)
        {
            var offset = checked(i * _frameRowSize);
            var qpc = BitConverter.ToUInt64(_frameBlob, offset + checked((int)_frameElements[0].DataOffset));
            var displayDelta = BitConverter.ToDouble(_frameBlob, offset + checked((int)_frameElements[1].DataOffset));
            if (qpc > long.MaxValue || !double.IsFinite(displayDelta) || displayDelta <= 0) continue;
            if (qpc > (ulong)_lastDisplayedQpc) _lastDisplayedQpc = (long)qpc;
        }

        if (TargetHasExited()) return new PresentMonReading(MetricState.Stopped);
        if (swapChains != 1) return new PresentMonReading(MetricState.Unavailable);

        var fps = ReadDouble(_dynamicBlob, 0, _dynamicElements[0]);
        var frameTime = ReadDouble(_dynamicBlob, 0, _dynamicElements[1]);
        if (!double.IsFinite(fps) || !double.IsFinite(frameTime) || fps < 0 || frameTime < 0)
            return new PresentMonReading(MetricState.Error, ErrorCode: "invalid_reading");
        if (fps == 0 || frameTime == 0) return new PresentMonReading(MetricState.Unavailable);

        var now = Stopwatch.GetTimestamp();
        if (_lastDisplayedQpc <= 0 || now < _lastDisplayedQpc || now - _lastDisplayedQpc > MaxFrameAge)
            return new PresentMonReading(MetricState.Unavailable);

        return new PresentMonReading(MetricState.Fresh, fps, frameTime);
    }

    private void ValidateCapabilities()
    {
        Check(Native.pmGetIntrospectionRoot(_session, out var root));
        try
        {
            var data = Marshal.PtrToStructure<IntrospectionRoot>(root);
            if (!Supports(data.Metrics, MetricDisplayedFps, MetricTypeDynamic, UnitFramesPerSecond,
                    DataTypeDouble, DataTypeDouble, StatisticAverage) ||
                !Supports(data.Metrics, MetricDisplayedFrameTime, MetricTypeDynamic, UnitMilliseconds,
                    DataTypeDouble, DataTypeDouble, StatisticAverage) ||
                !Supports(data.Metrics, MetricPresentStartQpc, MetricTypeFrameEvent, UnitQpc,
                    DataTypeVoid, DataTypeUInt64, StatisticNone) ||
                !Supports(data.Metrics, MetricBetweenDisplayChange, MetricTypeDynamicFrame, UnitMilliseconds,
                    DataTypeDouble, DataTypeDouble, StatisticAverage))
                throw Failure(MetricState.Unavailable, "presentmon_metric_unavailable");
        }
        finally
        {
            Native.pmFreeIntrospectionRoot(root);
        }
    }

    private static bool Supports(IntPtr metricsPointer, int id, int type, int unit, int polledType,
        int frameType, int statistic)
    {
        if (metricsPointer == IntPtr.Zero) return false;
        var metrics = Marshal.PtrToStructure<NativeArray>(metricsPointer);
        if (metrics.Data == IntPtr.Zero || metrics.Size > 1024) return false;
        for (nuint i = 0; i < metrics.Size; i++)
        {
            var itemPointer = Marshal.ReadIntPtr(metrics.Data, checked((int)i * IntPtr.Size));
            if (itemPointer == IntPtr.Zero) continue;
            var item = Marshal.PtrToStructure<IntrospectionMetric>(itemPointer);
            if (item.Id != id) continue;
            if (item.Type != type || item.Unit != unit || item.TypeInfo == IntPtr.Zero) return false;
            var dataType = Marshal.PtrToStructure<DataTypeInfo>(item.TypeInfo);
            if (dataType.PolledType != polledType || dataType.FrameType != frameType ||
                !ContainsStatistic(item.Statistics, statistic) || !HasDefaultDevice(item.DeviceMetrics))
                return false;
            return true;
        }
        return false;
    }

    private static bool ContainsStatistic(IntPtr arrayPointer, int statistic)
    {
        if (arrayPointer == IntPtr.Zero) return false;
        var array = Marshal.PtrToStructure<NativeArray>(arrayPointer);
        if (array.Data == IntPtr.Zero || array.Size > 32) return false;
        for (nuint i = 0; i < array.Size; i++)
        {
            var pointer = Marshal.ReadIntPtr(array.Data, checked((int)i * IntPtr.Size));
            if (pointer != IntPtr.Zero && Marshal.PtrToStructure<StatisticInfo>(pointer).Statistic == statistic)
                return true;
        }
        return false;
    }

    private static bool HasDefaultDevice(IntPtr arrayPointer)
    {
        if (arrayPointer == IntPtr.Zero) return false;
        var array = Marshal.PtrToStructure<NativeArray>(arrayPointer);
        if (array.Data == IntPtr.Zero || array.Size > 64) return false;
        for (nuint i = 0; i < array.Size; i++)
        {
            var pointer = Marshal.ReadIntPtr(array.Data, checked((int)i * IntPtr.Size));
            if (pointer == IntPtr.Zero) continue;
            var device = Marshal.PtrToStructure<DeviceMetricInfo>(pointer);
            if (device.DeviceId == DefaultDeviceId && device.Availability == MetricAvailable) return true;
        }
        return false;
    }

    private static ulong GetRowSize(QueryElement[] elements, ulong maximum)
    {
        ulong size = 0;
        foreach (var element in elements)
        {
            var end = checked(element.DataOffset + element.DataSize);
            if (end > maximum) throw Failure(MetricState.Error, "presentmon_query_layout");
            if (end > size) size = end;
        }
        return size;
    }

    private static double ReadDouble(byte[] blob, int row, QueryElement element) =>
        BitConverter.ToDouble(blob, checked(row + (int)element.DataOffset));

    private bool TargetHasExited()
    {
        try { return _process is null || _process.HasExited; }
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception)
        {
            throw Failure(MetricState.Error, "target_status_failed");
        }
    }

    private static void Check(int status)
    {
        if (status == StatusSuccess) return;
        if (status == StatusInvalidPid) throw Stopped();
        if (status is StatusPipeError or StatusMiddlewareMissingPath or StatusMissingFile or
            StatusMiddlewareMissingEndpoint or StatusFeatureDisabled)
            throw Failure(MetricState.Unavailable, "presentmon_unavailable");
        if (status is StatusMiddlewareVersionLow or StatusMiddlewareVersionHigh or StatusMiddlewareServiceMismatch)
            throw Failure(MetricState.Error, "presentmon_api_mismatch");
        throw Failure(MetricState.Error, "presentmon_api_failure");
    }

    private static PresentMonCaptureException Stopped() =>
        Failure(MetricState.Stopped, "target_stopped");

    private static PresentMonCaptureException Failure(MetricState state, string errorCode) =>
        new(state, errorCode);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_frameQuery != IntPtr.Zero) Native.pmFreeFrameQuery(_frameQuery);
        if (_dynamicQuery != IntPtr.Zero) Native.pmFreeDynamicQuery(_dynamicQuery);
        if (_tracking && _session != IntPtr.Zero) Native.pmStopTrackingProcess(_session, _processId);
        if (_session != IntPtr.Zero) Native.pmCloseSession(_session);
        _process?.Dispose();
        _frameQuery = IntPtr.Zero;
        _dynamicQuery = IntPtr.Zero;
        _session = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeArray { public IntPtr Data; public nuint Size; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IntrospectionRoot { public IntPtr Metrics, Enums, Devices, Units; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IntrospectionMetric
    {
        public int Id, Type, Unit, PreferredUnit;
        public IntPtr TypeInfo, Statistics, DeviceMetrics;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataTypeInfo { public int PolledType, FrameType, EnumId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatisticInfo { public int Statistic; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceMetricInfo { public uint DeviceId; public int Availability; public uint ArraySize; }

    [StructLayout(LayoutKind.Sequential, Size = 40)]
    private struct ApiVersion { public ushort Major, Minor, Patch; }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryElement
    {
        public int Metric, Statistic;
        public uint DeviceId, ArrayIndex;
        public ulong DataOffset, DataSize;
    }

    private static class Native
    {
        private const string Loader = "PresentMonAPI2Loader.dll";

        [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pmGetApiVersion(out ApiVersion version);
        [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pmOpenSession(out IntPtr session);
        [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pmCloseSession(IntPtr session);
        [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pmStartTrackingProcess(IntPtr session, uint processId);
        [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pmStopTrackingProcess(IntPtr session, uint processId);
        [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pmGetIntrospectionRoot(IntPtr session, out IntPtr root);
        [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pmFreeIntrospectionRoot(IntPtr root);
        [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pmRegisterDynamicQuery(IntPtr session, out IntPtr query,
            [In, Out] QueryElement[] elements, ulong count, double windowSizeMs, double metricOffsetMs);
        [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pmFreeDynamicQuery(IntPtr query);
        [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pmPollDynamicQuery(IntPtr query, uint processId,
            [Out] byte[] blob, ref uint swapChains);
        [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pmRegisterFrameQuery(IntPtr session, out IntPtr query,
            [In, Out] QueryElement[] elements, ulong count, out uint blobSize);
        [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pmFreeFrameQuery(IntPtr query);
        [DllImport(Loader, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pmConsumeFrames(IntPtr query, uint processId,
            [Out] byte[] blobs, ref uint frameCount);
    }
}
