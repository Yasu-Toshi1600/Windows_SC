using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Windows_SC.Models;

namespace Windows_SC.Services;

internal sealed class WindowsSystemMetricsService : ISystemMetricsService
{
    private const uint PdhFormatDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private readonly DiagnosticLogger _logger;
    private readonly object _configurationGate = new();
    private readonly SystemMetricsState _state = new();
    private readonly BackgroundResourceLifetime _nativeLifetime = new();
    private bool _gpuInitialized;
    private readonly Timer _timer;
    private ulong? _previousIdle;
    private ulong? _previousKernel;
    private ulong? _previousUser;
    private IntPtr _gpuQuery;
    private IntPtr _gpuCounter;
    private bool? _lastGpuAvailable;
    private int _sampleRunning;
    private volatile bool _isDisposed;

    public WindowsSystemMetricsService(DiagnosticLogger logger)
    {
        _logger = logger;
        _timer = new Timer(_ => Sample(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler? MetricsChanged;

    public SystemMetricsSnapshot GetCachedMetrics() => _state.GetCachedMetrics();

    public IReadOnlyList<MonitorGraphPoint> GetGraphPoints(SystemMonitorMetric metric) =>
        _state.GetGraphPoints(metric);

    public SystemMonitorMetric RequestedMetrics => _state.Request.Metrics;

    public void SetMetrics(SystemMonitorMetric metrics)
    {
        lock (_configurationGate)
        {
            if (_isDisposed || !_state.Configure(metrics)) return;
            // Even disabling all metrics gets one background pass to close PDH.
            _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
    }

    private void Sample()
    {
        if (_isDisposed || Interlocked.Exchange(ref _sampleRunning, 1) != 0)
        {
            return;
        }

        try
        {
            _nativeLifetime.TryRun(SampleCore);
        }
        finally
        {
            Volatile.Write(ref _sampleRunning, 0);
        }
    }

    private void SampleCore()
    {
        var request = _state.Request;
        bool sampleGpu = (request.Metrics & SystemMonitorMetric.Gpu) != 0;
        double? gpu = null;
        if (!sampleGpu)
        {
            CloseGpuQuery();
            _gpuInitialized = false;
            _lastGpuAvailable = null;
        }
        else if (!_gpuInitialized)
        {
            _gpuInitialized = true;
            InitializeGpuCounter();
        }
        else
        {
            gpu = ReadGpuPercent();
        }

        if (request.Metrics == SystemMonitorMetric.None)
        {
            lock (_configurationGate)
            {
                if (!_isDisposed && _state.Request.Generation == request.Generation)
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            _previousIdle = _previousKernel = _previousUser = null;
            return;
        }

        double? cpu = null;
        if ((request.Metrics & SystemMonitorMetric.Cpu) != 0)
        {
            cpu = ReadCpuPercent();
        }
        else
        {
            _previousIdle = _previousKernel = _previousUser = null;
        }
        if (_isDisposed) return;
        double memoryPercent = 0;
        ulong usedBytes = 0, totalBytes = 0;
        if ((request.Metrics & SystemMonitorMetric.Memory) != 0)
        {
            ReadMemory(out memoryPercent, out usedBytes, out totalBytes);
        }
        if (!_state.Publish(new SystemMetricsSnapshot(cpu, gpu, memoryPercent, usedBytes, totalBytes),
                request.Generation)) return;

        bool gpuAvailable = gpu.HasValue;
        if (sampleGpu && _lastGpuAvailable != gpuAvailable)
        {
            _lastGpuAvailable = gpuAvailable;
            _logger.Write(
                $"[SystemMetrics] action=gpu-sample " +
                $"result={(gpuAvailable ? "success" : "skipped")}");
        }

        if (!_isDisposed)
        {
            MetricsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private double? ReadCpuPercent()
    {
        if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
        {
            return null;
        }

        ulong idleValue = idle.ToUInt64();
        ulong kernelValue = kernel.ToUInt64();
        ulong userValue = user.ToUInt64();
        if (_previousIdle is null || _previousKernel is null || _previousUser is null)
        {
            _previousIdle = idleValue;
            _previousKernel = kernelValue;
            _previousUser = userValue;
            return null;
        }

        ulong idleDelta = idleValue - _previousIdle.Value;
        ulong kernelDelta = kernelValue - _previousKernel.Value;
        ulong userDelta = userValue - _previousUser.Value;
        _previousIdle = idleValue;
        _previousKernel = kernelValue;
        _previousUser = userValue;
        ulong total = kernelDelta + userDelta;
        return total == 0
            ? null
            : Math.Clamp((total - Math.Min(total, idleDelta)) * 100d / total, 0, 100);
    }

    private double? ReadGpuPercent()
    {
        if (_gpuQuery == IntPtr.Zero || _gpuCounter == IntPtr.Zero
            || PdhCollectQueryData(_gpuQuery) != 0)
        {
            return null;
        }

        uint bufferSize = 0;
        uint itemCount = 0;
        uint status = PdhGetFormattedCounterArray(
            _gpuCounter,
            PdhFormatDouble,
            ref bufferSize,
            out itemCount,
            IntPtr.Zero);
        if (status != PdhMoreData || bufferSize == 0 || itemCount == 0)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = PdhGetFormattedCounterArray(
                _gpuCounter,
                PdhFormatDouble,
                ref bufferSize,
                out itemCount,
                buffer);
            if (status != 0)
            {
                return null;
            }

            double maximum = 0;
            bool hasValue = false;
            int itemSize = Marshal.SizeOf<PdhFormattedCounterValueItem>();
            for (int index = 0; index < itemCount; index++)
            {
                PdhFormattedCounterValueItem item =
                    Marshal.PtrToStructure<PdhFormattedCounterValueItem>(
                        IntPtr.Add(buffer, index * itemSize));
                if (item.Value.Status == 0 && double.IsFinite(item.Value.DoubleValue))
                {
                    maximum = Math.Max(maximum, item.Value.DoubleValue);
                    hasValue = true;
                }
            }

            return hasValue ? Math.Clamp(maximum, 0, 100) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ReadMemory(
        out double percent,
        out ulong usedBytes,
        out ulong totalBytes)
    {
        MemoryStatus status = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0)
        {
            percent = 0;
            usedBytes = 0;
            totalBytes = 0;
            return;
        }

        totalBytes = status.TotalPhysical;
        usedBytes = totalBytes - Math.Min(totalBytes, status.AvailablePhysical);
        percent = Math.Clamp(usedBytes * 100d / totalBytes, 0, 100);
    }

    private void InitializeGpuCounter()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _gpuQuery) != 0
            || PdhAddEnglishCounter(
                _gpuQuery,
                @"\GPU Engine(*)\Utilization Percentage",
                IntPtr.Zero,
                out _gpuCounter) != 0)
        {
            CloseGpuQuery();
            return;
        }

        // The first collection establishes a baseline and must not be shown as 0%.
        PdhCollectQueryData(_gpuQuery);
    }

    private void CloseGpuQuery()
    {
        if (_gpuQuery != IntPtr.Zero)
        {
            PdhCloseQuery(_gpuQuery);
        }

        _gpuQuery = IntPtr.Zero;
        _gpuCounter = IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (_configurationGate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _timer.Dispose();
        }
        bool completed = _nativeLifetime.Stop(
            CloseGpuQuery,
            exception => System.Diagnostics.Debug.WriteLine(
                $"System metrics cleanup failed: {exception.GetType().Name}"),
            TimeSpan.FromSeconds(1));
        _logger.Write(
            $"[SystemMetrics] action=dispose result={(completed ? "success" : "skipped")} " +
            $"reason={(completed ? "cleanup-finished" : "cleanup-pending")}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValue
    {
        public uint Status;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValueItem
    {
        public IntPtr Name;
        public PdhFormattedCounterValue Value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(
        string? dataSource,
        IntPtr userData,
        out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(
        IntPtr query,
        string counterPath,
        IntPtr userData,
        out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        out uint itemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
