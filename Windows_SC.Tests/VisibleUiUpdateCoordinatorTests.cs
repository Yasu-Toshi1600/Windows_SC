using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows_SC.Services;

namespace Windows_SC.Tests;

[TestClass]
public sealed class VisibleUiUpdateCoordinatorTests
{
    [TestMethod]
    public void HiddenSampling_DoesNotQueueUiWork_ShowRefreshesLatestImmediately()
    {
        TestDispatcher dispatcher = new();
        int latest = 0, displayed = -1;
        using VisibleUiUpdateCoordinator updates = new(dispatcher, () => displayed = latest);
        for (int index = 0; index < 5000; index++)
        {
            latest = index;
            updates.RequestUpdate();
        }
        Assert.AreEqual(0, dispatcher.Attempts);
        Assert.AreEqual(-1, displayed);
        updates.SetVisible(true);
        Assert.AreEqual(4999, displayed);
        Assert.AreEqual(0, dispatcher.Pending.Count);
    }

    [TestMethod]
    public void SlowUi_KeepsOnePendingRefresh_AndReadsLatestValue()
    {
        TestDispatcher dispatcher = new();
        int latest = 0, displayed = -1;
        using VisibleUiUpdateCoordinator updates = new(dispatcher, () => displayed = latest);
        updates.SetVisible(true);
        for (int index = 1; index <= 5000; index++)
        {
            latest = index;
            updates.RequestUpdate();
        }
        Assert.AreEqual(1, dispatcher.Attempts);
        Assert.AreEqual(1, dispatcher.Pending.Count);
        dispatcher.RunNext();
        Assert.AreEqual(5000, displayed);
        latest = 5001;
        updates.RequestUpdate();
        dispatcher.RunNext();
        Assert.AreEqual(5001, displayed);
    }

    [TestMethod]
    public void HideBeforeDispatch_SkipsStaleRefresh_ReopenReadsCurrentCache()
    {
        TestDispatcher dispatcher = new();
        int refreshes = 0;
        using VisibleUiUpdateCoordinator updates = new(dispatcher, () => refreshes++);
        updates.SetVisible(true);
        updates.RequestUpdate();
        updates.SetVisible(false);
        dispatcher.RunNext();
        Assert.AreEqual(1, refreshes);
        updates.RequestUpdate();
        Assert.AreEqual(0, dispatcher.Pending.Count);
        updates.SetVisible(true);
        Assert.AreEqual(2, refreshes);
        updates.SetVisible(true);
        Assert.AreEqual(2, refreshes);
    }

    [TestMethod]
    public void Dispose_SuppressesPendingAndFutureRefreshes()
    {
        TestDispatcher dispatcher = new();
        int refreshes = 0;
        VisibleUiUpdateCoordinator updates = new(dispatcher, () => refreshes++);
        updates.SetVisible(true);
        updates.RequestUpdate();
        updates.Dispose();
        dispatcher.RunNext();
        updates.SetVisible(true);
        updates.RequestUpdate();
        Assert.AreEqual(1, refreshes);
        Assert.AreEqual(0, dispatcher.Pending.Count);
    }

    [TestMethod]
    public void RejectedDispatch_DoesNotStrandFutureUpdates()
    {
        TestDispatcher dispatcher = new() { Accept = false };
        int refreshes = 0;
        using VisibleUiUpdateCoordinator updates = new(dispatcher, () => refreshes++);
        updates.SetVisible(true);
        updates.RequestUpdate();
        dispatcher.Accept = true;
        updates.RequestUpdate();
        dispatcher.RunNext();
        Assert.AreEqual(2, dispatcher.Attempts);
        Assert.AreEqual(2, refreshes);
    }

    private sealed class TestDispatcher : IUiDispatcher
    {
        public bool Accept { get; set; } = true;
        public int Attempts { get; private set; }
        public Queue<Action> Pending { get; } = new();

        public bool TryEnqueue(Action callback)
        {
            Attempts++;
            if (Accept) Pending.Enqueue(callback);
            return Accept;
        }

        public void RunNext() => Pending.Dequeue()();
    }
}
