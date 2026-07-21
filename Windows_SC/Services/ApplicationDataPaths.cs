using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Storage;

namespace Windows_SC.Services;

internal static class ApplicationDataPaths
{
    public static string RootDirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Windows_SC");

    public static string SettingsDirectoryPath { get; } =
        Path.Combine(RootDirectoryPath, "Settings");

    public static string SettingsBackupDirectoryPath { get; } =
        Path.Combine(SettingsDirectoryPath, "Backup");

    public static string SettingsFilePath { get; } =
        Path.Combine(SettingsDirectoryPath, "settings.json");

    public static string LogDirectoryPath { get; } =
        Path.Combine(RootDirectoryPath, "Logs");

    public static IReadOnlyList<string> MigrateLegacyData()
    {
        List<string> messages = [];
        Directory.CreateDirectory(SettingsBackupDirectoryPath);
        Directory.CreateDirectory(LogDirectoryPath);

        List<string> legacyRoots = [RootDirectoryPath];
        List<string> legacyLogDirectories = [Path.Combine(RootDirectoryPath, "Logs")];
        AddPackagedLegacyPaths(legacyRoots, legacyLogDirectories, messages);

        MigrateSettings(legacyRoots, messages);
        MigrateSettingsBackups(legacyRoots, messages);
        MigrateLogs(legacyLogDirectories, messages);
        return messages;
    }

    private static void AddPackagedLegacyPaths(
        ICollection<string> legacyRoots,
        ICollection<string> legacyLogDirectories,
        ICollection<string> messages)
    {
        try
        {
            string localCachePath = ApplicationData.Current.LocalCacheFolder.Path;
            string localStatePath = ApplicationData.Current.LocalFolder.Path;
            AddDistinctPath(
                legacyRoots,
                Path.Combine(localCachePath, "Local", "Windows_SC"));
            AddDistinctPath(
                legacyLogDirectories,
                Path.Combine(localCachePath, "Local", "Windows_SC", "Logs"));
            AddDistinctPath(legacyLogDirectories, Path.Combine(localStatePath, "Logs"));
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            messages.Add("[DataMigration] package-storage=unavailable mode=unpackaged");
        }
    }

    private static void MigrateSettings(
        IEnumerable<string> legacyRoots,
        ICollection<string> messages)
    {
        List<string> sources = legacyRoots
            .Select(root => Path.Combine(root, "settings.json"))
            .Where(path => !PathsEqual(path, SettingsFilePath) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        foreach (string source in sources)
        {
            try
            {
                string destination = !File.Exists(SettingsFilePath)
                    ? SettingsFilePath
                    : CreateUniqueBackupPath("settings.legacy");
                File.Move(source, destination);
                messages.Add(
                    $"[DataMigration] type=settings result=success source=\"{source}\" destination=\"{destination}\"");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                messages.Add(
                    $"[DataMigration] type=settings result=failed exception={exception.GetType().Name} source=\"{source}\"");
            }
        }
    }

    private static void MigrateSettingsBackups(
        IEnumerable<string> legacyRoots,
        ICollection<string> messages)
    {
        foreach (string root in legacyRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root) || PathsEqual(root, SettingsDirectoryPath))
            {
                continue;
            }

            try
            {
                foreach (string source in Directory.EnumerateFiles(
                    root,
                    "settings.corrupt-*.json",
                    SearchOption.TopDirectoryOnly))
                {
                    string destination = CreateUniqueBackupPath(
                        Path.GetFileNameWithoutExtension(source));
                    File.Move(source, destination);
                    messages.Add(
                        $"[DataMigration] type=settings-backup result=success source=\"{source}\" destination=\"{destination}\"");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                messages.Add(
                    $"[DataMigration] type=settings-backup result=failed exception={exception.GetType().Name} source=\"{root}\"");
            }
        }
    }

    private static void MigrateLogs(
        IEnumerable<string> legacyLogDirectories,
        ICollection<string> messages)
    {
        foreach (string sourceDirectory in legacyLogDirectories
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (PathsEqual(sourceDirectory, LogDirectoryPath)
                || !Directory.Exists(sourceDirectory))
            {
                continue;
            }

            try
            {
                foreach (string source in Directory.EnumerateFiles(
                    sourceDirectory,
                    "window-diagnostics*.log",
                    SearchOption.TopDirectoryOnly))
                {
                    string destination = Path.Combine(LogDirectoryPath, Path.GetFileName(source));
                    if (File.Exists(destination))
                    {
                        string legacyContent = File.ReadAllText(source);
                        if (!string.IsNullOrEmpty(legacyContent))
                        {
                            File.AppendAllText(destination, Environment.NewLine + legacyContent);
                        }

                        File.Delete(source);
                    }
                    else
                    {
                        File.Move(source, destination);
                    }

                    messages.Add(
                        $"[DataMigration] type=log result=success source=\"{source}\" destination=\"{destination}\"");
                }

                TryDeleteEmptyDirectory(sourceDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                messages.Add(
                    $"[DataMigration] type=log result=failed exception={exception.GetType().Name} source=\"{sourceDirectory}\"");
            }
        }
    }

    private static string CreateUniqueBackupPath(string fileNameWithoutExtension)
    {
        string timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        for (int suffix = 0; ; suffix++)
        {
            string suffixText = suffix == 0 ? string.Empty : $"-{suffix}";
            string candidate = Path.Combine(
                SettingsBackupDirectoryPath,
                $"{fileNameWithoutExtension}-{timestamp}{suffixText}.json");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void AddDistinctPath(ICollection<string> paths, string path)
    {
        if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(path);
        }
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteEmptyDirectory(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }
}
