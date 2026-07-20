using System;
using System.Collections.Generic;
using System.Linq;
using Windows_SC.Models;

namespace Windows_SC.Services;

internal static class LauncherSettingsValidator
{
    public static IReadOnlyList<string> Validate(LauncherSettings settings)
    {
        List<string> errors = [];

        if (settings.SchemaVersion != LauncherSettings.CurrentSchemaVersion)
        {
            errors.Add($"未対応の設定スキーマです: {settings.SchemaVersion}");
        }

        if (!Enum.IsDefined(settings.LayoutMode))
        {
            errors.Add($"未対応のレイアウト設定です: {settings.LayoutMode}");
        }

        if (settings.Pages is null || settings.Pages.Count == 0)
        {
            errors.Add("ランチャーページが1つ以上必要です。");
            return errors;
        }

        HashSet<Guid> pageIds = [];
        HashSet<Guid> itemIds = [];
        foreach (LauncherPageDefinition page in settings.Pages)
        {
            if (page.Id == Guid.Empty || !pageIds.Add(page.Id))
            {
                errors.Add("ページIDが空、または重複しています。");
            }

            if (string.IsNullOrWhiteSpace(page.Name))
            {
                errors.Add($"ページ名が空です: {page.Id}");
            }

            if (page.Items is null)
            {
                errors.Add($"ページの項目一覧がありません: {page.Id}");
                continue;
            }

            foreach (LauncherItemDefinition item in page.Items)
            {
                if (item.Id == Guid.Empty || !itemIds.Add(item.Id))
                {
                    errors.Add("項目IDが空、または重複しています。");
                }

                if (string.IsNullOrWhiteSpace(item.Title))
                {
                    errors.Add($"項目名が空です: {item.Id}");
                }

                if (item.Kind == LauncherItemKind.Slider
                    && item.VolumeSlider is { } slider
                    && slider.Minimum >= slider.Maximum)
                {
                    errors.Add($"スライダーの最小値は最大値未満である必要があります: {item.Id}");
                }

                if (item.Kind == LauncherItemKind.Toggle
                    && item.CycleAction is { } cycleAction)
                {
                    ValidateCycleAction(item.Id, cycleAction, errors);
                }
            }
        }

        return errors;
    }

    private static void ValidateCycleAction(
        Guid itemId,
        CycleActionDefinition cycleAction,
        List<string> errors)
    {
        if (cycleAction.Kind == CycleActionKind.AudioOutput)
        {
            int deviceCount = cycleAction.AudioDeviceIds
                .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (deviceCount < 2)
            {
                errors.Add($"音声切り替えにはデバイスが2台以上必要です: {itemId}");
            }

            return;
        }

        if (cycleAction.CommandSteps.Count < 2)
        {
            errors.Add($"コマンド切り替えには操作が2つ以上必要です: {itemId}");
        }

        HashSet<Guid> stepIds = [];
        foreach (CommandCycleStepDefinition step in cycleAction.CommandSteps)
        {
            if (step.Id == Guid.Empty || !stepIds.Add(step.Id))
            {
                errors.Add($"コマンド操作IDが空、または重複しています: {itemId}");
            }

            if (string.IsNullOrWhiteSpace(step.DisplayName))
            {
                errors.Add($"コマンド操作の表示名が空です: {itemId}");
            }

            if (step.Action is null || string.IsNullOrWhiteSpace(step.Action.Target))
            {
                errors.Add($"コマンド操作の実行対象が空です: {itemId}");
            }
        }
    }
}
