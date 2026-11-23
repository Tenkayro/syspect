using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Syspect.App.Services;
using Syspect.App.Views;
using Syspect.Core.Models;
using Syspect.Core.Services;
using Windows.Graphics;
using Windows.UI;

namespace Syspect.App;

public sealed partial class MainWindow : Window
{
    private UpdateCheckResult? _pendingUpdate;
    private bool _isUpdateDialogOpen;

    internal XamlRoot? DialogXamlRoot => NavView?.XamlRoot;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();

        ConfigureWindow();
        UpdateAdminHeader();

        ContentFrame.NavigationFailed += (_, args) =>
        {
            args.Handled = true;
            CrashHandlerService.ReportException($"Frame.NavigationFailed ({args.SourcePageType?.Name})", args.Exception);
        };

        AppServices.Current.NavigateRequested = NavigateToPage;
        NavView.SelectedItem = NavView.MenuItems[0];
        NavView.Loaded += NavView_Loaded;
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (AppWindow is not null)
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "syspect.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }

            AppWindow.Resize(new SizeInt32(1280, 840));
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            if (area is not null)
            {
                var x = area.WorkArea.X + (area.WorkArea.Width - 1280) / 2;
                var y = area.WorkArea.Y + (area.WorkArea.Height - 840) / 2;
                AppWindow.Move(new PointInt32(Math.Max(0, x), Math.Max(0, y)));
            }

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = 980;
                presenter.PreferredMinimumHeight = 660;
            }
        }
    }

    private void UpdateAdminHeader()
    {
        if (AdminHelper.IsAdministrator())
        {
            AdminBadge.Background = new SolidColorBrush(Color.FromArgb(45, 16, 185, 129));
            AdminBadgeText.Text = "Admin";
            ElevateButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            AdminBadge.Background = new SolidColorBrush(Color.FromArgb(50, 245, 158, 11));
            AdminBadgeText.Text = "Standard User";
            ElevateButton.Visibility = Visibility.Visible;
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
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

            if (ContentFrame.CurrentSourcePageType != pageType)
            {
                try
                {
                    ContentFrame.Navigate(pageType);
                }
                catch (Exception ex)
                {
                    CrashHandlerService.ReportException($"Navigate ({pageType.Name})", ex);
                }
            }
        }
    }

    private void NavigateToPage(Type pageType, object? parameter)
    {
        var tag = pageType.Name switch
        {
            nameof(DashboardPage) => "Dashboard",
            nameof(RepairPage) => "Repair",
            nameof(DriversPage) => "Drivers",
            nameof(StartupServicesPage) => "StartupServices",
            nameof(FirewallPage) => "Firewall",
            nameof(LogsPage) => "Logs",
            _ => "Dashboard"
        };

        foreach (var obj in NavView.MenuItems)
        {
            if (obj is NavigationViewItem navItem && Equals(navItem.Tag, tag))
            {
                NavView.SelectedItem = navItem;
                break;
            }
        }

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            try
            {
                ContentFrame.Navigate(pageType, parameter);
            }
            catch (Exception ex)
            {
                CrashHandlerService.ReportException($"Navigate ({pageType.Name})", ex);
            }
        }
    }

    private void ElevateButton_Click(object sender, RoutedEventArgs e)
    {
        if (AdminHelper.TryRestartElevated())
        {
            Application.Current.Exit();
        }
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        NavView.Loaded -= NavView_Loaded;
        _ = CheckForUpdatesSilentlyAsync();
    }

    private async Task CheckForUpdatesSilentlyAsync()
    {
        await Task.Delay(1200);
        var result = await AppServices.Current.Updates.CheckForUpdatesAsync();
        if (result.IsUpdateAvailable && result.LatestRelease is { } release)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _pendingUpdate = result;
                UpdateAvailableText.Text = $"Update v{release.Version}";
                UpdateAvailableButton.Visibility = Visibility.Visible;
            });
        }
    }

    private async void UpdateAvailableButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null || _isUpdateDialogOpen || NavView.XamlRoot is null)
        {
            return;
        }

        _isUpdateDialogOpen = true;
        try
        {
            await UpdateDialogHelper.ShowUpdateDialogAsync(NavView.XamlRoot, AppServices.Current.Updates, _pendingUpdate);
        }
        finally
        {
            _isUpdateDialogOpen = false;
        }
    }
}
