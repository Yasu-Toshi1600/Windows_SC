using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;
using Windows_SC.Models;

namespace Windows_SC.Tests;

[TestClass]
public sealed class AudioDeviceNamePersistenceTests
{
    [TestMethod]
    public void LegacyCycleWithoutNamesKeepsDeviceOrder()
    {
        CycleActionDefinition cycle = JsonSerializer.Deserialize<CycleActionDefinition>(
            "{\"AudioDeviceIds\":[\"second\",\"first\"]}")!;
        CollectionAssert.AreEqual(new[] { "second", "first" }, cycle.AudioDeviceIds);
        Assert.AreEqual(0, cycle.AudioDeviceNames.Count);
    }

    [TestMethod]
    public void DuplicateDisplayNamesRemainSeparateByIdAfterRoundTrip()
    {
        CycleActionDefinition cycle = new()
        {
            AudioDeviceIds = ["second", "first"],
            AudioDeviceNames = new() { ["first"] = "Speaker", ["second"] = "Speaker" }
        };
        CycleActionDefinition restored = JsonSerializer.Deserialize<CycleActionDefinition>(
            JsonSerializer.Serialize(cycle))!;
        CollectionAssert.AreEqual(cycle.AudioDeviceIds, restored.AudioDeviceIds);
        Assert.AreEqual(2, restored.AudioDeviceNames.Count);
        Assert.AreEqual("Speaker", restored.AudioDeviceNames["first"]);
        Assert.AreEqual("Speaker", restored.AudioDeviceNames["second"]);
    }
}
