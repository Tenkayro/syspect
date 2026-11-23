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

    private void FwTabRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out int index))
        {
            ViewModel.SelectedTabIndex = index;
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

        var ok = DialogHelper.ShowConfirmDialog(
            Window.GetWindow(this),
            "Delete Selected Firewall Rules?",
            $"Permanently delete {count} selected orphaned firewall rule(s)?",
            "Delete");

        if (ok)
        {
            await ViewModel.DeleteSelectedOrphanedAsync();
        }
    }

    private async void CleanupCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DeleteCleanupCandidatesAsync();
    }
}
