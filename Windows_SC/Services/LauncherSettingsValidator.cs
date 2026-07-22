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
        foreach (LauncherPageDefinition? page in settings.Pages)
        {
            if (page is null)
            {
                errors.Add("ページの内容がありません。");
                continue;
            }

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

            foreach (LauncherItemDefinition? item in page.Items)
            {
                if (item is null)
                {
                    errors.Add($"ページ内の項目がありません: {page.Id}");
                    continue;
                }

                if (item.Id == Guid.Empty || !itemIds.Add(item.Id))
                {
                    errors.Add("項目IDが空、または重複しています。");
                }

                if (string.IsNullOrWhiteSpace(item.Title))
                {
                    errors.Add($"項目名が空です: {item.Id}");
                }

                if (!Enum.IsDefined(item.Kind))
                {
                    errors.Add($"未対応の項目種類です: {item.Kind} ({item.Id})");
                    continue;
                }

                if (!Enum.IsDefined(item.PostExecutionBehavior))
                {
                    errors.Add(
                        $"未対応の実行後動作です: {item.PostExecutionBehavior} ({item.Id})");
                }

                switch (item.Kind)
                {
                    case LauncherItemKind.Button:
                        ValidateButton(item, errors);
                        break;
                    case LauncherItemKind.Toggle:
                        CycleActionDefinition? cycleAction = item.GetEffectiveCycleAction();
                        if (cycleAction is null)
                        {
                            errors.Add($"循環切り替えの内容がありません: {item.Id}");
                        }
                        else
                        {
                            ValidateCycleAction(item.Id, cycleAction, errors);
                        }

                        break;
                    case LauncherItemKind.Slider:
                        ValidateSlider(item, errors);
                        break;
                }
            }
        }

        return errors;
    }

    private static void ValidateButton(
        LauncherItemDefinition item,
        List<string> errors)
    {
        if (item.Action is null)
        {
            errors.Add($"ボタンの実行内容がありません: {item.Id}");
            return;
        }

        ValidateAction(item.Id, item.Action, "ボタン", errors);
    }

    private static void ValidateSlider(
        LauncherItemDefinition item,
        List<string> errors)
    {
        if (item.VolumeSlider is null)
        {
            errors.Add($"音量スライダーの設定がありません: {item.Id}");
            return;
        }

        double minimum = item.VolumeSlider.Minimum;
        double maximum = item.VolumeSlider.Maximum;
        if (!double.IsFinite(minimum)
            || !double.IsFinite(maximum)
            || minimum < 0
            || maximum > 100
            || minimum >= maximum)
        {
            errors.Add(
                $"音量スライダーの範囲は0～100で、最小値を最大値未満にしてください: {item.Id}");
        }
    }

    private static void ValidateCycleAction(
        Guid itemId,
        CycleActionDefinition cycleAction,
        List<string> errors)
    {
        if (!Enum.IsDefined(cycleAction.Kind))
        {
            errors.Add($"未対応の循環切り替え種類です: {cycleAction.Kind} ({itemId})");
            return;
        }

        if (cycleAction.Kind == CycleActionKind.AudioOutput)
        {
            if (cycleAction.AudioDeviceIds is null)
            {
                errors.Add($"音声切り替えのデバイス一覧がありません: {itemId}");
                return;
            }

            if (cycleAction.AudioDeviceIds.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"音声切り替えに空のデバイスIDがあります: {itemId}");
            }

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

        if (cycleAction.CommandSteps is null)
        {
            errors.Add($"コマンド切り替えの操作一覧がありません: {itemId}");
            return;
        }

        if (cycleAction.CommandSteps.Count < 2)
        {
            errors.Add($"コマンド切り替えには操作が2つ以上必要です: {itemId}");
        }

        HashSet<Guid> stepIds = [];
        foreach (CommandCycleStepDefinition? step in cycleAction.CommandSteps)
        {
            if (step is null)
            {
                errors.Add($"コマンド切り替えに空の操作があります: {itemId}");
                continue;
            }

            if (step.Id == Guid.Empty || !stepIds.Add(step.Id))
            {
                errors.Add($"コマンド操作IDが空、または重複しています: {itemId}");
            }

            if (string.IsNullOrWhiteSpace(step.DisplayName))
            {
                errors.Add($"コマンド操作の表示名が空です: {itemId}");
            }

            if (step.Action is null)
            {
                errors.Add($"コマンド操作の実行内容がありません: {itemId}");
            }
            else
            {
                ValidateAction(itemId, step.Action, "コマンド操作", errors);
                if (step.Action.Kind != LauncherActionKind.Command)
                {
                    errors.Add($"コマンド操作の実行種類が不正です: {itemId}");
                }
            }
        }
    }

    private static void ValidateAction(
        Guid itemId,
        LauncherActionDefinition action,
        string context,
        List<string> errors)
    {
        if (!Enum.IsDefined(action.Kind))
        {
            errors.Add($"{context}の実行種類が未対応です: {action.Kind} ({itemId})");
        }

        if (string.IsNullOrWhiteSpace(action.Target))
        {
            errors.Add($"{context}の実行対象が空です: {itemId}");
        }

        if (action.Arguments is null || action.WorkingDirectory is null)
        {
            errors.Add($"{context}の引数または作業フォルダーが不正です: {itemId}");
        }
    }
}
