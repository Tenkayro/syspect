using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Syspect.Core.Services;

namespace Syspect.App.Services;

/// <summary>
/// Captures unhandled UI, AppDomain, and Task exceptions, writes crash logs to disk,
/// and surfaces a copyable crash report window (with native Win32 clipboard + MessageBox fallback).
/// </summary>
public static class CrashHandlerService
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveableZeroInit = 0x0042;
    private const uint MbOkIconError = 0x00000010 | 0x00010000;

    private static MainWindow? _mainWindow;
    private static int _dialogActive;

    public static void RegisterMainWindow(MainWindow window)
    {
        _mainWindow = window;
    }

    public static void ReportException(string source, Exception? ex, bool isFatal = false, string? extraMessage = null)
    {
        var (report, logPath) = WriteCrashLog(source, ex, extraMessage);
        var summary = ex is not null
            ? $"{ex.GetType().Name}: {ex.Message}"
            : (!string.IsNullOrWhiteSpace(extraMessage) ? extraMessage : "Unknown fatal error.");

        Debug.WriteLine(report);

        if (Interlocked.CompareExchange(ref _dialogActive, 1, 0) != 0)
        {
            return;
        }

        if (!isFatal && _mainWindow is { IsLoaded: true } window && window.Dispatcher is { HasShutdownStarted: false } dispatcher)
        {
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    ShowCrashWindow(window, source, summary, report, logPath);
                }
                catch
                {
                    ShowWin32CrashFallback(summary, report, logPath);
                }
                finally
                {
                    Interlocked.Exchange(ref _dialogActive, 0);
                }
            });
            return;
        }

        try
        {
            ShowWin32CrashFallback(summary, report, logPath);
        }
        finally
        {
            Interlocked.Exchange(ref _dialogActive, 0);
        }
    }

    public static (string ReportText, string? LogFilePath) WriteCrashLog(string source, Exception? ex, string? extraMessage = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== syspect Crash Report ===");
        sb.AppendLine($"Timestamp:   {DateTimeOffset.Now:O}");
        sb.AppendLine($"Version:     v{UpdateService.GetCurrentVersion()}");
        sb.AppendLine($"OS:          {Environment.OSVersion}");
        sb.AppendLine($"Runtime:     {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
        sb.AppendLine($"Admin:       {SafeIsAdmin()}");
        sb.AppendLine($"Process:     {Environment.ProcessPath ?? "unknown"}");
        sb.AppendLine($"Source:      {source}");

        if (!string.IsNullOrWhiteSpace(extraMessage))
        {
            sb.AppendLine($"Details:     {extraMessage.Trim()}");
        }

        sb.AppendLine();
        if (ex is not null)
        {
            AppendExceptionChain(sb, ex, depth: 0);
        }
        else
        {
            sb.AppendLine("No managed Exception object was supplied.");
        }

        var reportText = sb.ToString();
        string? logPath = null;

        try
        {
            var logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "syspect",
                "logs");
            Directory.CreateDirectory(logsDir);
            logPath = Path.Combine(logsDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(logPath, reportText, Encoding.UTF8);
        }
        catch
        {
            logPath = null;
        }

        return (reportText, logPath);
    }

    private static void ShowCrashWindow(
        Window owner,
        string source,
        string summary,
        string reportText,
        string? logPath)
    {
        var dialog = new Window
        {
            Title = "syspect — Crash Report",
            Width = 680,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3))
        };

        WindowThemeHelper.ApplyDarkTitleBar(dialog);

        var headerText = new TextBlock
        {
            Text = $"{source} — {summary}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };

        var logLocationBox = new TextBox
        {
            Text = logPath is not null
                ? $"Saved crash log: {logPath}"
                : "Could not write crash log file to disk.",
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xA6, 0xA6)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var stackBox = new TextBox
        {
            Text = reportText,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 300,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Background = new SolidColorBrush(Color.FromRgb(0x15, 0x15, 0x15)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x35, 0x35)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 14)
        };

        var copyBtn = new Button
        {
            Content = "Copy Crash Log",
            Margin = new Thickness(0, 0, 8, 0)
        };
        if (Application.Current?.TryFindResource("AccentButtonStyle") is Style accentStyle)
        {
            copyBtn.Style = accentStyle;
        }
        copyBtn.Click += (_, _) =>
        {
            if (TryCopyToClipboardWin32(reportText))
            {
                copyBtn.Content = "Copied!";
            }
        };

        var openFolderBtn = new Button
        {
            Content = "Open Logs Folder",
            IsEnabled = !string.IsNullOrWhiteSpace(logPath),
            Margin = new Thickness(0, 0, 8, 0)
        };
        openFolderBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(logPath))
            {
                OpenFolderForFile(logPath);
            }
        };

        var closeBtn = new Button
        {
            Content = "Close",
            IsCancel = true
        };
        closeBtn.Click += (_, _) => dialog.Close();

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { openFolderBtn, copyBtn, closeBtn }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { headerText, logLocationBox, stackBox, buttonRow }
        };

        dialog.ShowDialog();
    }

    private static void ShowWin32CrashFallback(string summary, string reportText, string? logPath)
    {
        var copied = TryCopyToClipboardWin32(reportText);
        var msg = new StringBuilder();
        msg.AppendLine("syspect encountered an unexpected error:");
        msg.AppendLine();
        msg.AppendLine(summary);
        msg.AppendLine();
        if (copied)
        {
            msg.AppendLine("The full crash report has been copied to your clipboard.");
        }
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            msg.AppendLine($"Crash log saved to:\n{logPath}");
        }

        _ = MessageBoxW(0, msg.ToString(), "syspect — Crash Report", MbOkIconError);
    }

    public static bool TryCopyToClipboardWin32(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var opened = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (OpenClipboard(0))
            {
                opened = true;
                break;
            }
            Thread.Sleep(15);
        }

        if (!opened)
        {
            return false;
        }

        nint hGlobal = 0;
        try
        {
            EmptyClipboard();
            var bytes = (nuint)((text.Length + 1) * sizeof(char));
            hGlobal = GlobalAlloc(GmemMoveableZeroInit, bytes);
            if (hGlobal == 0)
            {
                return false;
            }

            var ptr = GlobalLock(hGlobal);
            if (ptr == 0)
            {
                GlobalFree(hGlobal);
                hGlobal = 0;
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(CfUnicodeText, hGlobal) == 0)
            {
                GlobalFree(hGlobal);
                hGlobal = 0;
                return false;
            }

            // Clipboard owns hGlobal once SetClipboardData succeeds.
            hGlobal = 0;
            return true;
        }
        catch
        {
            if (hGlobal != 0)
            {
                GlobalFree(hGlobal);
            }
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void OpenFolderForFile(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Ignore shell open errors.
        }
    }

    private static void AppendExceptionChain(StringBuilder sb, Exception ex, int depth)
    {
        var prefix = depth == 0 ? "Exception" : $"InnerException[{depth}]";
        sb.AppendLine($"{prefix}: {ex.GetType().FullName}");
        sb.AppendLine($"HResult:     0x{ex.HResult:X8}");
        sb.AppendLine($"Message:     {ex.Message}");
        sb.AppendLine("StackTrace:");
        sb.AppendLine(string.IsNullOrWhiteSpace(ex.StackTrace) ? "  (none)" : ex.StackTrace);

        if (ex is AggregateException agg)
        {
            for (var i = 0; i < agg.InnerExceptions.Count; i++)
            {
                sb.AppendLine();
                AppendExceptionChain(sb, agg.InnerExceptions[i], depth + 1 + i);
            }
        }
        else if (ex.InnerException is not null)
        {
            sb.AppendLine();
            AppendExceptionChain(sb, ex.InnerException, depth + 1);
        }
    }

    private static bool SafeIsAdmin()
    {
        try
        {
            return AdminHelper.IsAdministrator();
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint hWnd, string lpText, string lpCaption, uint uType);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GlobalFree(nint hMem);
}
