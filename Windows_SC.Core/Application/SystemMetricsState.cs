using System.Collections.Generic;
using Windows_SC.Models;

namespace Windows_SC.Services;

// Sampling owns the history; displaying the same sample never adds another point.
internal sealed class SystemMetricsState
{
    private readonly object _gate = new();
    private readonly SystemMonitorHistory _cpu = new();
    private readonly SystemMonitorHistory _gpu = new();
    private readonly SystemMonitorHistory _memory = new();
    private SystemMonitorMetric _requestedMetrics;
    private SystemMetricsSnapshot _cached = new(null, null, 0, 0, 0);
    private long _generation;

    public (SystemMonitorMetric Metrics, long Generation) Request
    {
        get { lock (_gate) return (_requestedMetrics, _generation); }
    }

    public bool Configure(SystemMonitorMetric metrics)
    {
        metrics &= SystemMonitorMetric.Cpu | SystemMonitorMetric.Gpu | SystemMonitorMetric.Memory;
        lock (_gate)
        {
            if (_requestedMetrics == metrics) return false;
            _requestedMetrics = metrics;
            _generation++;
            _cpu.Clear();
            _gpu.Clear();
            _memory.Clear();
            _cached = new(null, null, 0, 0, 0);
            return true;
        }
    }

    public bool Publish(SystemMetricsSnapshot sample, long generation)
    {
        lock (_gate)
        {
            // A native call can finish after settings changed (including A -> B -> A).
            if (_requestedMetrics == SystemMonitorMetric.None || generation != _generation) return false;
            bool cpu = (_requestedMetrics & SystemMonitorMetric.Cpu) != 0;
            bool gpu = (_requestedMetrics & SystemMonitorMetric.Gpu) != 0;
            bool memory = (_requestedMetrics & SystemMonitorMetric.Memory) != 0;
            _cached = new(cpu ? sample.CpuPercent : null, gpu ? sample.GpuPercent : null,
                memory ? sample.MemoryPercent : 0, memory ? sample.UsedMemoryBytes : 0,
                memory ? sample.TotalMemoryBytes : 0);
            _cpu.Add(_cached.CpuPercent);
            _gpu.Add(_cached.GpuPercent);
            _memory.Add(_cached.TotalMemoryBytes == 0 ? null : _cached.MemoryPercent);
            return true;
        }
    }

    public SystemMetricsSnapshot GetCachedMetrics()
    {
        lock (_gate) return _cached;
    }

    public IReadOnlyList<MonitorGraphPoint> GetGraphPoints(SystemMonitorMetric metric)
    {
        lock (_gate)
        {
            return metric switch
            {
                SystemMonitorMetric.Cpu => _cpu.CreateGraphPoints(),
                SystemMonitorMetric.Gpu => _gpu.CreateGraphPoints(),
                SystemMonitorMetric.Memory => _memory.CreateGraphPoints(),
                _ => System.Array.Empty<MonitorGraphPoint>()
            };
        }
    }
}
