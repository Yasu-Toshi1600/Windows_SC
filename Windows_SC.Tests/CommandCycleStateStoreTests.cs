using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.Tests;

[TestClass]
public sealed class CommandCycleStateStoreTests
{
    [TestMethod]
    public async Task RestartRestoresEachItemAndEditsResetPosition()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Windows_SC-cycle-test-" + Guid.NewGuid());
        string path = Path.Combine(directory, "state.json");
        try
        {
            Guid first = Guid.NewGuid(), second = Guid.NewGuid();
            CommandCycleStepDefinition[] steps = [new() { DisplayName = "A" }, new() { DisplayName = "B" }];
            CommandCycleStateStore store = new(path, _ => { });
            await store.SaveNextAsync(first, steps, 1);
            await store.SaveNextAsync(second, steps, 0);
            CommandCycleStateStore restarted = new(path, _ => { });
            Assert.AreEqual(1, restarted.GetNext(first, steps));
            Assert.AreEqual(0, restarted.GetNext(second, steps));
            Array.Reverse(steps);
            Assert.AreEqual(0, restarted.GetNext(first, steps));
            Assert.IsFalse(File.ReadAllText(path).Contains("DisplayName"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(2)]
    public async Task OutOfRangeIndexInStateFileRestartsAtFirstStep(int invalidIndex)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Windows_SC-cycle-test-" + Guid.NewGuid());
        string path = Path.Combine(directory, "state.json");
        try
        {
            CommandCycleStepDefinition[] steps = [new(), new()];
            Guid id = Guid.NewGuid();
            CommandCycleStateStore store = new(path, _ => { });
            await store.SaveNextAsync(id, steps, 1);
            JsonNode state = JsonNode.Parse(File.ReadAllText(path))!;
            state[id.ToString()]!["NextIndex"] = invalidIndex;
            File.WriteAllText(path, state.ToJsonString());

            CommandCycleStateStore restarted = new(path, _ => { });
            Assert.AreEqual(0, restarted.GetNext(id, steps));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task SaveFailureIsReported()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Windows_SC-cycle-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            bool failed = false;
            CommandCycleStateStore store = new(directory, _ => failed = true);
            CommandCycleStepDefinition[] steps = [new(), new()];
            Guid id = Guid.NewGuid();
            await store.SaveNextAsync(id, steps, 1);
            Assert.IsTrue(failed);
        }
        finally
        {
            if (File.Exists(directory + ".tmp")) File.Delete(directory + ".tmp");
            Directory.Delete(directory);
        }
    }
}
