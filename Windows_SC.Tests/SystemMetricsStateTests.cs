using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.Tests;

[TestClass]
public sealed class SystemMetricsStateTests
{
    [TestMethod]
    public void CpuAndMemorySelection_ExcludesGpuAndKeepsOnlyRequestedValues()
    {
        SystemMetricsState state = new();
        Assert.IsTrue(state.Configure(SystemMonitorMetric.Cpu | SystemMonitorMetric.Memory));
        Assert.AreEqual(SystemMonitorMetric.Cpu | SystemMonitorMetric.Memory, state.Request.Metrics);
        Assert.IsTrue(state.Publish(new(25, 90, 50, 100, 200), state.Request.Generation));
        Assert.AreEqual(25d, state.GetCachedMetrics().CpuPercent);
        Assert.IsNull(state.GetCachedMetrics().GpuPercent);
        Assert.AreEqual(0, state.GetGraphPoints(SystemMonitorMetric.Gpu).Count);
        Assert.AreEqual(1, state.GetGraphPoints(SystemMonitorMetric.Cpu).Count);
        Assert.AreEqual(1, state.GetGraphPoints(SystemMonitorMetric.Memory).Count);
    }

    [TestMethod]
    public void History_ContinuesWithoutUiReads_AndDisplayDoesNotAppendSamples()
    {
        SystemMetricsState state = new();
        state.Configure(SystemMonitorMetric.Cpu);
        long generation = state.Request.Generation;
        // Simulate hidden sampling for twenty minutes without a UI refresh.
        for (int index = 0; index < 1200; index++)
            Assert.IsTrue(state.Publish(new(index % 100, null, 0, 0, 0), generation));
        var points = state.GetGraphPoints(SystemMonitorMetric.Cpu);
        Assert.AreEqual(60, points.Count);
        Assert.AreEqual(0d, points[0].X, 0.001);
        Assert.AreEqual(100d, points[59].X, 0.001);
        Assert.AreEqual(0.4d, points[59].Y, 0.001);
        for (int index = 0; index < 20; index++)
            CollectionAssert.AreEqual(new System.Collections.Generic.List<MonitorGraphPoint>(points),
                new System.Collections.Generic.List<MonitorGraphPoint>(state.GetGraphPoints(SystemMonitorMetric.Cpu)));
    }

    [TestMethod]
    public void MissingSample_ClearsHistory_AndNextValueStartsOnRight()
    {
        SystemMetricsState state = new();
        state.Configure(SystemMonitorMetric.Gpu);
        long generation = state.Request.Generation;
        state.Publish(new(null, 25, 0, 0, 0), generation);
        state.Publish(new(null, null, 0, 0, 0), generation);
        Assert.AreEqual(0, state.GetGraphPoints(SystemMonitorMetric.Gpu).Count);
        state.Publish(new(null, 50, 0, 0, 0), generation);
        Assert.AreEqual(1, state.GetGraphPoints(SystemMonitorMetric.Gpu).Count);
        Assert.AreEqual(100d, state.GetGraphPoints(SystemMonitorMetric.Gpu)[0].X, 0.001);
    }

    [TestMethod]
    public void Reconfiguration_DropsInFlightResults_EvenWhenSelectionReturnsToOriginal()
    {
        SystemMetricsState state = new();
        state.Configure(SystemMonitorMetric.Gpu);
        long oldGeneration = state.Request.Generation;
        state.Publish(new(null, 80, 0, 0, 0), oldGeneration);
        state.Configure(SystemMonitorMetric.Cpu);
        Assert.IsNull(state.GetCachedMetrics().GpuPercent);
        Assert.AreEqual(0, state.GetGraphPoints(SystemMonitorMetric.Gpu).Count);
        Assert.IsFalse(state.Publish(new(null, 90, 0, 0, 0), oldGeneration));
        state.Configure(SystemMonitorMetric.Gpu);
        Assert.IsFalse(state.Publish(new(null, 90, 0, 0, 0), oldGeneration));
        Assert.IsTrue(state.Publish(new(null, 10, 0, 0, 0), state.Request.Generation));
        Assert.AreEqual(10d, state.GetCachedMetrics().GpuPercent);
    }

    [TestMethod]
    public void SameSelection_PreservesHistory_DisablingClearsAndRejectsSamples()
    {
        SystemMetricsState state = new();
        Assert.AreEqual(SystemMonitorMetric.None, state.Request.Metrics);
        state.Configure(SystemMonitorMetric.Memory);
        long generation = state.Request.Generation;
        state.Publish(new(null, null, 50, 100, 200), generation);
        Assert.IsFalse(state.Configure(SystemMonitorMetric.Memory));
        Assert.AreEqual(generation, state.Request.Generation);
        Assert.AreEqual(1, state.GetGraphPoints(SystemMonitorMetric.Memory).Count);
        state.Configure(SystemMonitorMetric.None);
        Assert.IsFalse(state.Publish(new(null, null, 50, 100, 200), generation));
        Assert.IsFalse(state.Publish(new(null, null, 50, 100, 200), state.Request.Generation));
        Assert.AreEqual(0, state.GetGraphPoints(SystemMonitorMetric.Memory).Count);
        Assert.AreEqual(0UL, state.GetCachedMetrics().TotalMemoryBytes);
    }
}
