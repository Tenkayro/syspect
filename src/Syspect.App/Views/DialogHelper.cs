using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Syspect.App.Services;

namespace Syspect.App.Views;

/// <summary>
/// Dark-themed WPF modal confirmation dialogs matching syspect's Fluent styling.
/// </summary>
public static class DialogHelper
{
    public static bool ShowConfirmDialog(
        Window? owner,
        string title,
        string message,
        string confirmText,
        string cancelText = "Cancel",
        bool isPrimaryAccent = true)
    {
        bool confirmed = false;

        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            Background = TryFindBrush("AppBackgroundBrush", Color.FromRgb(0x1C, 0x1C, 0x1C)),
            Foreground = TryFindBrush("TextPrimaryBrush", Color.FromRgb(0xF3, 0xF3, 0xF3))
        };

        WindowThemeHelper.ApplyDarkTitleBar(dialog);

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var messageBlock = new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = TryFindBrush("TextSecondaryBrush", Color.FromRgb(0xD0, 0xD0, 0xD0)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        };

        var cancelBtn = new Button
        {
            Content = cancelText,
            MinWidth = 90,
            IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelBtn.Click += (_, _) =>
        {
            confirmed = false;
            dialog.DialogResult = false;
        };

        var confirmBtn = new Button
        {
            Content = confirmText,
            MinWidth = 100,
            IsDefault = true
        };
        if (isPrimaryAccent && Application.Current.TryFindResource("AccentButtonStyle") is Style accentStyle)
        {
            confirmBtn.Style = accentStyle;
        }
        confirmBtn.Click += (_, _) =>
        {
            confirmed = true;
            dialog.DialogResult = true;
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancelBtn, confirmBtn }
        };

        var layout = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { titleBlock, messageBlock, buttons }
        };

        dialog.Content = layout;
        dialog.ShowDialog();
        return confirmed;
    }

    private static Brush TryFindBrush(string resourceKey, Color fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey) is Brush brush)
        {
            return brush;
        }
        var b = new SolidColorBrush(fallback);
        b.Freeze();
        return b;
    }
}
