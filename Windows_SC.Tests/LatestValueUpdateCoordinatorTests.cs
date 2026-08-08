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

    [TestMethod]
    public async Task ErrorHandlerFailure_DoesNotLeaveCoordinatorBusy()
    {
        List<int> applied = [];
        LatestValueUpdateCoordinator<int> coordinator = new(
            value =>
            {
                applied.Add(value);
                return value == 1
                    ? Task.FromException(new System.InvalidOperationException("apply failed"))
                    : Task.CompletedTask;
            },
            _ => throw new System.InvalidOperationException("report failed"));

        coordinator.Request(1);
        coordinator.Request(2);
        await coordinator.WaitForIdleAsync();

        CollectionAssert.AreEqual(new[] { 1, 2 }, applied);
        Assert.IsFalse(coordinator.IsProcessing);
    }

    [TestMethod]
    public async Task WaitForIdleBeforeFirstRequest_CompletesImmediately()
    {
        LatestValueUpdateCoordinator<int> coordinator = new(_ => Task.CompletedTask);

        await coordinator.WaitForIdleAsync().WaitAsync(System.TimeSpan.FromSeconds(1));

        Assert.IsFalse(coordinator.IsProcessing);
    }
}
