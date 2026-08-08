using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.ViewModels.Settings;

internal sealed class DiagnosticsSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly SettingsPersistenceCoordinator _persistenceCoordinator;
    private readonly DiagnosticLogger _logger;
    private readonly EnvironmentInformationService _environmentInformationService;
    private readonly IStartMenuMonitor _startMenuMonitor;
    private string _statusMessage = string.Empty;
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private bool _isDetailedDiagnosticsEnabled;
    private bool _isDetailedDiagnosticsAlwaysEnabled;
    private DateTimeOffset? _detailedLoggingExpiresAt;
    private bool _isApplyingSetting;

    public DiagnosticsSettingsViewModel(
        LauncherSettings settings,
        ISettingsRepository settingsRepository,
        MainWindowViewModel mainWindowViewModel,
        SettingsPersistenceCoordinator persistenceCoordinator,
        DiagnosticLogger logger,
        EnvironmentInformationService environmentInformationService,
        IStartMenuMonitor startMenuMonitor)
    {
        _settingsRepository = settingsRepository;
        _mainWindowViewModel = mainWindowViewModel;
        _persistenceCoordinator = persistenceCoordinator;
        _logger = logger;
        _environmentInformationService = environmentInformationService;
        _startMenuMonitor = startMenuMonitor;
        _detailedLoggingExpiresAt = settings.DetailedLoggingExpiresAtUtc;
        _isDetailedDiagnosticsAlwaysEnabled = settings.DetailedLoggingAlwaysEnabled;
        _isDetailedDiagnosticsEnabled =
            _isDetailedDiagnosticsAlwaysEnabled
            || (_detailedLoggingExpiresAt is { } expiration
                && expiration > DateTimeOffset.UtcNow);
        _startMenuMonitor.ReadyChanged += StartMenuMonitor_ReadyChanged;
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(IsStatusMessageOpen));
            }
        }
    }

    public InfoBarSeverity StatusSeverity
    {
        get => _statusSeverity;
        private set => SetProperty(ref _statusSeverity, value);
    }

    public bool IsStatusMessageOpen => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool IsDetailedDiagnosticsEnabled
    {
        get => _isDetailedDiagnosticsEnabled;
        set
        {
            if (!SetProperty(ref _isDetailedDiagnosticsEnabled, value))
            {
                return;
            }

            _detailedLoggingExpiresAt = null;
            if (!value && _isDetailedDiagnosticsAlwaysEnabled)
            {
                _isDetailedDiagnosticsAlwaysEnabled = false;
                OnPropertyChanged(nameof(IsDetailedDiagnosticsAlwaysEnabled));
            }

            OnPropertyChanged(nameof(DetailedDiagnosticsStatus));
            SetStatus(
                value
                    ? "［適用］を押すと詳細診断ログが有効になります。"
                    : "［適用］を押すと詳細診断ログが無効になります。",
                InfoBarSeverity.Informational);
        }
    }

    public bool IsDetailedDiagnosticsAlwaysEnabled
    {
        get => _isDetailedDiagnosticsAlwaysEnabled;
        set
        {
            if (!SetProperty(ref _isDetailedDiagnosticsAlwaysEnabled, value))
            {
                return;
            }

            if (value && !_isDetailedDiagnosticsEnabled)
            {
                _isDetailedDiagnosticsEnabled = true;
                OnPropertyChanged(nameof(IsDetailedDiagnosticsEnabled));
            }

            _detailedLoggingExpiresAt = null;
            OnPropertyChanged(nameof(DetailedDiagnosticsStatus));
            SetStatus(
                value
                    ? "［適用］を押すと詳細診断ログが常時有効になります。"
                    : "［適用］を押すと詳細診断ログが24時間有効になります。",
                InfoBarSeverity.Informational);
        }
    }

    public string DetailedDiagnosticsStatus
    {
        get
        {
            if (!IsDetailedDiagnosticsEnabled)
            {
                return "現在は無効です。通常ログのみ記録します。";
            }

            if (IsDetailedDiagnosticsAlwaysEnabled)
            {
                return "常時有効です。手動で無効にするまで詳細ログを記録します。";
            }

            return _detailedLoggingExpiresAt is { } expiration
                && expiration > DateTimeOffset.UtcNow
                    ? $"{expiration.ToLocalTime():yyyy/MM/dd HH:mm}まで有効です。"
                    : "［適用］を押すと、その時点から24時間有効になります。";
        }
    }

    public string NormalLogPath => GetDisplayPath(_logger.LogFilePath);

    public string DetailedLogPath => GetDisplayPath(_logger.DetailedLogFilePath);

    public string StartMenuMonitoringStatus => _startMenuMonitor.IsReady
        ? "スタートメニュー監視: 正常"
        : "スタートメニュー監視: 代替モードで動作中（UI Automationイベントを利用できません）";

    public string ApplicationVersionText => $"Windows_SC バージョン {ApplicationInformation.Version}";

    internal void OpenDataFolder() =>
        OpenFolder(ApplicationDataPaths.RootDirectoryPath, "データフォルダー");

    internal async Task ApplyAsync()
    {
        if (_isApplyingSetting)
        {
            return;
        }

        _isApplyingSetting = true;
        bool newAlwaysEnabled = IsDetailedDiagnosticsEnabled
            && IsDetailedDiagnosticsAlwaysEnabled;
        DateTimeOffset? newExpiration = IsDetailedDiagnosticsEnabled
            && !newAlwaysEnabled
                ? DateTimeOffset.UtcNow.AddHours(24)
                : null;

        try
        {
            await _persistenceCoordinator.RunAsync(async () =>
            {
                LauncherSettings settings = _mainWindowViewModel.ExportSettings();
                DateTimeOffset? previousExpiration = settings.DetailedLoggingExpiresAtUtc;
                bool previousAlwaysEnabled = settings.DetailedLoggingAlwaysEnabled;
                try
                {
                    settings.DetailedLoggingExpiresAtUtc = newExpiration;
                    settings.DetailedLoggingAlwaysEnabled = newAlwaysEnabled;
                    await _settingsRepository.SaveAsync(settings);
                    _detailedLoggingExpiresAt = newExpiration;
                    _logger.ConfigureDetailedLogging(newExpiration, newAlwaysEnabled);
                    _environmentInformationService.LogIfChanged("diagnostics-setting");
                    OnPropertyChanged(nameof(DetailedDiagnosticsStatus));
                    SetStatus(
                        newAlwaysEnabled
                            ? "詳細診断ログを常時有効にしました。"
                            : IsDetailedDiagnosticsEnabled
                                ? "詳細診断ログを24時間有効にしました。"
                                : "詳細診断ログを無効にしました。",
                        InfoBarSeverity.Success);
                }
                catch (Exception exception)
                {
                    settings.DetailedLoggingExpiresAtUtc = previousExpiration;
                    settings.DetailedLoggingAlwaysEnabled = previousAlwaysEnabled;
                    _logger.Write(
                        $"[Diagnostics] action=configure-detailed-logging result=failed " +
                        $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
                    _logger.WriteDetailed(
                        $"[Diagnostics] action=configure-detailed-logging result=failed " +
                        $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8} " +
                        $"message=\"{LogValue.Normalize(exception.Message)}\"");
                    SetStatus(
                        $"詳細診断ログの設定を保存できませんでした: {exception.Message}",
                        InfoBarSeverity.Error);
                }
            });
        }
        finally
        {
            _isApplyingSetting = false;
        }
    }

    internal string CreateEnvironmentInformation() =>
        _environmentInformationService.CreateReport();

    internal void RefreshEnvironmentInformationLog() =>
        _environmentInformationService.LogIfChanged("troubleshooting");

    internal void ReportEnvironmentInformationCopied() =>
        SetStatus("環境情報をコピーしました。", InfoBarSeverity.Informational);

    internal void ClearLogs()
    {
        try
        {
            _logger.ClearLogs();
            _logger.Write("[Diagnostics] action=clear-logs result=success");
            _environmentInformationService.LogIfChanged("logs-cleared", force: true);
            SetStatus("ログを削除しました。", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            _logger.Write(
                $"[Diagnostics] action=clear-logs result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
            _logger.WriteDetailed(
                $"[Diagnostics] action=clear-logs result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8} " +
                $"message=\"{LogValue.Normalize(exception.Message)}\"");
            SetStatus(
                $"ログを削除できませんでした: {exception.Message}",
                InfoBarSeverity.Error);
        }
    }

    public void Dispose() =>
        _startMenuMonitor.ReadyChanged -= StartMenuMonitor_ReadyChanged;

    private void StartMenuMonitor_ReadyChanged(object? sender, EventArgs args) =>
        OnPropertyChanged(nameof(StartMenuMonitoringStatus));

    private void OpenFolder(string folderPath, string displayName)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });
            _logger.Write("[Diagnostics] action=open-data-folder result=success");
            _logger.WriteDetailed(
                $"[Diagnostics] action=open-data-folder result=success " +
                $"path=\"{LogValue.Normalize(folderPath)}\"");
            SetStatus($"{displayName}を開きました。", InfoBarSeverity.Informational);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or IOException
            or UnauthorizedAccessException)
        {
            _logger.Write(
                $"[Diagnostics] action=open-data-folder result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
            _logger.WriteDetailed(
                $"[Diagnostics] action=open-data-folder result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8} " +
                $"path=\"{LogValue.Normalize(folderPath)}\" " +
                $"message=\"{LogValue.Normalize(exception.Message)}\"");
            SetStatus($"{displayName}を開けませんでした: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private static string GetDisplayPath(string path)
    {
        string localAppData = Environment
            .GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            .TrimEnd(Path.DirectorySeparatorChar);
        string prefix = localAppData + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? $"%LOCALAPPDATA%{Path.DirectorySeparatorChar}" + path[prefix.Length..]
            : path;
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusSeverity = severity;
        StatusMessage = message;
    }
}
