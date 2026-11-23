using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Syspect.Core.Models;

namespace Syspect.App
{
    public enum BannerSeverity
    {
        Info,
        Success,
        Warning,
        Error
    }
}

namespace Syspect.App.Converters
{
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is Visibility.Visible;
    }

    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is Visibility.Collapsed;
    }

    public sealed class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool b && !b;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool b && !b;
    }

    public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is string s ? (!string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed)
            : value is not null ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public sealed class SafetyTierToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush CleanupBg = CreateFrozen(44, 16, 185, 129);
        private static readonly SolidColorBrush CautionBg = CreateFrozen(48, 245, 158, 11);
        private static readonly SolidColorBrush ProtectedBg = CreateFrozen(48, 239, 68, 68);
        private static readonly SolidColorBrush StandardBg = CreateFrozen(34, 148, 163, 184);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
        {
            SafetyTier.CleanupCandidate => CleanupBg,
            SafetyTier.Caution => CautionBg,
            SafetyTier.Protected => ProtectedBg,
            _ => StandardBg
        };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        private static SolidColorBrush CreateFrozen(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    public sealed class RepairStateToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush PendingBrush = CreateFrozen(34, 148, 163, 184);
        private static readonly SolidColorBrush RunningBrush = CreateFrozen(52, 59, 130, 246);
        private static readonly SolidColorBrush SuccessBrush = CreateFrozen(46, 16, 185, 129);
        private static readonly SolidColorBrush WarningBrush = CreateFrozen(50, 245, 158, 11);
        private static readonly SolidColorBrush FailedBrush = CreateFrozen(50, 239, 68, 68);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
        {
            RepairStepState.Running => RunningBrush,
            RepairStepState.Succeeded => SuccessBrush,
            RepairStepState.Warning => WarningBrush,
            RepairStepState.Failed => FailedBrush,
            _ => PendingBrush
        };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        private static SolidColorBrush CreateFrozen(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    public sealed class BoolToStatusBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush OkBrush = CreateFrozen(46, 16, 185, 129);
        private static readonly SolidColorBrush WarnBrush = CreateFrozen(50, 245, 158, 11);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
            bool flag = value is true;
            if (invert) flag = !flag;
            return flag ? OkBrush : WarnBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        private static SolidColorBrush CreateFrozen(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    public sealed class BannerSeverityToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush InfoBg = CreateFrozen(38, 59, 130, 246);
        private static readonly SolidColorBrush SuccessBg = CreateFrozen(38, 16, 185, 129);
        private static readonly SolidColorBrush WarningBg = CreateFrozen(42, 245, 158, 11);
        private static readonly SolidColorBrush ErrorBg = CreateFrozen(44, 239, 68, 68);

        private static readonly SolidColorBrush InfoBorder = CreateFrozen(115, 59, 130, 246);
        private static readonly SolidColorBrush SuccessBorder = CreateFrozen(115, 16, 185, 129);
        private static readonly SolidColorBrush WarningBorder = CreateFrozen(120, 245, 158, 11);
        private static readonly SolidColorBrush ErrorBorder = CreateFrozen(125, 239, 68, 68);

        private static readonly SolidColorBrush InfoAccent = CreateFrozen(255, 96, 165, 250);
        private static readonly SolidColorBrush SuccessAccent = CreateFrozen(255, 52, 211, 153);
        private static readonly SolidColorBrush WarningAccent = CreateFrozen(255, 251, 191, 36);
        private static readonly SolidColorBrush ErrorAccent = CreateFrozen(255, 248, 113, 113);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var sev = value is BannerSeverity s ? s : BannerSeverity.Info;
            var mode = parameter as string ?? "Background";

            if (string.Equals(mode, "Border", StringComparison.OrdinalIgnoreCase))
            {
                return sev switch
                {
                    BannerSeverity.Success => SuccessBorder,
                    BannerSeverity.Warning => WarningBorder,
                    BannerSeverity.Error => ErrorBorder,
                    _ => InfoBorder
                };
            }

            if (string.Equals(mode, "Accent", StringComparison.OrdinalIgnoreCase))
            {
                return sev switch
                {
                    BannerSeverity.Success => SuccessAccent,
                    BannerSeverity.Warning => WarningAccent,
                    BannerSeverity.Error => ErrorAccent,
                    _ => InfoAccent
                };
            }

            return sev switch
            {
                BannerSeverity.Success => SuccessBg,
                BannerSeverity.Warning => WarningBg,
                BannerSeverity.Error => ErrorBg,
                _ => InfoBg
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        private static SolidColorBrush CreateFrozen(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    public sealed class BannerSeverityToGlyphConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
        {
            BannerSeverity.Success => "\uE73E",
            BannerSeverity.Warning => "\uE7BA",
            BannerSeverity.Error => "\uEA39",
            _ => "\uE946"
        };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
