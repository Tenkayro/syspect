using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syspect.Core.Models;
using Syspect.Core.Services;

namespace Syspect.App.Views;

/// <summary>
/// Shared WinUI 3 ContentDialog flow for displaying release notes and installing MSI updates.
/// </summary>
public static class UpdateDialogHelper
{
    public static async Task ShowUpdateDialogAsync(
        XamlRoot xamlRoot,
        UpdateService updateService,
        UpdateCheckResult checkResult)
    {
        if (checkResult.LatestRelease is not { } release)
        {
            return;
        }

        var hasMsi = !string.IsNullOrWhiteSpace(release.MsiDownloadUrl);
        var sizeMb = release.MsiSizeBytes > 0
            ? $" ({release.MsiSizeBytes / (1024d * 1024d):F1} MB)"
            : string.Empty;

        var summaryText = new TextBlock
        {
            Text = $"Installed: v{checkResult.CurrentVersion}   ->   Latest: v{release.Version}{sizeMb}",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };

        var notesContent = string.IsNullOrWhiteSpace(release.ReleaseNotes)
            ? "No release notes were provided for this release."
            : release.ReleaseNotes.Trim();

        var notesScroller = new ScrollViewer
        {
            MaxHeight = 200,
            Content = new TextBlock
            {
                Text = notesContent,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            }
        };

        var statusText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0
        };

        var progressPanel = new StackPanel
        {
            Spacing = 6,
            Visibility = Visibility.Collapsed,
            Children = { statusText, progressBar }
        };

        var errorInfoBar = new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsClosable = false,
            IsOpen = !hasMsi,
            Message = hasMsi
                ? string.Empty
                : "No .msi installer asset is attached to this release. Click Open GitHub to download manually."
        };

        var contentStack = new StackPanel
        {
            Spacing = 12,
            MinWidth = 420,
            Children =
            {
                summaryText,
                notesScroller,
                progressPanel,
                errorInfoBar
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = $"Update Available - {release.ReleaseName}",
            Content = contentStack,
            PrimaryButtonText = "Download & Install",
            SecondaryButtonText = "Open GitHub",
            CloseButtonText = "Later",
            DefaultButton = hasMsi ? ContentDialogButton.Primary : ContentDialogButton.Secondary,
            IsPrimaryButtonEnabled = hasMsi
        };

        using var downloadCts = new CancellationTokenSource();
        var shouldExitForInstaller = false;

        dialog.CloseButtonClick += (_, _) =>
        {
            downloadCts.Cancel();
        };

        dialog.SecondaryButtonClick += (_, _) =>
        {
            UpdateService.OpenUrlInBrowser(release.HtmlUrl);
        };

        dialog.PrimaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral();
            args.Cancel = true;

            sender.IsPrimaryButtonEnabled = false;
            sender.IsSecondaryButtonEnabled = false;
            sender.CloseButtonText = "Cancel";
            progressPanel.Visibility = Visibility.Visible;
            progressBar.IsIndeterminate = false;
            progressBar.Value = 0;
            statusText.Text = $"Downloading {release.MsiFileName ?? $"syspect-{release.Version}-win-x64.msi"}...";
            errorInfoBar.IsOpen = false;

            try
            {
                var progress = new Progress<double>(pct =>
                {
                    if (pct < 0)
                    {
                        progressBar.IsIndeterminate = true;
                    }
                    else
                    {
                        progressBar.IsIndeterminate = false;
                        progressBar.Value = pct;
                        statusText.Text = pct >= 99.9
                            ? "Verifying SHA-256 checksum..."
                            : $"Downloading installer... {pct:F0}%";
                    }
                });

                var msiPath = await updateService.DownloadAndVerifyMsiAsync(release, progress, downloadCts.Token);
                statusText.Text = "Starting Windows Installer...";

                if (UpdateService.TryLaunchMsiUpgrade(msiPath, out var launchError))
                {
                    shouldExitForInstaller = true;
                    args.Cancel = false;
                }
                else
                {
                    progressPanel.Visibility = Visibility.Collapsed;
                    errorInfoBar.Severity = InfoBarSeverity.Error;
                    errorInfoBar.Message = launchError ?? "Could not start Windows Installer.";
                    errorInfoBar.IsOpen = true;
                    sender.IsPrimaryButtonEnabled = true;
                    sender.IsSecondaryButtonEnabled = true;
                    sender.CloseButtonText = "Later";
                }
            }
            catch (OperationCanceledException)
            {
                progressPanel.Visibility = Visibility.Collapsed;
                sender.IsPrimaryButtonEnabled = hasMsi;
                sender.IsSecondaryButtonEnabled = true;
                sender.CloseButtonText = "Later";
            }
            catch (Exception ex)
            {
                progressPanel.Visibility = Visibility.Collapsed;
                errorInfoBar.Severity = InfoBarSeverity.Error;
                errorInfoBar.Message = ex.Message;
                errorInfoBar.IsOpen = true;
                sender.IsPrimaryButtonEnabled = hasMsi;
                sender.IsSecondaryButtonEnabled = true;
                sender.CloseButtonText = "Later";
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();

        if (shouldExitForInstaller)
        {
            Application.Current.Exit();
        }
    }
}
