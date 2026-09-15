using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Flow.Launcher.Converters;

/// <summary>
/// Builds a rounded rectangle clip from (ActualWidth, ActualHeight, CornerRadius),
/// so content such as the result glow is clipped to the row's rounded shape.
/// </summary>
public class RoundedRectClipConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [double width, double height, CornerRadius radius])
        {
            return DependencyProperty.UnsetValue;
        }

        if (width < double.Epsilon || height < double.Epsilon)
        {
            return Geometry.Empty;
        }

        var geometry = new RectangleGeometry(new Rect(0, 0, width, height), radius.TopLeft, radius.TopLeft);
        geometry.Freeze();
        return geometry;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
