using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FastApp
{
    // Int (e.g. a collection's Count) -> Visibility, Visible when the count is
    // zero. Pass ConverterParameter="Invert" to flip that (Visible when non-zero).
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isZero = value is int i && i == 0;
            if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) isZero = !isZero;
            return isZero ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
