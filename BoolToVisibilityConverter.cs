using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FastApp
{
    // Bool -> Visibility. Pass ConverterParameter="Invert" to flip which value
    // maps to Visible vs Collapsed, instead of needing a second converter class.
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool flag = value is bool b && b;
            if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
