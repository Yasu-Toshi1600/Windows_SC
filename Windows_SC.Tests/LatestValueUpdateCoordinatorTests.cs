using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows_SC.Services;

namespace Windows_SC.Tests;

[TestClass]
public sealed class LatestValueUpdateCoordinatorTests
{
    [TestMethod]
    public async Task RequestsWhileBusy_ApplyFirstAndLatestValues()
    {
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> appliedValues = [];
        LatestValueUpdateCoordinator<int> coordinator = new(async value =>
        {
            appliedValues.Add(value);
            if (appliedValues.Count == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
        });

        coordinator.Request(10);
        await firstStarted.Task;
        coordinator.Request(20);
        coordinator.Request(30);
        releaseFirst.SetResult();
        await coordinator.WaitForIdleAsync();

        CollectionAssert.AreEqual(new[] { 10, 30 }, appliedValues);
        Assert.IsFalse(coordinator.IsProcessing);
    }

    [TestMethod]
    public async Task FailedRequest_DoesNotBlockLatestValue()
    {
        List<int> appliedValues = [];
        List<string> errors = [];
        LatestValueUpdateCoordinator<int> coordinator = new(
            value =>
            {
                appliedValues.Add(value);
                return value == 10
                    ? Task.FromException(new System.InvalidOperationException("failed"))
                    : Task.CompletedTask;
            },
            exception => errors.Add(exception.Message));

        coordinator.Request(10);
        coordinator.Request(20);
        await coordinator.WaitForIdleAsync();

        CollectionAssert.AreEqual(new[] { 10, 20 }, appliedValues);
        CollectionAssert.AreEqual(new[] { "failed" }, errors);
    }
}
