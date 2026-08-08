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
    public async Task ExecuteAsync_RejectsConcurrentMacroWithoutInterleavingSteps()
    {
        BlockingActionService actionService = new();
        using MacroExecutionService service = new(actionService, _ => { });
        Task<ActionExecutionResult> first = service.ExecuteAsync(
            Guid.NewGuid(),
            CreateMacro("first"));
        await actionService.Started.Task;

        ActionExecutionResult second = await service.ExecuteAsync(
            Guid.NewGuid(),
            CreateMacro("second"));
        actionService.Release.SetResult();
        ActionExecutionResult firstResult = await first;

        Assert.IsFalse(second.IsSuccess);
        StringAssert.Contains(second.ErrorMessage, "実行中");
        Assert.IsTrue(firstResult.IsSuccess);
        CollectionAssert.AreEqual(new[] { "first" }, actionService.Targets);
    }

    [TestMethod]
    public async Task ExecuteAsync_AfterDisposeThrowsObjectDisposedException()
    {
        RecordingActionService actionService = new();
        MacroExecutionService service = new(actionService, _ => { });
        service.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            service.ExecuteAsync(Guid.NewGuid(), CreateMacro("first")));
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

    private sealed class BlockingActionService : IActionExecutionService
    {
        public List<string> Targets { get; } = [];

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ActionExecutionResult> ExecuteAsync(
            LauncherActionDefinition action,
            CancellationToken cancellationToken = default)
        {
            Targets.Add(action.Target);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return ActionExecutionResult.Success;
        }
    }
}
