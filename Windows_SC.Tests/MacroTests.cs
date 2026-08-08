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
public sealed class MacroTests
{
    [TestMethod]
    public async Task ExecuteAsync_RunsStepsInRegistrationOrder()
    {
        RecordingActionService actionService = new();
        using MacroExecutionService service = new(actionService, _ => { });
        MacroDefinition macro = CreateMacro("first", "second", "third");

        ActionExecutionResult result = await service.ExecuteAsync(Guid.NewGuid(), macro);

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(
            new[] { "first", "second", "third" },
            actionService.Targets);
    }

    [TestMethod]
    public async Task ExecuteAsync_StopsAtFirstFailedStep()
    {
        RecordingActionService actionService = new(failingTarget: "second");
        using MacroExecutionService service = new(actionService, _ => { });
        MacroDefinition macro = CreateMacro("first", "second", "third");

        ActionExecutionResult result = await service.ExecuteAsync(Guid.NewGuid(), macro);

        Assert.IsFalse(result.IsSuccess);
        CollectionAssert.AreEqual(new[] { "first", "second" }, actionService.Targets);
        StringAssert.Contains(result.ErrorMessage, "ステップ2");
    }

    [TestMethod]
    public void Validate_RejectsNestedMacro()
    {
        LauncherSettings settings = LauncherSettings.CreateDefault();
        settings.Pages[0].Items.Add(new LauncherItemDefinition
        {
            Kind = LauncherItemKind.Button,
            Title = "Macro",
            Action = new LauncherActionDefinition
            {
                Kind = LauncherActionKind.Macro,
                Macro = new MacroDefinition
                {
                    Steps =
                    [
                        new MacroStepDefinition
                        {
                            DisplayName = "Nested",
                            Action = new LauncherActionDefinition
                            {
                                Kind = LauncherActionKind.Macro,
                                Macro = new MacroDefinition()
                            }
                        }
                    ]
                }
            }
        });

        IReadOnlyList<string> errors = LauncherSettingsValidator.Validate(settings);

        Assert.IsTrue(errors.Any(error => error.Contains("入れ子", StringComparison.Ordinal)));
    }

    private static MacroDefinition CreateMacro(params string[] targets)
    {
        MacroDefinition macro = new();
        foreach (string target in targets)
        {
            macro.Steps.Add(new MacroStepDefinition
            {
                DisplayName = target,
                Action = new LauncherActionDefinition
                {
                    Kind = LauncherActionKind.Application,
                    Target = target
                }
            });
        }

        return macro;
    }

    private sealed class RecordingActionService(string? failingTarget = null)
        : IActionExecutionService
    {
        public List<string> Targets { get; } = [];

        public Task<ActionExecutionResult> ExecuteAsync(
            LauncherActionDefinition action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Targets.Add(action.Target);
            return Task.FromResult(
                string.Equals(action.Target, failingTarget, StringComparison.Ordinal)
                    ? ActionExecutionResult.Failure("expected failure")
                    : ActionExecutionResult.Success);
        }
    }
}
