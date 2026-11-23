using System.Windows;
using System.Windows.Controls;
using Syspect.App.ViewModels;
using Syspect.Core.Models;

namespace Syspect.App.Views;

public partial class StartupServicesPage : UserControl
{
    public StartupServicesViewModel ViewModel { get; } = new();

    public StartupServicesPage()
    {
        DataContext = ViewModel;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (ViewModel.FilteredStartup.Count == 0 && ViewModel.FilteredServices.Count == 0)
            {
                await ViewModel.LoadAsync();
            }
        };
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private async void ToggleStartupButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: StartupItemViewModel item })
        {
            await ViewModel.ToggleStartupAsync(item.Model);
        }
    }

    private async void ToggleServiceStateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ServiceEntryViewModel entry })
        {
            if (entry.IsRunning && !ConfirmIfCaution(entry.Model, "stop")) return;
            await ViewModel.ToggleServiceRunningAsync(entry.Model);
        }
    }

    private async void SetServiceManualButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ServiceEntryViewModel entry })
        {
            if (!ConfirmIfCaution(entry.Model, "set to Manual")) return;
            await ViewModel.ChangeServiceStartModeAsync(entry.Model, "demand");
        }
    }

    private async void SetServiceDisabledButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ServiceEntryViewModel entry })
        {
            if (!ConfirmIfCaution(entry.Model, "disable")) return;
            await ViewModel.ChangeServiceStartModeAsync(entry.Model, "disabled");
        }
    }

    private async void SetServiceAutoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ServiceEntryViewModel entry })
        {
            await ViewModel.ChangeServiceStartModeAsync(entry.Model, "auto");
        }
    }

    private async void ToggleTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ScheduledTaskViewModel task })
        {
            await ViewModel.ToggleTaskEnabledAsync(task.Model);
        }
    }

    private async void DeleteTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ScheduledTaskViewModel task })
        {
            var result = MessageBox.Show(
                Window.GetWindow(this),
                $"Permanently delete scheduled task '{task.FullPath}'?",
                "Delete Scheduled Task?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                await ViewModel.DeleteTaskAsync(task.Model);
            }
        }
    }

    private bool ConfirmIfCaution(ServiceEntry entry, string actionText)
    {
        if (entry.SafetyTier != SafetyTier.Caution) return true;

        var result = MessageBox.Show(
            Window.GetWindow(this),
            $"{entry.CategoryNote}\n\nAre you sure you want to {actionText} this service?",
            $"Caution: '{entry.DisplayName}'",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return result == MessageBoxResult.Yes;
    }
}
