using System;
using System.Collections.Generic;
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
            }
        }

        return errors;
    }
}
