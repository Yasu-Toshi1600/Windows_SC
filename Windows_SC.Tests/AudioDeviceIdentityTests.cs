using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows_SC.Services;
using Windows_SC.Models;
using System.Text.Json;

namespace Windows_SC.Tests;

[TestClass]
public sealed class AudioDeviceIdentityTests
{
    [TestMethod]
    public void ChangedEndpointResolvesByExactStableId()
    {
        AudioOutputDevice device = new("new", "Renamed", true, "Opaque#Aa");
        Assert.AreEqual(device, AudioDeviceIdentityResolver.Resolve("old", "Opaque#Aa", [device]));
    }

    [TestMethod]
    public void MissingPropertyFallsBackButConflictingIdentityDoesNot()
    {
        Assert.IsNotNull(AudioDeviceIdentityResolver.Resolve("ID", "stable", [new("id", "A", true)]));
        Assert.IsNull(AudioDeviceIdentityResolver.Resolve("ID", "stable", [new("id", "A", true, "other")]));
        Assert.IsNull(AudioDeviceIdentityResolver.Resolve("old", "stable", [new("new", "A", true)]));
    }

    [TestMethod]
    public void AmbiguousStableIdentityIsNotChosen()
    {
        Assert.IsNull(AudioDeviceIdentityResolver.Resolve("one", "s",
            [new("one", "A", true, "s"), new("two", "A", true, "s")]));
    }

    [TestMethod]
    public void ResolutionPreservesOrderAndDeduplicatesPhysicalEndpoint()
    {
        var ids = AudioDeviceIdentityResolver.ResolveIds(["old", "b", "new"],
            new System.Collections.Generic.Dictionary<string, string> { ["old"] = "s" },
            [new("new", "A", true, "s"), new("b", "B", true)]);
        CollectionAssert.AreEqual(new[] { "new", "b" }, new System.Collections.Generic.List<string>(ids));
    }

    [TestMethod]
    public void LegacySettingsWithoutStableIdsRemainReadable()
    {
        CycleActionDefinition cycle = JsonSerializer.Deserialize<CycleActionDefinition>(
            """{ "AudioDeviceIds": ["second", "first"] }""")!;
        Assert.AreEqual(0, cycle.AudioDeviceStableIds.Count);
        CollectionAssert.AreEqual(new[] { "second", "first" }, cycle.AudioDeviceIds);
        CollectionAssert.AreEqual(new[] { "second", "first" },
            new System.Collections.Generic.List<string>(AudioDeviceIdentityResolver.ResolveIds(
                cycle.AudioDeviceIds, cycle.AudioDeviceStableIds,
                [new("first", "A", true), new("second", "B", true)])));
    }
}
