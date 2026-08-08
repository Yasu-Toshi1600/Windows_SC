using System;
using System.Collections.Generic;
using System.Linq;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.ViewModels;

internal sealed record SettingsSaveValidationResult(
    bool IsValid,
    Guid? ItemId,
    Guid? CommandStepId,
    Guid? MacroStepId,
    string Message)
{
    public static SettingsSaveValidationResult Valid { get; } =
        new(true, null, null, null, string.Empty);

    public static SettingsSaveValidationResult Invalid(
        Guid itemId,
        string message,
        Guid? commandStepId = null,
        Guid? macroStepId = null) =>
        new(false, itemId, commandStepId, macroStepId, message);
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
            item.IsButton
            && item.ActionKind is not (LauncherActionKind.ShortcutKey or LauncherActionKind.Macro)
            && string.IsNullOrWhiteSpace(item.Target));
        if (incompleteButton is not null)
        {
            return SettingsSaveValidationResult.Invalid(
                incompleteButton.Id,
                "ボタンの起動対象またはコマンドを入力してください。");
        }

        LauncherItemEditorViewModel? incompleteShortcut = items.FirstOrDefault(item =>
            item.IsButton
            && item.ActionKind == LauncherActionKind.ShortcutKey
            && ShortcutKeyValidator.Validate(item.ShortcutKey) is not null);
        if (incompleteShortcut is not null)
        {
            return SettingsSaveValidationResult.Invalid(
                incompleteShortcut.Id,
                ShortcutKeyValidator.Validate(incompleteShortcut.ShortcutKey)!);
        }

        foreach (LauncherItemEditorViewModel item in items.Where(item =>
                     item.IsButton && item.ActionKind == LauncherActionKind.Macro))
        {
            if (item.MacroSteps.Count is < 1 or > 50)
            {
                return SettingsSaveValidationResult.Invalid(
                    item.Id,
                    "マクロには1～50個のステップを登録してください。");
            }

            int totalDelay = 0;
            HashSet<Guid> ids = [];
            foreach (MacroStepEditorViewModel step in item.MacroSteps)
            {
                string? error = null;
                if (!ids.Add(step.Id) || step.Id == Guid.Empty)
                {
                    error = "マクロのステップIDが重複しています。";
                }
                else if (string.IsNullOrWhiteSpace(step.DisplayName))
                {
                    error = "マクロの各ステップに表示名を入力してください。";
                }
                else if (step.Kind == MacroStepKind.Wait)
                {
                    totalDelay += step.DelayMilliseconds;
                    if (step.DelayMilliseconds is < 0 or > 10_000)
                    {
                        error = "1回の待機時間は0～10000msで指定してください。";
                    }
                }
                else if (step.ActionKind == LauncherActionKind.ShortcutKey)
                {
                    error = ShortcutKeyValidator.Validate(step.ShortcutKey);
                }
                else if (string.IsNullOrWhiteSpace(step.Target))
                {
                    error = "マクロの操作ステップに実行対象を入力してください。";
                }

                if (error is not null)
                {
                    return SettingsSaveValidationResult.Invalid(
                        item.Id,
                        error,
                        macroStepId: step.Id);
                }
            }

            if (totalDelay > 60_000)
            {
                return SettingsSaveValidationResult.Invalid(
                    item.Id,
                    "マクロの待機時間合計は60000ms以下にしてください。");
            }
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
