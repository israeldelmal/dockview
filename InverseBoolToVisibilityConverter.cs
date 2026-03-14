using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dockview;

/// <summary>
/// Converts bool → Visibility: false → Visible, true → Collapsed.
/// Used to show the "No Signal" overlay when IsCapturing is false.
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
