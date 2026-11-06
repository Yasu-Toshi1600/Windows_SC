using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows_SC.Services;

namespace Windows_SC.Tests;

[TestClass]
public sealed class SettingsPersistenceTests
{
    [TestMethod]
    public void EditDuringSave_RemainsDirtyUntilLatestRevisionIsSaved()
    {
        SettingsEditRevisionTracker tracker = new();
        tracker.MarkChanged();
        long savingRevision = tracker.CurrentRevision;

        tracker.MarkChanged();
        tracker.MarkSaved(savingRevision);

        Assert.IsTrue(tracker.IsDirty);
        tracker.MarkSaved(tracker.CurrentRevision);
        Assert.IsFalse(tracker.IsDirty);
    }

    [TestMethod]
    public async Task ConcurrentPersistenceOperations_RunInRequestOrder()
    {
        SettingsPersistenceCoordinator coordinator = new();
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> order = [];

        Task first = coordinator.RunAsync(async () =>
        {
            order.Add(1);
            firstStarted.SetResult();
            await releaseFirst.Task;
            order.Add(2);
        });
        await firstStarted.Task;

        Task second = coordinator.RunAsync(() =>
        {
            order.Add(3);
            return Task.CompletedTask;
        });
        releaseFirst.SetResult();

        await Task.WhenAll(first, second);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, order);
    }

    [TestMethod]
    public async Task FailedSaveDoesNotBlockNextSaveOrShutdownWait()
    {
        SettingsPersistenceCoordinator coordinator = new();
        await Assert.ThrowsExactlyAsync<System.IO.IOException>(() =>
            coordinator.RunAsync(() => throw new System.IO.IOException("Save failed")));

        bool nextSaveRan = false;
        await coordinator.RunAsync(() =>
        {
            nextSaveRan = true;
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(nextSaveRan);
    }

    [TestMethod]
    public async Task WaitForIdleAsync_WaitsForRunningAndQueuedOperations()
    {
        SettingsPersistenceCoordinator coordinator = new();
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task first = coordinator.RunAsync(async () =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
        });
        await firstStarted.Task;
        Task second = coordinator.RunAsync(() => Task.CompletedTask);
        Task waitForIdle = coordinator.WaitForIdleAsync();

        Assert.IsFalse(waitForIdle.IsCompleted);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second, waitForIdle);
    }
}
