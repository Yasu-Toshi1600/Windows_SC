using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows_SC.ViewModels.Settings;

namespace Windows_SC.Views.Settings;

public sealed partial class DiagnosticsPanel : UserControl
{
    public DiagnosticsPanel()
    {
        InitializeComponent();
    }

    internal event EventHandler? DeleteLogsRequested;

    private DiagnosticsSettingsViewModel? ViewModel => DataContext as DiagnosticsSettingsViewModel;

    private void OpenDataFolder_Click(object sender, RoutedEventArgs args) =>
        ViewModel?.OpenDataFolder();

    private async void ApplyDetailedDiagnostics_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel is { } viewModel)
        {
            await viewModel.ApplyAsync();
        }
    }

    private void CopyEnvironmentInfo_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        DataPackage package = new();
        package.SetText(viewModel.CreateEnvironmentInformation());
        Clipboard.SetContent(package);
        viewModel.ReportEnvironmentInformationCopied();
    }

    private void DeleteLogs_Click(object sender, RoutedEventArgs args) =>
        DeleteLogsRequested?.Invoke(this, EventArgs.Empty);
}
