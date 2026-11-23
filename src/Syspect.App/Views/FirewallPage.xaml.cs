using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syspect.App.ViewModels;

namespace Syspect.App.Views;

public sealed partial class FirewallPage : Page
{
    public FirewallViewModel ViewModel { get; } = new();

    public FirewallPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (ViewModel.AuditResult is null)
            {
                await ViewModel.LoadAsync();
            }
        };
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private void FwTabSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not null)
        {
            ViewModel.SelectedTabIndex = sender.Items.IndexOf(sender.SelectedItem);
        }
    }

    private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var count = ViewModel.OrphanedRules.Count(r => r.IsChecked);
        if (count == 0)
        {
            await ViewModel.DeleteSelectedOrphanedAsync();
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Delete Selected Firewall Rules?",
            Content = $"Permanently delete {count} selected orphaned firewall rule(s)?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteSelectedOrphanedAsync();
        }
    }

    private async void CleanupCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DeleteCleanupCandidatesAsync();
    }
}
