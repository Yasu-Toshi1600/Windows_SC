using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.Tests;

[TestClass]
public sealed class CoreValidationTests
{
    [TestMethod]
    public void Validate_RejectsUnknownLauncherItemBackgroundColor()
    {
        LauncherSettings settings = LauncherSettings.CreateDefault();
        settings.Pages[0].Items[0].BackgroundColor = (LauncherItemBackgroundColor)999;

        IReadOnlyList<string> errors = LauncherSettingsValidator.Validate(settings);

        Assert.IsTrue(errors.Any(error => error.Contains("背景色", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Validate_RejectsUnsupportedSchemaLayoutAndMissingPages()
    {
        LauncherSettings settings = new()
        {
            SchemaVersion = 99,
            LayoutMode = (LauncherLayoutMode)99,
            Pages = []
        };

        IReadOnlyList<string> errors = LauncherSettingsValidator.Validate(settings);

        Assert.IsTrue(errors.Any(error => error.Contains("スキーマ", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(error => error.Contains("レイアウト", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(error => error.Contains("ページ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Validate_RejectsDuplicatePageAndItemIds()
    {
        Guid pageId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        LauncherSettings settings = new()
        {
            Pages =
            [
                CreatePage(pageId, itemId),
                CreatePage(pageId, itemId)
            ]
        };

        IReadOnlyList<string> errors = LauncherSettingsValidator.Validate(settings);

        Assert.IsTrue(errors.Any(error => error.Contains("ページID", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(error => error.Contains("項目ID", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Validate_RejectsApplicationSliderWithoutTarget()
    {
        LauncherSettings settings = CreateSettings(new LauncherItemDefinition
        {
            Kind = LauncherItemKind.Slider,
            Title = "Application volume",
            VolumeSlider = new VolumeSliderDefinition
            {
                Type = VolumeSliderKind.Application,
                Minimum = 0,
                Maximum = 100
            }
        });

        IReadOnlyList<string> errors = LauncherSettingsValidator.Validate(settings);

        Assert.IsTrue(errors.Any(error => error.Contains("アプリ別音量", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(-1d, 100d)]
    [DataRow(0d, 101d)]
    [DataRow(50d, 50d)]
    [DataRow(double.NaN, 100d)]
    public void Validate_RejectsInvalidSliderRanges(double minimum, double maximum)
    {
        LauncherSettings settings = CreateSettings(new LauncherItemDefinition
        {
            Kind = LauncherItemKind.Slider,
            Title = "Volume",
            VolumeSlider = new VolumeSliderDefinition
            {
                Minimum = minimum,
                Maximum = maximum
            }
        });

        IReadOnlyList<string> errors = LauncherSettingsValidator.Validate(settings);

        Assert.IsTrue(errors.Any(error => error.Contains("範囲", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Validate_RejectsCaseInsensitiveDuplicateAudioDevices()
    {
        LauncherSettings settings = CreateSettings(new LauncherItemDefinition
        {
            Kind = LauncherItemKind.Toggle,
            Title = "Audio",
            CycleAction = new CycleActionDefinition
            {
                AudioDeviceIds = ["Device-A", "device-a"]
            }
        });

        IReadOnlyList<string> errors = LauncherSettingsValidator.Validate(settings);

        Assert.IsTrue(errors.Any(error => error.Contains("2台以上", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Validate_RejectsNonCommandActionInCommandCycle()
    {
        LauncherSettings settings = CreateSettings(new LauncherItemDefinition
        {
            Kind = LauncherItemKind.Toggle,
            Title = "Commands",
            CycleAction = new CycleActionDefinition
            {
                Kind = CycleActionKind.Commands,
                CommandSteps =
                [
                    CreateCommandStep("First", LauncherActionKind.Command),
                    CreateCommandStep("Second", LauncherActionKind.Application)
                ]
            }
        });

        IReadOnlyList<string> errors = LauncherSettingsValidator.Validate(settings);

        Assert.IsTrue(errors.Any(error => error.Contains("実行種類が不正", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Validate_RejectsMacroDelayOutsidePerStepAndTotalLimits()
    {
        LauncherSettings settings = CreateSettings(new LauncherItemDefinition
        {
            Kind = LauncherItemKind.Button,
            Title = "Macro",
            Action = new LauncherActionDefinition
            {
                Kind = LauncherActionKind.Macro,
                Macro = new MacroDefinition
                {
                    Steps = Enumerable.Range(0, 7)
                        .Select(index => new MacroStepDefinition
                        {
                            DisplayName = $"Wait {index}",
                            Kind = MacroStepKind.Wait,
                            DelayMilliseconds = index == 0 ? 10_001 : 10_000
                        })
                        .ToList()
                }
            }
        });

        IReadOnlyList<string> errors = LauncherSettingsValidator.Validate(settings);

        Assert.IsTrue(errors.Any(error => error.Contains("0～10000ms", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(error => error.Contains("合計待機時間", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Validate_AcceptsCompleteApplicationVolumeAndCommandCycleSettings()
    {
        LauncherSettings settings = LauncherSettings.CreateDefault();
        settings.Pages[0].Items.Add(new LauncherItemDefinition
        {
            Kind = LauncherItemKind.Slider,
            Title = "Application volume",
            VolumeSlider = new VolumeSliderDefinition
            {
                Type = VolumeSliderKind.Application,
                Minimum = 0,
                Maximum = 100,
                Application = new ApplicationAudioTargetDefinition
                {
                    IdentifierType = ApplicationAudioIdentifierKind.ExecutablePath,
                    Identifier = "c:\\apps\\player.exe",
                    DisplayName = "Player"
                }
            }
        });
        settings.Pages[0].Items.Add(new LauncherItemDefinition
        {
            Kind = LauncherItemKind.Toggle,
            Title = "Commands",
            CycleAction = new CycleActionDefinition
            {
                Kind = CycleActionKind.Commands,
                CommandSteps =
                [
                    CreateCommandStep("First", LauncherActionKind.Command),
                    CreateCommandStep("Second", LauncherActionKind.Command)
                ]
            }
        });

        Assert.AreEqual(0, LauncherSettingsValidator.Validate(settings).Count);
    }

    private static LauncherSettings CreateSettings(LauncherItemDefinition item) => new()
    {
        Pages =
        [
            new LauncherPageDefinition
            {
                Name = "Main",
                Items = [item]
            }
        ]
    };

    private static LauncherPageDefinition CreatePage(Guid pageId, Guid itemId) => new()
    {
        Id = pageId,
        Name = "Main",
        Items =
        [
            new LauncherItemDefinition
            {
                Id = itemId,
                Kind = LauncherItemKind.Button,
                Title = "App",
                Action = new LauncherActionDefinition { Target = "app.exe" }
            }
        ]
    };

    private static CommandCycleStepDefinition CreateCommandStep(
        string displayName,
        LauncherActionKind kind) => new()
    {
        DisplayName = displayName,
        Action = new LauncherActionDefinition
        {
            Kind = kind,
            Target = "command.exe"
        }
    };
}
