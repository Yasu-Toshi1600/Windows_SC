using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace Windows_SC.Services;

internal sealed class ProcessMemoryDiagnostics : IDisposable
{
    private readonly DiagnosticLogger _logger;
    private readonly object _gate = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly Timer _timer;
    private readonly ProcessGpuUsageSampler _gpu = new();
    private long? _previousTimestamp;
    private TimeSpan _previousCpu;
    private IoCounters? _previousIo;
    private bool _isDisposed;
    private bool _settingsOpen;
    private int _settingsOpenCount;

    public ProcessMemoryDiagnostics(DiagnosticLogger logger)
    {
        _logger = logger;
        _timer = new Timer(_ => Capture("periodic"), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Capture(string reason, bool? settingsOpen = null)
    {
        lock (_gate)
        {
            if (_isDisposed) return;
            if (settingsOpen.HasValue)
            {
                _settingsOpen = settingsOpen.Value;
                if (_settingsOpen) _settingsOpenCount++;
            }

            try
            {
                using Process process = Process.GetCurrentProcess();
                process.Refresh();
                long timestamp = Stopwatch.GetTimestamp();
                TimeSpan cpu = process.TotalProcessorTime;
                double seconds = _previousTimestamp is { } previous
                    ? Stopwatch.GetElapsedTime(previous, timestamp).TotalSeconds : 0;
                string cpuPercent = seconds > 0
                    ? Math.Clamp((cpu - _previousCpu).TotalSeconds / seconds
                        / Environment.ProcessorCount * 100, 0, 100)
                        .ToString("F3", CultureInfo.InvariantCulture)
                    : "warming-up";
                bool ioAvailable = GetProcessIoCounters(process.Handle, out IoCounters io);
                int ioError = ioAvailable ? 0 : Marshal.GetLastWin32Error();
                string ioFields = ioAvailable
                    ? $"io-read-total-bytes={io.ReadBytes} io-write-total-bytes={io.WriteBytes} " +
                      $"io-other-total-bytes={io.OtherBytes} " +
                      $"io-read-total-operations={io.ReadOperations} " +
                      $"io-write-total-operations={io.WriteOperations} " +
                      $"io-other-total-operations={io.OtherOperations} " +
                      (_previousIo is { } previousIo && seconds > 0
                          ? $"io-read-bytes-per-second={Rate(io.ReadBytes, previousIo.ReadBytes, seconds)} " +
                            $"io-write-bytes-per-second={Rate(io.WriteBytes, previousIo.WriteBytes, seconds)}"
                          : "io-read-bytes-per-second=warming-up io-write-bytes-per-second=warming-up")
                    : $"io-state=unavailable io-win32-error={ioError}";
                _previousTimestamp = timestamp;
                _previousCpu = cpu;
                _previousIo = ioAvailable ? io : null;
                string gpuPercent;
                try
                {
                    gpuPercent = _gpu.Sample();
                }
                catch (Exception exception)
                {
                    _gpu.Dispose();
                    gpuPercent = "unavailable";
                    _logger.Write($"[Memory] action=sample-gpu result=failed " +
                        $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
                }
                GCMemoryInfo gc = GC.GetGCMemoryInfo();
                _logger.Write(
                    $"[Memory] action=sample result=success reason={reason} " +
                    $"pid={process.Id} uptime-seconds={_uptime.ElapsedMilliseconds / 1000} " +
                    $"settings-open={(_settingsOpen ? "true" : "false")} " +
                    $"settings-open-count={_settingsOpenCount} " +
                    $"working-set-bytes={process.WorkingSet64} " +
                    $"private-bytes={process.PrivateMemorySize64} " +
                    $"managed-bytes={GC.GetTotalMemory(forceFullCollection: false)} " +
                    $"gc-heap-bytes={gc.HeapSizeBytes} " +
                    $"gc-fragmented-bytes={gc.FragmentedBytes} " +
                    $"gc-committed-bytes={gc.TotalCommittedBytes} " +
                    $"allocated-total-bytes={GC.GetTotalAllocatedBytes(precise: false)} " +
                    $"gen0-count={GC.CollectionCount(0)} " +
                    $"gen1-count={GC.CollectionCount(1)} " +
                    $"gen2-count={GC.CollectionCount(2)} handles={process.HandleCount} " +
                    $"sample-interval-seconds={seconds.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"cpu-percent={cpuPercent} cpu-total-ms={(long)cpu.TotalMilliseconds} " +
                    $"gpu-max-engine-percent={gpuPercent} {ioFields}");
            }
            catch (Exception exception)
            {
                _logger.Write(
                    $"[Memory] action=sample result=failed reason={reason} " +
                    $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _timer.Dispose();
            _gpu.Dispose();
        }
    }

    private static string Rate(ulong current, ulong previous, double seconds) =>
        ((current >= previous ? current - previous : 0) / seconds)
            .ToString("F1", CultureInfo.InvariantCulture);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
