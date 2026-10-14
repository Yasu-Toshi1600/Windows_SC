using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows_SC.Services;

namespace Windows_SC.Tests;

[TestClass]
public sealed class MemoryFlightTests
{
    private const long MiB = 1024 * 1024;

    [TestMethod]
    public void DetectsDropsAndRisesButSuppressesRepeatedTriggers()
    {
        MemoryChangeDetector detector = new();
        Assert.IsNull(detector.Observe(0, 200 * MiB));
        Assert.AreEqual(-40 * MiB, detector.Observe(1000, 160 * MiB)!.Value.DeltaBytes);
        Assert.IsNull(detector.Observe(2000, 200 * MiB));
        for (int second = 3; second <= 60; second++) detector.Observe(second * 1000, 200 * MiB);
        Assert.AreEqual(32 * MiB, detector.Observe(61_000, 232 * MiB)!.Value.DeltaBytes);
    }

    [TestMethod]
    public void DetectsCumulativeChangeWithinThirtySeconds()
    {
        MemoryChangeDetector detector = new();
        for (int second = 0; second < 16; second++)
            Assert.IsNull(detector.Observe(second * 1000, (200 + second) * MiB));
        MemoryChange change = detector.Observe(16_000, 216 * MiB)!.Value;
        Assert.AreEqual(0L, change.BaselineMilliseconds);
        Assert.AreEqual(16 * MiB, change.DeltaBytes);
    }

    [TestMethod]
    public void RelativeThresholdHasFourMiBFloor()
    {
        MemoryChangeDetector detector = new();
        detector.Observe(0, 10 * MiB);
        Assert.IsNull(detector.Observe(1000, 13 * MiB));
        Assert.IsNotNull(detector.Observe(2000, 14 * MiB));
    }

    [TestMethod]
    public void MissingSamplesAndResumeDoNotCreateFalseSuddenChanges()
    {
        MemoryChangeDetector detector = new();
        detector.Observe(0, 200 * MiB);
        Assert.IsNull(detector.Observe(1000, null));
        Assert.IsNull(detector.Observe(2000, 100 * MiB));
        Assert.IsNull(detector.Observe(60_000, 200 * MiB));
        Assert.IsNull(detector.Observe(50_000, 100 * MiB));
    }

    [TestMethod]
    public void HistoryRemainsBoundedDuringLongRun()
    {
        MemoryChangeDetector detector = new();
        for (int second = 0; second < 100_000; second++)
            Assert.IsNull(detector.Observe(second * 1000L, 200 * MiB));
        Assert.AreEqual(31, detector.HistoryCount);
    }

    [TestMethod]
    public void CsvRotatesWhileRunningAndRetainsHeadersAndSizeBound()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WindowsSC-MemoryFlight-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "samples.csv");
        try
        {
            using (RotatingCsvWriter writer = new(path, "a,b", maximumBytes: 32, fileCount: 3))
                for (int index = 0; index < 100; index++) writer.WriteLine($"{index},value");
            string[] files = Directory.GetFiles(directory);
            Assert.AreEqual(3, files.Length);
            Assert.IsTrue(files.All(file => new FileInfo(file).Length <= 32));
            Assert.IsTrue(files.All(file => File.ReadLines(file).First() == "a,b"));
            StringAssert.Contains(File.ReadAllText(path), "99,value");
            using (RotatingCsvWriter writer = new(path, "a,b", maximumBytes: 32, fileCount: 3))
                writer.WriteLine("new,run");
            StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "samples.1.csv")), "99,value");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
