using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Syspect.App.Services;
using Syspect.App.Views;
using Syspect.Core.Models;
using Syspect.Core.Services;

namespace Syspect.App;

public partial class MainWindow : Window
{
    private readonly Dictionary<Type, UserControl> _pageCache = [];
    private Type? _currentPageType;
    private UpdateCheckResult? _pendingUpdate;
    private bool _isUpdateDialogOpen;

    public MainWindow()
    {
        InitializeComponent();
        WindowThemeHelper.ApplyDarkTitleBar(this);
        UpdateAdminHeader();

        AppServices.Current.NavigateRequested = NavigateToPage;
        NavDashboard.IsChecked = true;
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

    private void NavItem_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            var pageType = tag switch
            {
                "Dashboard" => typeof(DashboardPage),
                "Repair" => typeof(RepairPage),
                "Drivers" => typeof(DriversPage),
                "StartupServices" => typeof(StartupServicesPage),
                "Firewall" => typeof(FirewallPage),
                "Logs" => typeof(LogsPage),
                _ => typeof(DashboardPage)
            };

            ShowPage(pageType);
        }
    }

    private void NavigateToPage(Type pageType, object? parameter)
    {
        var navBtn = pageType.Name switch
        {
            nameof(DashboardPage) => NavDashboard,
            nameof(RepairPage) => NavRepair,
            nameof(DriversPage) => NavDrivers,
            nameof(StartupServicesPage) => NavStartupServices,
            nameof(FirewallPage) => NavFirewall,
            nameof(LogsPage) => NavLogs,
            _ => NavDashboard
        };

        if (navBtn.IsChecked != true)
        {
            navBtn.IsChecked = true;
        }
        else
        {
            ShowPage(pageType);
        }
    }

    private void ShowPage(Type pageType)
    {
        if (_currentPageType == pageType)
        {
            return;
        }

        try
        {
            if (!_pageCache.TryGetValue(pageType, out var view))
            {
                view = CreateViewForType(pageType);
                _pageCache[pageType] = view;
            }

            _currentPageType = pageType;
            PageHost.Content = view;
        }
        catch (Exception ex)
        {
            CrashHandlerService.ReportException($"Navigate ({pageType.Name})", ex);
        }
    }

    private static UserControl CreateViewForType(Type pageType) => pageType.Name switch
    {
        nameof(DashboardPage) => new DashboardPage(),
        nameof(RepairPage) => new RepairPage(),
        nameof(DriversPage) => new DriversPage(),
        nameof(StartupServicesPage) => new StartupServicesPage(),
        nameof(FirewallPage) => new FirewallPage(),
        nameof(LogsPage) => new LogsPage(),
        _ => new DashboardPage()
    };

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
                UpdateAvailableText.Text = $"Update v{release.Version}";
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
