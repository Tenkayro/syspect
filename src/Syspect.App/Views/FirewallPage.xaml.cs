using System.Windows;
using System.Windows.Controls;
using Syspect.App.ViewModels;

namespace Syspect.App.Views;

public partial class FirewallPage : UserControl
{
    public FirewallViewModel ViewModel { get; } = new();

    public FirewallPage()
    {
        DataContext = ViewModel;
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

    private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var count = ViewModel.OrphanedRules.Count(r => r.IsChecked);
        if (count == 0)
        {
            await ViewModel.DeleteSelectedOrphanedAsync();
            return;
        }

        var result = MessageBox.Show(
            Window.GetWindow(this),
            $"Permanently delete {count} selected orphaned firewall rule(s)?",
            "Delete Selected Firewall Rules?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            await ViewModel.DeleteSelectedOrphanedAsync();
        }
    }

    private async void CleanupCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DeleteCleanupCandidatesAsync();
    }
}
