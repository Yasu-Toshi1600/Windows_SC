using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using Windows_SC.Services;

namespace Windows_SC.Tests;

[TestClass]
public sealed class LauncherStateTests
{
    [TestMethod]
    public void AudioOutputCycleSelector_ReturnsNextAvailableRegisteredDevice()
    {
        string[] orderedIds = ["device-a", "device-b", "device-c"];
        Dictionary<string, AudioOutputDevice> available = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["device-a"] = new("device-a", "A", true),
            ["device-c"] = new("device-c", "C", true)
        };

        AudioOutputDevice? result = AudioOutputCycleSelector.FindNext(
            orderedIds,
            available,
            "device-a");

        Assert.AreEqual("device-c", result?.Id);
    }

    [TestMethod]
    public void AudioOutputCycleSelector_WrapsToFirstDevice()
    {
        string[] orderedIds = ["device-a", "device-b"];
        Dictionary<string, AudioOutputDevice> available = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["device-a"] = new("device-a", "A", true),
            ["device-b"] = new("device-b", "B", true)
        };

        AudioOutputDevice? result = AudioOutputCycleSelector.FindNext(
            orderedIds,
            available,
            "device-b");

        Assert.AreEqual("device-a", result?.Id);
    }

    [TestMethod]
    public void SystemMonitorHistory_ClampsAndLimitsSamples()
    {
        SystemMonitorHistory history = new(2);
        history.Add(-10);
        history.Add(50);
        history.Add(120);

        IReadOnlyList<MonitorGraphPoint> points = history.CreateGraphPoints();

        Assert.AreEqual(2, points.Count);
        Assert.AreEqual(20d, points[0].Y, 0.001);
        Assert.AreEqual(0d, points[1].Y, 0.001);
    }

    [TestMethod]
    public void SystemMonitorHistory_ClearsWhenMetricIsUnavailable()
    {
        SystemMonitorHistory history = new();
        history.Add(25);
        history.Add(null);

        Assert.AreEqual(0, history.CreateGraphPoints().Count);
    }
}
