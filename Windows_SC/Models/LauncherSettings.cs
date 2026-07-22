using System;
using System.Collections.Generic;
using System.Linq;

namespace Windows_SC.Models;

internal sealed class LauncherSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool AssumePhonePanelVisible { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public DateTimeOffset? DetailedLoggingExpiresAtUtc { get; set; }

    public LauncherLayoutMode LayoutMode { get; set; } = LauncherLayoutMode.Standard;

    public List<LauncherPageDefinition> Pages { get; set; } = [];

    public static LauncherSettings CreateDefault() => new()
    {
        Pages =
        [
            new LauncherPageDefinition
            {
                Id = Guid.NewGuid(),
                Name = "メイン",
                Items =
                [
                    LauncherItemDefinition.CreateButton("メモ帳", "notepad.exe"),
                    LauncherItemDefinition.CreateButton("電卓", "calc.exe"),
                    LauncherItemDefinition.CreateButton("エクスプローラー", "explorer.exe"),
                    LauncherItemDefinition.CreateButton("Windows 設定", "ms-settings:")
                ]
            }
        ]
    };
}

internal sealed class LauncherPageDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public List<LauncherItemDefinition> Items { get; set; } = [];
}

internal sealed class LauncherItemDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public LauncherItemKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public LauncherPostExecutionBehavior PostExecutionBehavior { get; set; } =
        LauncherPostExecutionBehavior.CloseOnSuccess;

    public LauncherActionDefinition? Action { get; set; }

    public AudioDeviceToggleDefinition? AudioDeviceToggle { get; set; }

    public CycleActionDefinition? CycleAction { get; set; }

    public VolumeSliderDefinition? VolumeSlider { get; set; }

    public static LauncherItemDefinition CreateButton(string title, string target) => new()
    {
        Kind = LauncherItemKind.Button,
        Title = title,
        Action = new LauncherActionDefinition
        {
            Target = target
        }
    };

    public CycleActionDefinition? GetEffectiveCycleAction()
    {
        if (CycleAction is not null)
        {
            return CycleAction;
        }

        if (AudioDeviceToggle is null)
        {
            return null;
        }

        return new CycleActionDefinition
        {
            Kind = CycleActionKind.AudioOutput,
            AudioDeviceIds = AudioDeviceToggle.GetOrderedDeviceIds().ToList()
        };
    }
}

internal enum LauncherItemKind
{
    Button,
    Toggle,
    Slider
}

internal sealed class LauncherActionDefinition
{
    public LauncherActionKind Kind { get; set; } = LauncherActionKind.Application;

    public string Target { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public bool HideCommandWindow { get; set; } = true;
}

internal enum LauncherActionKind
{
    Application,
    File,
    Url,
    Command,
    BatchFile
}

internal sealed class AudioDeviceToggleDefinition
{
    public List<string> DeviceIds { get; set; } = [];

    // Schema v1 compatibility for settings created before multi-device cycling.
    public string FirstDeviceId { get; set; } = string.Empty;

    public string SecondDeviceId { get; set; } = string.Empty;

    public IReadOnlyList<string> GetOrderedDeviceIds()
    {
        if (DeviceIds is { Count: > 0 })
        {
            return DeviceIds;
        }

        List<string> legacyDeviceIds = [];
        if (!string.IsNullOrWhiteSpace(FirstDeviceId))
        {
            legacyDeviceIds.Add(FirstDeviceId);
        }

        if (!string.IsNullOrWhiteSpace(SecondDeviceId)
            && !legacyDeviceIds.Contains(SecondDeviceId, StringComparer.OrdinalIgnoreCase))
        {
            legacyDeviceIds.Add(SecondDeviceId);
        }

        return legacyDeviceIds;
    }
}

internal enum LauncherPostExecutionBehavior
{
    CloseOnSuccess,
    KeepOpen
}

internal enum LauncherLayoutMode
{
    Standard,
    Compact
}

internal sealed class CycleActionDefinition
{
    public CycleActionKind Kind { get; set; } = CycleActionKind.AudioOutput;

    public bool RetryFailedCommand { get; set; } = true;

    public List<string> AudioDeviceIds { get; set; } = [];

    public List<CommandCycleStepDefinition> CommandSteps { get; set; } = [];
}

internal enum CycleActionKind
{
    AudioOutput,
    Commands
}

internal sealed class CommandCycleStepDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string DisplayName { get; set; } = string.Empty;

    public LauncherActionDefinition Action { get; set; } = new()
    {
        Kind = LauncherActionKind.Command
    };
}

internal sealed class VolumeSliderDefinition
{
    public double Minimum { get; set; }

    public double Maximum { get; set; } = 100;
}
