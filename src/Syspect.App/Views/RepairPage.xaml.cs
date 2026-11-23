using System.Windows;
using System.Windows.Controls;
using Syspect.App.ViewModels;

namespace Syspect.App.Views;

public partial class RepairPage : UserControl
{
    public RepairViewModel ViewModel { get; } = new();

    public RepairPage()
    {
        DataContext = ViewModel;
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
            Dispatcher,
            () => ConsoleTextBox.ScrollToEnd());
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelPipeline();
    }

    private async void ScheduleChkdskButton_Click(object sender, RoutedEventArgs e)
    {
        var ok = DialogHelper.ShowConfirmDialog(
            Window.GetWindow(this),
            "Schedule chkdsk C: /f on Next Reboot?",
            "Windows will verify and repair the NTFS filesystem of drive C: before booting on the next restart.",
            "Schedule");

        if (ok)
        {
            await ViewModel.ScheduleChkdskRebootAsync();
        }
    }

    private async void RefreshCbsButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadCbsSummaryAsync();
    }
}
