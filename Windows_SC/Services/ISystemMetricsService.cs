using System;

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

    void SetActive(bool isActive);
}
