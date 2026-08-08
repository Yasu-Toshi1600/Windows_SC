using System.IO;
using System.Text.Json.Nodes;
using Windows_SC.Models;

namespace Windows_SC.Services;


internal static class LauncherSettingsMigrator
{
    public static void Migrate(JsonNode root)
    {
        int schemaVersion = root["SchemaVersion"]?.GetValue<int>() ?? 0;
        if (schemaVersion == 0)
        {
            schemaVersion = 1;
            root["SchemaVersion"] = schemaVersion;
        }

        while (schemaVersion < LauncherSettings.CurrentSchemaVersion)
        {
            schemaVersion = schemaVersion switch
            {
                1 => MigrateVersionOneToTwo(root),
                _ => throw new InvalidDataException(
                    $"未対応の設定スキーマです: {schemaVersion}")
            };
        }

        if (schemaVersion != LauncherSettings.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"未対応の設定スキーマです: {schemaVersion}");
        }
    }

    private static int MigrateVersionOneToTwo(JsonNode root)
    {
        // Phase 0 reserves schema v2 for optional feature-expansion fields. Existing
        // pages, item ordering, enum values, and legacy audio fields remain untouched.
        root["SchemaVersion"] = 2;
        return 2;
    }
}
