using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Syspect.Core.Models;
using Windows.UI;

namespace Syspect.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Collapsed;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is bool b && !b;
}

public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string s ? (!string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed)
        : value is not null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class SafetyTierToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush CleanupBg = new(Color.FromArgb(40, 16, 185, 129));
    private static readonly SolidColorBrush CautionBg = new(Color.FromArgb(42, 245, 158, 11));
    private static readonly SolidColorBrush ProtectedBg = new(Color.FromArgb(42, 239, 68, 68));
    private static readonly SolidColorBrush StandardBg = new(Color.FromArgb(30, 148, 163, 184));

    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        SafetyTier.CleanupCandidate => CleanupBg,
        SafetyTier.Caution => CautionBg,
        SafetyTier.Protected => ProtectedBg,
        _ => StandardBg
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class RepairStateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush PendingBrush = new(Color.FromArgb(30, 148, 163, 184));
    private static readonly SolidColorBrush RunningBrush = new(Color.FromArgb(45, 59, 130, 246));
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromArgb(42, 16, 185, 129));
    private static readonly SolidColorBrush WarningBrush = new(Color.FromArgb(45, 245, 158, 11));
    private static readonly SolidColorBrush FailedBrush = new(Color.FromArgb(45, 239, 68, 68));

    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        RepairStepState.Running => RunningBrush,
        RepairStepState.Succeeded => SuccessBrush,
        RepairStepState.Warning => WarningBrush,
        RepairStepState.Failed => FailedBrush,
        _ => PendingBrush
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class BoolToStatusBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OkBrush = new(Color.FromArgb(42, 16, 185, 129));
    private static readonly SolidColorBrush WarnBrush = new(Color.FromArgb(45, 245, 158, 11));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        bool flag = value is true;
        if (invert) flag = !flag;
        return flag ? OkBrush : WarnBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
