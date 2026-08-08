using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows_SC.Services;

namespace Windows_SC.Tests;

[TestClass]
public sealed class SequentialAsyncQueueTests
{
    [TestMethod]
    public async Task EnqueueWhileBusy_PreservesEveryItemInOrder()
    {
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> processed = [];
        SequentialAsyncQueue<string> queue = new(async item =>
        {
            processed.Add(item);
            if (processed.Count == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
        });

        queue.Enqueue("first");
        await firstStarted.Task;
        queue.Enqueue("second");
        queue.Enqueue("third");
        releaseFirst.SetResult();
        await queue.WaitForIdleAsync();

        CollectionAssert.AreEqual(new[] { "first", "second", "third" }, processed);
    }

    [TestMethod]
    public async Task FailedItem_DoesNotDropFollowingItem()
    {
        List<string> processed = [];
        List<string> errors = [];
        SequentialAsyncQueue<string> queue = new(
            item =>
            {
                processed.Add(item);
                return item == "first"
                    ? Task.FromException(new System.InvalidOperationException("failed"))
                    : Task.CompletedTask;
            },
            exception => errors.Add(exception.Message));

        queue.Enqueue("first");
        queue.Enqueue("second");
        await queue.WaitForIdleAsync();

        CollectionAssert.AreEqual(new[] { "first", "second" }, processed);
        CollectionAssert.AreEqual(new[] { "failed" }, errors);
    }
}
