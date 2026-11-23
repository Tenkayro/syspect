using System.Windows;
using System.Windows.Controls;
using Syspect.App.Services;
using Syspect.App.ViewModels;

namespace Syspect.App.Views;

public partial class DashboardPage : UserControl
{
    private bool _isUpdateDialogOpen;

    public DashboardViewModel ViewModel { get; } = new();

    public DashboardPage()
    {
        DataContext = ViewModel;
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
        var result = MessageBox.Show(
            Window.GetWindow(this),
            "The following safe maintenance steps will be executed:\n" +
            "• Disable DiagTrack (telemetry)\n" +
            "• Remove orphaned Downloads/legacy firewall rules\n" +
            "• Delete dead scheduled tasks (missing executables)\n" +
            "• Clean stale user temporary files\n\n" +
            "Protected services (SharedAccess, WSL, Audio/iLok, VMware) remain untouched.",
            "Run Safe One-Click Cleanup?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            await ViewModel.RunOneClickCleanupAsync();
        }
    }

    private async void RestartNvidiaButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RestartNvidiaTrayAsync();
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdateDialogOpen)
        {
            return;
        }

        var result = await ViewModel.CheckForUpdatesAsync();
        if (result is { IsUpdateAvailable: true })
        {
            _isUpdateDialogOpen = true;
            try
            {
                UpdateDialogHelper.ShowUpdateDialog(Window.GetWindow(this), AppServices.Current.Updates, result);
            }
            finally
            {
                _isUpdateDialogOpen = false;
            }
        }
    }
}
