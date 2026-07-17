using System;
using System.Collections.Generic;

namespace Windows_SC.Models;

internal sealed class LauncherSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool AssumePhonePanelVisible { get; set; } = true;

    public bool StartWithWindows { get; set; }

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
                    LauncherItemDefinition.CreateButton("ショートカット 1"),
                    LauncherItemDefinition.CreateButton("ショートカット 2"),
                    LauncherItemDefinition.CreateButton("ショートカット 3"),
                    LauncherItemDefinition.CreateButton("ショートカット 4")
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

    public LauncherActionDefinition? Action { get; set; }

    public AudioDeviceToggleDefinition? AudioDeviceToggle { get; set; }

    public VolumeSliderDefinition? VolumeSlider { get; set; }

    public static LauncherItemDefinition CreateButton(string title) => new()
    {
        Kind = LauncherItemKind.Button,
        Title = title,
        Action = new LauncherActionDefinition()
    };
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
    public string FirstDeviceId { get; set; } = string.Empty;

    public string SecondDeviceId { get; set; } = string.Empty;
}

internal sealed class VolumeSliderDefinition
{
    public double Minimum { get; set; }

    public double Maximum { get; set; } = 100;
}
