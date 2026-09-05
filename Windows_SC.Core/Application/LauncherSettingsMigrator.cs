using System.IO;
using System.Text.Json.Nodes;
using Windows_SC.Models;

namespace Windows_SC.Services;


internal static class LauncherSettingsMigrator
{
    public static void Migrate(JsonNode root)
    {
        if (root is not JsonObject settingsObject)
        {
            throw new InvalidDataException("The settings root must be a JSON object.");
        }

        long schemaVersion = ReadSchemaVersion(settingsObject);
        if (schemaVersion > LauncherSettings.CurrentSchemaVersion)
        {
            throw new FutureSettingsSchemaException(
                schemaVersion,
                LauncherSettings.CurrentSchemaVersion);
        }

        if (schemaVersion == 0)
        {
            schemaVersion = 1;
            settingsObject["SchemaVersion"] = schemaVersion;
        }

        while (schemaVersion < LauncherSettings.CurrentSchemaVersion)
        {
            schemaVersion = schemaVersion switch
            {
                1 => MigrateVersionOneToTwo(settingsObject),
                _ => throw new InvalidDataException(
                    $"未対応の設定スキーマです: {schemaVersion}")
            };
        }

        if (schemaVersion != LauncherSettings.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"未対応の設定スキーマです: {schemaVersion}");
        }
    }

    private static long ReadSchemaVersion(JsonObject root)
    {
        JsonNode? versionNode = root["SchemaVersion"];
        if (versionNode is null)
        {
            return 0;
        }

        if (versionNode is JsonValue value
            && value.TryGetValue(out long schemaVersion)
            && schemaVersion >= 0)
        {
            return schemaVersion;
        }

        throw new InvalidDataException(
            "The settings schema version must be a non-negative integer.");
    }

    private static int MigrateVersionOneToTwo(JsonNode root)
    {
        // Phase 0 reserves schema v2 for optional feature-expansion fields. Existing
        // pages, item ordering, enum values, and legacy audio fields remain untouched.
        root["SchemaVersion"] = 2;
        return 2;
    }
}
