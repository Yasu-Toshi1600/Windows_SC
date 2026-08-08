using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.Tests;

[TestClass]
public sealed class LauncherSettingsMigrationTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [TestMethod]
    public void Migrate_VersionOne_PreservesPagesItemsAndOrder()
    {
        JsonNode root = JsonNode.Parse(
            """
            {
              "SchemaVersion": 1,
              "AssumePhonePanelVisible": false,
              "StartWithWindows": true,
              "LayoutMode": 1,
              "Pages": [
                {
                  "Id": "11111111-1111-1111-1111-111111111111",
                  "Name": "Main",
                  "Items": [
                    {
                      "Id": "22222222-2222-2222-2222-222222222222",
                      "Kind": 0,
                      "Title": "App",
                      "Action": {
                        "Kind": 0,
                        "Target": "app.exe",
                        "Arguments": "--test",
                        "WorkingDirectory": "C:\\\\Apps",
                        "HideCommandWindow": true
                      }
                    },
                    {
                      "Id": "33333333-3333-3333-3333-333333333333",
                      "Kind": 1,
                      "Title": "Audio",
                      "CycleAction": {
                        "Kind": 0,
                        "AudioDeviceIds": ["device-b", "device-a"],
                        "CommandSteps": []
                      }
                    }
                  ]
                }
              ]
            }
            """)!;

        LauncherSettingsMigrator.Migrate(root);
        LauncherSettings settings = root.Deserialize<LauncherSettings>(SerializerOptions)!;

        Assert.AreEqual(2, settings.SchemaVersion);
        Assert.IsFalse(settings.AssumePhonePanelVisible);
        Assert.IsTrue(settings.StartWithWindows);
        Assert.AreEqual(LauncherLayoutMode.Compact, settings.LayoutMode);
        Assert.AreEqual(2, settings.Pages[0].Items.Count);
        Assert.AreEqual("App", settings.Pages[0].Items[0].Title);
        CollectionAssert.AreEqual(
            new[] { "device-b", "device-a" },
            settings.Pages[0].Items[1].CycleAction!.AudioDeviceIds);
        Assert.AreEqual(0, LauncherSettingsValidator.Validate(settings).Count);
    }

    [TestMethod]
    public void Migrate_MissingVersion_TreatsDocumentAsVersionOne()
    {
        JsonNode root = JsonNode.Parse(
            """
            {
              "Pages": [
                {
                  "Id": "11111111-1111-1111-1111-111111111111",
                  "Name": "Main",
                  "Items": []
                }
              ]
            }
            """)!;

        LauncherSettingsMigrator.Migrate(root);

        Assert.AreEqual(2, root["SchemaVersion"]!.GetValue<int>());
    }

    [TestMethod]
    public void Migrate_FutureVersion_ThrowsInvalidDataException()
    {
        JsonNode root = JsonNode.Parse("""{ "SchemaVersion": 99 }""")!;

        Assert.ThrowsExactly<InvalidDataException>(() =>
            LauncherSettingsMigrator.Migrate(root));
    }
}
