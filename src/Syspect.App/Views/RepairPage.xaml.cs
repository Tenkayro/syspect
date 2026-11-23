using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syspect.App.ViewModels;

namespace Syspect.App.Views;

public sealed partial class RepairPage : Page
{
    public RepairViewModel ViewModel { get; } = new();

    public RepairPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (ViewModel.CbsReport is null)
            {
                await ViewModel.LoadCbsSummaryAsync();
            }
        };
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.StartPipelineAsync(
            DispatcherQueue,
            () => ConsoleScrollViewer.ChangeView(null, ConsoleScrollViewer.ScrollableHeight, null, disableAnimation: true));
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelPipeline();
    }

    private async void ScheduleChkdskButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Schedule chkdsk C: /f on Next Reboot?",
            Content = "Windows will verify and repair the NTFS filesystem of drive C: before booting on the next restart.",
            PrimaryButtonText = "Schedule",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ScheduleChkdskRebootAsync();
        }
    }

    private async void RefreshCbsButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadCbsSummaryAsync();
    }
}
