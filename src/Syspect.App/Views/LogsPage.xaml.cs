using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Syspect.App.Services;
using Syspect.App.ViewModels;
using Syspect.Core.Services;

namespace Syspect.App.Views;

public partial class LogsPage : UserControl
{
    public LogsViewModel ViewModel { get; } = new();

    public LogsPage()
    {
        DataContext = ViewModel;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (ViewModel.LogFiles.Count == 0)
            {
                await ViewModel.LoadAsync();
            }
        };
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private void CopyContentButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ViewModel.SelectedLogContent)) return;
        if (!CrashHandlerService.TryCopyToClipboardWin32(ViewModel.SelectedLogContent))
        {
            Clipboard.SetText(ViewModel.SelectedLogContent);
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dir = HealthRepairService.DefaultAppLogDirectory;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }
}
