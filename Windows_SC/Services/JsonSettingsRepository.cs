using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Windows_SC.Models;

namespace Windows_SC.Services;

internal sealed class JsonSettingsRepository : ISettingsRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly DiagnosticLogger _logger;
    private readonly string _settingsFilePath;
    private readonly bool _usesDefaultSettingsPath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public JsonSettingsRepository(DiagnosticLogger logger, string? settingsFilePath = null)
    {
        _logger = logger;
        _usesDefaultSettingsPath = settingsFilePath is null;
        _settingsFilePath = settingsFilePath ?? ApplicationDataPaths.SettingsFilePath;
    }

    public async Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsFilePath))
        {
            LauncherSettings defaults = LauncherSettings.CreateDefault();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            _logger.Write("[Settings] action=create-default result=success");
            _logger.WriteDetailed(
                $"[Settings] action=create-default result=success " +
                $"path=\"{LogValue.Normalize(_settingsFilePath)}\"");
            return defaults;
        }

        try
        {
            string json = await File.ReadAllTextAsync(_settingsFilePath, cancellationToken)
                .ConfigureAwait(false);
            JsonNode root = JsonNode.Parse(json)
                ?? throw new JsonException("設定ファイルのルートがありません。");
            LauncherSettingsMigrator.Migrate(root);
            LauncherSettings settings = root.Deserialize<LauncherSettings>(SerializerOptions)
                ?? throw new JsonException("設定ファイルを読み込めませんでした。");
            EnsureValid(settings);
            _logger.Write(
                $"[Settings] action=load result=success schema={settings.SchemaVersion} " +
                $"pages={settings.Pages.Count}");
            return settings;
        }
        catch (FutureSettingsSchemaException exception)
        {
            _logger.Write(
                $"[Settings] action=load result=failed reason=future-schema " +
                $"found={exception.FoundVersion} supported={exception.SupportedVersion}");
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidDataException
            or NotSupportedException)
        {
            _logger.Write(
                $"[Settings] action=load result=failed reason=invalid-settings " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
            _logger.WriteDetailed(
                $"[Settings] action=load result=failed reason=invalid-settings " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8} " +
                $"message=\"{LogValue.Normalize(exception.Message)}\"");
            TryBackupInvalidSettings();
            LauncherSettings defaults = LauncherSettings.CreateDefault();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            _logger.Write("[Settings] action=recover-default result=success");
            return defaults;
        }
    }

    public async Task SaveAsync(
        LauncherSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureValid(settings);
            string? directory = Path.GetDirectoryName(_settingsFilePath);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("設定ファイルの保存先が不正です。");
            }

            Directory.CreateDirectory(directory);
            string temporaryPath = _settingsFilePath + ".tmp";

            try
            {
                string json = JsonSerializer.Serialize(settings, SerializerOptions);
                await File.WriteAllTextAsync(temporaryPath, json, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(temporaryPath, _settingsFilePath, overwrite: true);
                _logger.Write(
                    $"[Settings] action=save result=success schema={settings.SchemaVersion} " +
                    $"pages={settings.Pages.Count}");
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static void EnsureValid(LauncherSettings settings)
    {
        IReadOnlyList<string> errors = LauncherSettingsValidator.Validate(settings);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(" ", errors));
        }
    }

    private void TryBackupInvalidSettings()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return;
        }

        string backupDirectory = _usesDefaultSettingsPath
            ? ApplicationDataPaths.SettingsBackupDirectoryPath
            : Path.GetDirectoryName(_settingsFilePath)!;
        try
        {
            Directory.CreateDirectory(backupDirectory);
            string backupPath = Path.Combine(
                backupDirectory,
                $"settings.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.json");
            File.Move(_settingsFilePath, backupPath, overwrite: true);
            _logger.Write("[Settings] action=backup-invalid result=success");
            _logger.WriteDetailed(
                $"[Settings] action=backup-invalid result=success " +
                $"path=\"{LogValue.Normalize(backupPath)}\"");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            _logger.Write(
                $"[Settings] action=backup-invalid result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
        }
    }
}
