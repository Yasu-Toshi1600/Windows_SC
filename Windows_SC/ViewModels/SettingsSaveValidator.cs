using System;
using System.Collections.Generic;
using System.Linq;
using Windows_SC.Models;

namespace Windows_SC.ViewModels;

internal sealed record SettingsSaveValidationResult(
    bool IsValid,
    Guid? ItemId,
    Guid? CommandStepId,
    string Message)
{
    public static SettingsSaveValidationResult Valid { get; } =
        new(true, null, null, string.Empty);

    public static SettingsSaveValidationResult Invalid(
        Guid itemId,
        string message,
        Guid? commandStepId = null) =>
        new(false, itemId, commandStepId, message);
}

internal static class SettingsSaveValidator
{
    public static SettingsSaveValidationResult Validate(
        IReadOnlyList<LauncherItemEditorViewModel> items)
    {
        LauncherItemEditorViewModel? unnamedItem = items.FirstOrDefault(item =>
            string.IsNullOrWhiteSpace(item.Title));
        if (unnamedItem is not null)
        {
            return SettingsSaveValidationResult.Invalid(
                unnamedItem.Id,
                "表示名を入力してください。");
        }

        LauncherItemEditorViewModel? incompleteButton = items.FirstOrDefault(item =>
            item.IsButton && string.IsNullOrWhiteSpace(item.Target));
        if (incompleteButton is not null)
        {
            return SettingsSaveValidationResult.Invalid(
                incompleteButton.Id,
                "ボタンの起動対象またはコマンドを入力してください。");
        }

        LauncherItemEditorViewModel? incompleteAudioItem = items.FirstOrDefault(item =>
            item.IsToggle
            && item.CycleKind == CycleActionKind.AudioOutput
            && item.RegisteredAudioDevices
                .Select(device => device.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() < 2);
        if (incompleteAudioItem is not null)
        {
            return SettingsSaveValidationResult.Invalid(
                incompleteAudioItem.Id,
                "音声切り替えにはデバイスを2台以上登録してください。");
        }

        LauncherItemEditorViewModel? incompleteCommandItem = items.FirstOrDefault(item =>
            item.IsToggle
            && item.CycleKind == CycleActionKind.Commands
            && item.CommandSteps.Count < 2);
        if (incompleteCommandItem is not null)
        {
            return SettingsSaveValidationResult.Invalid(
                incompleteCommandItem.Id,
                "コマンド切り替えには操作を2つ以上登録してください。");
        }

        foreach (LauncherItemEditorViewModel item in items.Where(item =>
                     item.IsToggle && item.CycleKind == CycleActionKind.Commands))
        {
            CommandCycleStepEditorViewModel? incompleteStep =
                item.CommandSteps.FirstOrDefault(step =>
                    string.IsNullOrWhiteSpace(step.DisplayName)
                    || string.IsNullOrWhiteSpace(step.Target));
            if (incompleteStep is not null)
            {
                return SettingsSaveValidationResult.Invalid(
                    item.Id,
                    "各操作の表示名と実行対象を入力してください。",
                    incompleteStep.Id);
            }
        }

        return SettingsSaveValidationResult.Valid;
    }
}
