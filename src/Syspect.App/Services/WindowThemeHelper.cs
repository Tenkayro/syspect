using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Syspect.App.Services;

/// <summary>
/// Applies Windows 11 DWM immersive dark mode attributes to WPF window title bars and borders.
/// </summary>
public static class WindowThemeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;

    // COLORREF is 0x00BBGGRR: matches #181818 top bar and #333333 border.
    private const uint DarkCaptionColorRef = 0x00181818;
    private const uint DarkBorderColorRef = 0x00333333;

    public static void ApplyDarkTitleBar(Window window)
    {
        if (window is null)
        {
            return;
        }

        var helper = new WindowInteropHelper(window);
        if (helper.Handle != 0)
        {
            ApplyToHandle(helper.Handle);
            return;
        }

        void OnSourceInitialized(object? sender, EventArgs args)
        {
            window.SourceInitialized -= OnSourceInitialized;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != 0)
            {
                ApplyToHandle(hwnd);
            }
        }

        window.SourceInitialized += OnSourceInitialized;
    }

    private static void ApplyToHandle(nint hwnd)
    {
        try
        {
            int dark = 1;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
            }

            uint captionColor = DarkCaptionColorRef;
            _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColor, sizeof(uint));

            uint borderColor = DarkBorderColorRef;
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(uint));
        }
        catch
        {
            // Safe no-op on older Windows builds where custom DWM caption attributes are unsupported.
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);
}
