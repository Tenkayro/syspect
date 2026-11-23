using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syspect.App.ViewModels;
using Syspect.Core.Models;

namespace Syspect.App.Views;

public sealed partial class StartupServicesPage : Page
{
    public StartupServicesViewModel ViewModel { get; } = new();

    public StartupServicesPage()
    {
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

    private void TabSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not null)
        {
            ViewModel.SelectedTabIndex = sender.Items.IndexOf(sender.SelectedItem);
        }
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
            if (entry.IsRunning && !await ConfirmIfCautionAsync(entry.Model, "stop")) return;
            await ViewModel.ToggleServiceRunningAsync(entry.Model);
        }
    }

    private async void SetServiceManualButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ServiceEntryViewModel entry })
        {
            if (!await ConfirmIfCautionAsync(entry.Model, "set to Manual")) return;
            await ViewModel.ChangeServiceStartModeAsync(entry.Model, "demand");
        }
    }

    private async void SetServiceDisabledButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ServiceEntryViewModel entry })
        {
            if (!await ConfirmIfCautionAsync(entry.Model, "disable")) return;
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
            var dialog = new ContentDialog
            {
                Title = "Delete Scheduled Task?",
                Content = $"Permanently delete scheduled task '{task.FullPath}'?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.DeleteTaskAsync(task.Model);
            }
        }
    }

    private async Task<bool> ConfirmIfCautionAsync(ServiceEntry entry, string actionText)
    {
        if (entry.SafetyTier != SafetyTier.Caution) return true;

        var dialog = new ContentDialog
        {
            Title = $"Caution: '{entry.DisplayName}'",
            Content = $"{entry.CategoryNote}\n\nAre you sure you want to {actionText} this service?",
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
