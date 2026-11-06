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
}
