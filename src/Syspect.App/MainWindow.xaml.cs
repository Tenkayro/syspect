using System.Windows;
using System.Windows.Media;
using Syspect.App.Services;
using Syspect.App.Views;
using Syspect.Core.Models;
using Syspect.Core.Services;

namespace Syspect.App;

public partial class MainWindow : Window
{
    private UpdateCheckResult? _pendingUpdate;
    private bool _isUpdateDialogOpen;

    public MainWindow()
    {
        InitializeComponent();
        UpdateAdminHeader();

        AppServices.Current.NavigateRequested = NavigateToPage;
        Loaded += MainWindow_Loaded;
    }

    private void UpdateAdminHeader()
    {
        if (AdminHelper.IsAdministrator())
        {
            var bg = new SolidColorBrush(Color.FromArgb(45, 16, 185, 129));
            bg.Freeze();
            AdminBadge.Background = bg;
            AdminBadgeText.Text = "Admin";
            ElevateButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            var bg = new SolidColorBrush(Color.FromArgb(50, 245, 158, 11));
            bg.Freeze();
            AdminBadge.Background = bg;
            AdminBadgeText.Text = "Standard User";
            ElevateButton.Visibility = Visibility.Visible;
        }
    }

    private void NavigateToPage(Type pageType, object? parameter)
    {
        MainTabs.SelectedIndex = pageType.Name switch
        {
            nameof(DashboardPage) => 0,
            nameof(RepairPage) => 1,
            nameof(DriversPage) => 2,
            nameof(StartupServicesPage) => 3,
            nameof(FirewallPage) => 4,
            nameof(LogsPage) => 5,
            _ => 0
        };
    }

    private void ElevateButton_Click(object sender, RoutedEventArgs e)
    {
        if (AdminHelper.TryRestartElevated())
        {
            Application.Current.Shutdown();
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        _ = CheckForUpdatesSilentlyAsync();
    }

    private async Task CheckForUpdatesSilentlyAsync()
    {
        await Task.Delay(1200);
        var result = await AppServices.Current.Updates.CheckForUpdatesAsync();
        if (result.IsUpdateAvailable && result.LatestRelease is { } release)
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                _pendingUpdate = result;
                UpdateAvailableButton.Content = $"Update v{release.Version}";
                UpdateAvailableButton.Visibility = Visibility.Visible;
            });
        }
    }

    private void UpdateAvailableButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null || _isUpdateDialogOpen)
        {
            return;
        }

        _isUpdateDialogOpen = true;
        try
        {
            UpdateDialogHelper.ShowUpdateDialog(this, AppServices.Current.Updates, _pendingUpdate);
        }
        finally
        {
            _isUpdateDialogOpen = false;
        }
    }
}
