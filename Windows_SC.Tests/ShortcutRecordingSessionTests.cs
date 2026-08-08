using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using Windows_SC.Services;

namespace Windows_SC.Tests;

[TestClass]
public sealed class ShortcutRecordingSessionTests
{
    [TestMethod]
    public void Stop_ReleasesSuppressionBeforeTargetIsResolved()
    {
        List<bool> suppressionChanges = [];
        ShortcutRecordingSession session = new(suppressionChanges.Add);
        ShortcutRecordingTarget target = new(
            ShortcutRecordingTargetKind.LauncherItem,
            Guid.NewGuid());

        session.Begin(target);
        ShortcutRecordingTarget? captured = session.StopAndTakeTarget();

        Assert.AreEqual(target, captured);
        Assert.IsFalse(session.IsActive);
        CollectionAssert.AreEqual(new[] { true, false }, suppressionChanges);
    }

    [TestMethod]
    public void CancelWithoutActiveTarget_StillReleasesSuppression()
    {
        List<bool> suppressionChanges = [];
        ShortcutRecordingSession session = new(suppressionChanges.Add);

        session.Cancel();

        CollectionAssert.AreEqual(new[] { false }, suppressionChanges);
    }

    [TestMethod]
    public void BeginNewTarget_ReplacesPreviousTarget()
    {
        ShortcutRecordingSession session = new(_ => { });
        ShortcutRecordingTarget first = new(
            ShortcutRecordingTargetKind.LauncherItem,
            Guid.NewGuid());
        ShortcutRecordingTarget second = new(
            ShortcutRecordingTargetKind.MacroStep,
            Guid.NewGuid());

        session.Begin(first);
        session.Begin(second);

        Assert.AreEqual(second, session.ActiveTarget);
    }

    [TestMethod]
    public void Constructor_RejectsMissingSuppressionCallback()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ShortcutRecordingSession(null!));
    }
}
