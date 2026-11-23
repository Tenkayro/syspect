using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syspect.App.ViewModels;

namespace Syspect.App.Views;

public sealed partial class DriversPage : Page
{
    public DriversViewModel ViewModel { get; } = new();

    public DriversPage()
    {
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
