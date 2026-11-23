using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Syspect.App.Services;
using Syspect.Core.Models;
using Syspect.Core.Services;

namespace Syspect.App.Views;

/// <summary>
/// Shared WPF modal dialog flow for displaying release notes, downloading, verifying SHA-256, and launching MSI updates.
/// </summary>
public static class UpdateDialogHelper
{
    public static void ShowUpdateDialog(
        Window? owner,
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

        var dialog = new Window
        {
            Title = $"Update Available — {release.ReleaseName}",
            Width = 540,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3))
        };

        WindowThemeHelper.ApplyDarkTitleBar(dialog);

        var summaryText = new TextBlock
        {
            Text = $"Installed: v{checkResult.CurrentVersion}   ->   Latest: v{release.Version}{sizeMb}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var notesContent = string.IsNullOrWhiteSpace(release.ReleaseNotes)
            ? "No release notes were provided for this release."
            : release.ReleaseNotes.Trim();

        var notesBox = new TextBox
        {
            Text = notesContent,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 180,
            Background = new SolidColorBrush(Color.FromRgb(0x15, 0x15, 0x15)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x35, 0x35)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var statusText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xA6, 0xA6)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };

        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 6,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE9, 0x74, 0x51)),
            Background = new SolidColorBrush(Color.FromRgb(0x32, 0x32, 0x32))
        };

        var progressPanel = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 10),
            Children = { statusText, progressBar }
        };

        var warningBanner = new BannerControl
        {
            Severity = hasMsi ? BannerSeverity.Error : BannerSeverity.Warning,
            IsOpen = !hasMsi,
            Message = hasMsi
                ? string.Empty
                : "No .msi installer asset is attached to this release. Click Open GitHub to download manually.",
            Margin = new Thickness(0, 0, 0, 10)
        };

        var downloadBtn = new Button
        {
            Content = "Download & Install",
            IsEnabled = hasMsi,
            Margin = new Thickness(0, 0, 8, 0)
        };
        if (Application.Current?.TryFindResource("AccentButtonStyle") is Style accentStyle)
        {
            downloadBtn.Style = accentStyle;
        }

        var openGithubBtn = new Button
        {
            Content = "Open GitHub",
            Margin = new Thickness(0, 0, 8, 0)
        };

        var closeBtn = new Button
        {
            Content = "Later",
            IsCancel = true
        };

        using var downloadCts = new CancellationTokenSource();
        var shouldExitForInstaller = false;
        var isDownloading = false;

        dialog.Closing += (_, _) => downloadCts.Cancel();

        closeBtn.Click += (_, _) =>
        {
            downloadCts.Cancel();
            dialog.Close();
        };

        openGithubBtn.Click += (_, _) => UpdateService.OpenUrlInBrowser(release.HtmlUrl);

        downloadBtn.Click += async (_, _) =>
        {
            if (isDownloading) return;
            isDownloading = true;

            downloadBtn.IsEnabled = false;
            openGithubBtn.IsEnabled = false;
            closeBtn.Content = "Cancel";
            progressPanel.Visibility = Visibility.Visible;
            progressBar.IsIndeterminate = false;
            progressBar.Value = 0;
            statusText.Text = $"Downloading {release.MsiFileName ?? $"syspect-{release.Version}-win-x64.msi"}...";
            warningBanner.IsOpen = false;

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
                    dialog.Close();
                }
                else
                {
                    progressPanel.Visibility = Visibility.Collapsed;
                    warningBanner.Severity = BannerSeverity.Error;
                    warningBanner.Message = launchError ?? "Could not start Windows Installer.";
                    warningBanner.IsOpen = true;
                    downloadBtn.IsEnabled = true;
                    openGithubBtn.IsEnabled = true;
                    closeBtn.Content = "Later";
                }
            }
            catch (OperationCanceledException)
            {
                progressPanel.Visibility = Visibility.Collapsed;
                downloadBtn.IsEnabled = hasMsi;
                openGithubBtn.IsEnabled = true;
                closeBtn.Content = "Later";
            }
            catch (Exception ex)
            {
                progressPanel.Visibility = Visibility.Collapsed;
                warningBanner.Severity = BannerSeverity.Error;
                warningBanner.Message = ex.Message;
                warningBanner.IsOpen = true;
                downloadBtn.IsEnabled = hasMsi;
                openGithubBtn.IsEnabled = true;
                closeBtn.Content = "Later";
            }
            finally
            {
                isDownloading = false;
            }
        };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { openGithubBtn, downloadBtn, closeBtn }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { summaryText, notesBox, progressPanel, warningBanner, buttonRow }
        };

        dialog.ShowDialog();

        if (shouldExitForInstaller)
        {
            Application.Current?.Shutdown();
        }
    }
}
