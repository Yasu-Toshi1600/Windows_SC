using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows_SC.Services;

namespace Windows_SC.Tests;

[TestClass]
public sealed class BackgroundResourceLifetimeTests
{
    [TestMethod]
    public async Task Stop_DoesNotCloseResourceWhileNativeOperationIsRunning()
    {
        BackgroundResourceLifetime lifetime = new();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim unblock = new();
        using ManualResetEventSlim released = new();
        Task operation = Task.Run(() => lifetime.TryRun(() =>
        {
            entered.Set();
            unblock.Wait();
        }));
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(lifetime.Stop(released.Set, _ => released.Set(), TimeSpan.FromMilliseconds(30)));
            Assert.IsFalse(released.IsSet);
        }
        finally
        {
            unblock.Set();
            await operation;
            Assert.IsTrue(released.Wait(TimeSpan.FromSeconds(5)));
        }
        Assert.IsFalse(lifetime.TryRun(() => Assert.Fail("Operation ran after shutdown.")));
    }

    [TestMethod]
    public void Stop_BlockedReleaseReturnsWithinBudgetAndReleasesOnlyOnce()
    {
        BackgroundResourceLifetime lifetime = new();
        using ManualResetEventSlim unblock = new();
        using ManualResetEventSlim finished = new();
        int calls = 0;
        try
        {
            Assert.IsFalse(lifetime.Stop(() =>
            {
                Interlocked.Increment(ref calls);
                unblock.Wait();
                finished.Set();
            }, _ => finished.Set(), TimeSpan.FromMilliseconds(30)));
            Assert.IsFalse(lifetime.Stop(() => Interlocked.Increment(ref calls), _ => { }, TimeSpan.Zero));
        }
        finally
        {
            unblock.Set();
            Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(5)));
        }
        Assert.AreEqual(1, calls);
    }
}
