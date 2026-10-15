using System;
using System.Collections.Generic;
using Windows_SC.Models;

namespace Windows_SC.Services;


internal sealed record SystemMetricsSnapshot(
    double? CpuPercent,
    double? GpuPercent,
    double MemoryPercent,
    ulong UsedMemoryBytes,
    ulong TotalMemoryBytes);

internal interface ISystemMetricsService : IDisposable
{
    event EventHandler? MetricsChanged;

    SystemMetricsSnapshot GetCachedMetrics();

    IReadOnlyList<MonitorGraphPoint> GetGraphPoints(SystemMonitorMetric metric);

    SystemMonitorMetric RequestedMetrics { get; }

    void SetMetrics(SystemMonitorMetric metrics);
}
