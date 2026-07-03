using System;
using System.Globalization;
using System.Windows.Data;

namespace FastApp
{
    public class DietWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            // values[0] is the Percentage (e.g., 50%)
            // values[1] is the ActualWidth of the UI container (e.g., 900 pixels)
            if (values[0] is double percentage && values[1] is double totalWidth)
            {
                return (percentage / 100.0) * totalWidth;
            }
            return 0;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;
    }
}