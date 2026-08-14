using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.Tests;

[TestClass]
public sealed class CoreModelAndUtilityTests
{
    [TestMethod]
    public void CreateDefault_ProducesValidOrderedApplicationButtons()
    {
        LauncherSettings settings = LauncherSettings.CreateDefault();

        Assert.AreEqual(LauncherSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.HasCount(1, settings.Pages);
        Assert.HasCount(4, settings.Pages[0].Items);
        Assert.IsTrue(settings.Pages[0].Items.All(item =>
            item.Id != Guid.Empty
            && item.Kind == LauncherItemKind.Button
            && item.Action?.Kind == LauncherActionKind.Application
            && !string.IsNullOrWhiteSpace(item.Action.Target)));
        Assert.AreEqual(0, LauncherSettingsValidator.Validate(settings).Count);
    }

    [TestMethod]
    public void AudioDeviceToggle_DeviceIdsTakePrecedenceOverLegacyFields()
    {
        AudioDeviceToggleDefinition toggle = new()
        {
            DeviceIds = ["new-b", "new-a"],
            FirstDeviceId = "legacy-a",
            SecondDeviceId = "legacy-b"
        };

        CollectionAssert.AreEqual(
            new[] { "new-b", "new-a" },
            toggle.GetOrderedDeviceIds().ToArray());
    }

    [TestMethod]
    public void AudioDeviceToggle_LegacyFieldsPreserveOrderAndRemoveCaseInsensitiveDuplicate()
    {
        AudioDeviceToggleDefinition toggle = new()
        {
            FirstDeviceId = "Device-A",
            SecondDeviceId = "device-a"
        };

        CollectionAssert.AreEqual(
            new[] { "Device-A" },
            toggle.GetOrderedDeviceIds().ToArray());
    }

    [TestMethod]
    public void GetEffectiveCycleAction_ConvertsLegacyAudioToggleWithoutMutatingIt()
    {
        LauncherItemDefinition item = new()
        {
            Kind = LauncherItemKind.Toggle,
            AudioDeviceToggle = new AudioDeviceToggleDefinition
            {
                FirstDeviceId = "device-a",
                SecondDeviceId = "device-b"
            }
        };

        CycleActionDefinition? cycle = item.GetEffectiveCycleAction();

        Assert.IsNotNull(cycle);
        Assert.AreEqual(CycleActionKind.AudioOutput, cycle.Kind);
        CollectionAssert.AreEqual(
            new[] { "device-a", "device-b" },
            cycle.AudioDeviceIds);
        Assert.IsNull(item.CycleAction);
    }

    [TestMethod]
    [DataRow("plain-device-id", "plain-device-id")]
    [DataRow("SWD\\MMDEVAPI#{endpoint-id}#{interface-id}", "{endpoint-id}")]
    [DataRow("swd\\mmdevapi#{endpoint-id}", "{endpoint-id}")]
    public void AudioDeviceId_NormalizeHandlesPersistedAndInterfaceForms(
        string input,
        string expected)
    {
        Assert.AreEqual(expected, AudioDeviceId.Normalize(input));
    }

    [TestMethod]
    public void ResultFactoriesSetSuccessAndFailureFieldsConsistently()
    {
        AudioOutputDevice device = new("id", "Device", true);

        Assert.IsTrue(ActionExecutionResult.Success.IsSuccess);
        Assert.AreEqual(string.Empty, ActionExecutionResult.Success.ErrorMessage);
        Assert.IsFalse(ActionExecutionResult.Failure("failed").IsSuccess);
        Assert.AreEqual("failed", ActionExecutionResult.Failure("failed").ErrorMessage);

        AudioDeviceCycleResult cycleSuccess = AudioDeviceCycleResult.Success(device);
        Assert.IsTrue(cycleSuccess.IsSuccess);
        Assert.AreSame(device, cycleSuccess.CurrentDevice);
        Assert.AreEqual(string.Empty, cycleSuccess.ErrorMessage);
        Assert.IsFalse(AudioDeviceCycleResult.Failure("failed").IsSuccess);

        AudioMasterVolumeResult masterSuccess = AudioMasterVolumeResult.Success(42.5, isMuted: true);
        Assert.IsTrue(masterSuccess.IsSuccess);
        Assert.AreEqual(42.5, masterSuccess.VolumePercent, 0.001);
        Assert.IsTrue(masterSuccess.IsMuted);
        Assert.AreEqual(0, AudioMasterVolumeResult.Failure("failed").VolumePercent);
        Assert.IsFalse(AudioMasterVolumeResult.Failure("failed").IsMuted);

        ApplicationVolumeResult applicationSuccess = ApplicationVolumeResult.Success(3);
        Assert.IsTrue(applicationSuccess.IsSuccess);
        Assert.AreEqual(3, applicationSuccess.SucceededSessionCount);
        Assert.AreEqual(0, applicationSuccess.FailedSessionCount);
        ApplicationVolumeResult applicationFailure =
            ApplicationVolumeResult.Failure("partial", 1, 2);
        Assert.IsFalse(applicationFailure.IsSuccess);
        Assert.AreEqual(1, applicationFailure.SucceededSessionCount);
        Assert.AreEqual(2, applicationFailure.FailedSessionCount);
    }

    [TestMethod]
    public void LogValue_NormalizeRemovesRecordBreakingCharacters()
    {
        Assert.AreEqual(
            "'quoted' line two  end",
            LogValue.Normalize("\"quoted\"\rline\ntwo\0 end"));
    }

    [TestMethod]
    public void LogPrivacySanitizer_RedactsUserAndKnownPathContent()
    {
        string userPath = LogPrivacySanitizer.Sanitize(
            "C:\\Users\\Alice\\Documents\\secret.txt\r\n");
        string knownPath = LogPrivacySanitizer.Sanitize(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) +
            "\\Windows_SC\\Logs\\diagnostic.log");

        Assert.IsFalse(userPath.Contains("Alice", StringComparison.Ordinal));
        Assert.IsFalse(userPath.Contains("secret.txt", StringComparison.Ordinal));
        Assert.IsFalse(knownPath.Contains("diagnostic.log", StringComparison.Ordinal));
        StringAssert.Contains(userPath, "<user>\\<redacted>");
        StringAssert.Contains(knownPath, "%LOCALAPPDATA%\\<redacted>");
        Assert.IsFalse(userPath.Contains('\r'));
        Assert.IsFalse(userPath.Contains('\n'));
    }

    [TestMethod]
    public async Task ShortcutCoordinator_AttachDetachUsesOnlyCurrentHandler()
    {
        ShortcutKeyExecutionCoordinator coordinator = new();
        List<string> calls = [];
        Func<CancellationToken, Task> first = _ =>
        {
            calls.Add("first");
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> second = _ =>
        {
            calls.Add("second");
            return Task.CompletedTask;
        };

        await coordinator.PrepareTargetAsync(CancellationToken.None);
        coordinator.Attach(first);
        coordinator.Attach(second);
        coordinator.Detach(first);
        await coordinator.PrepareTargetAsync(CancellationToken.None);
        coordinator.Detach(second);
        await coordinator.PrepareTargetAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "second" }, calls);
    }

    [TestMethod]
    public async Task ShortcutCoordinator_ForwardsCancellationToken()
    {
        ShortcutKeyExecutionCoordinator coordinator = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        coordinator.Attach(token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            coordinator.PrepareTargetAsync(cancellation.Token));
    }
}
