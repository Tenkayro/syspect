using System.Windows;
using System.Windows.Controls;
using Syspect.App.ViewModels;

namespace Syspect.App.Views;

public partial class DriversPage : UserControl
{
    public DriversViewModel ViewModel { get; } = new();

    public DriversPage()
    {
        DataContext = ViewModel;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (ViewModel.TotalCount == 0)
            {
                await ViewModel.LoadAsync();
            }
        };
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }
}
