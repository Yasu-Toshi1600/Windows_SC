using System;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.Tests;

[TestClass]
public sealed class ApplicationVolumeStateStoreTests
{
    [TestMethod]
    public void Store_PreservesDifferentValuesForEachOutputDevice()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "application-volume-state.json");
        ApplicationAudioTarget target = new(
            ApplicationAudioIdentifierKind.ExecutablePath,
            "c:\\apps\\player.exe",
            "Player");
        try
        {
            using (ApplicationVolumeStateStore store = new(path, _ => { }))
            {
                store.Set("endpoint-a", target, 30);
                store.Set("endpoint-b", target, 70);
                store.FlushAsync().GetAwaiter().GetResult();
            }

            using (ApplicationVolumeStateStore loaded = new(path, _ => { }))
            {
                Assert.IsTrue(loaded.TryGet("endpoint-a", target, out int first));
                Assert.IsTrue(loaded.TryGet("endpoint-b", target, out int second));
                Assert.AreEqual(30, first);
                Assert.AreEqual(70, second);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Store_ReadOfMissingCombination_DoesNotCreateEntry()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "application-volume-state.json");
        ApplicationAudioTarget target = new(
            ApplicationAudioIdentifierKind.ExecutablePath,
            "c:\\apps\\player.exe",
            "Player");
        try
        {
            using (ApplicationVolumeStateStore store = new(path, _ => { }))
            {
                Assert.IsFalse(store.TryGet("endpoint-new", target, out _));
                store.FlushAsync().GetAwaiter().GetResult();
            }

            JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.AreEqual(0, root["Entries"]!.AsArray().Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"Windows_SC.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
