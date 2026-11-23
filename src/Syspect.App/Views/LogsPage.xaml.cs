using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syspect.App.ViewModels;
using Syspect.Core.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Syspect.App.Views;

public sealed partial class LogsPage : Page
{
    public LogsViewModel ViewModel { get; } = new();

    public LogsPage()
    {
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
        var pkg = new DataPackage();
        pkg.SetText(ViewModel.SelectedLogContent);
        Clipboard.SetContent(pkg);
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dir = HealthRepairService.DefaultAppLogDirectory;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }
}
