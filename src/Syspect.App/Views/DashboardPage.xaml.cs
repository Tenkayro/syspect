using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syspect.App.Services;
using Syspect.App.ViewModels;

namespace Syspect.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardViewModel ViewModel { get; } = new();

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (ViewModel.Overview is null)
            {
                await ViewModel.LoadAsync();
            }
        };
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private void GoToRepairButton_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Current.NavigateRequested?.Invoke(typeof(RepairPage), null);
    }

    private void GoToLogsButton_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Current.NavigateRequested?.Invoke(typeof(LogsPage), null);
    }

    private async void OneClickCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Run Safe One-Click Cleanup?",
            Content = "The following safe maintenance steps will be executed:\n• Disable DiagTrack (telemetry)\n• Remove orphaned Downloads/legacy firewall rules\n• Delete dead scheduled tasks (missing executables)\n• Clean stale user temporary files\n\nProtected services (SharedAccess, WSL, Audio/iLok, VMware) remain untouched.",
            PrimaryButtonText = "Clean Up",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.RunOneClickCleanupAsync();
        }
    }

    private bool _isUpdateDialogOpen;

    private async void RestartNvidiaButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RestartNvidiaTrayAsync();
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdateDialogOpen || XamlRoot is null)
        {
            return;
        }

        var result = await ViewModel.CheckForUpdatesAsync();
        if (result is { IsUpdateAvailable: true })
        {
            _isUpdateDialogOpen = true;
            try
            {
                await UpdateDialogHelper.ShowUpdateDialogAsync(XamlRoot, AppServices.Current.Updates, result);
            }
            finally
            {
                _isUpdateDialogOpen = false;
            }
        }
    }
}
