using System;
using System.Globalization;
using System.Windows.Data;

namespace FastApp
{
    public class PercentageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double percentage)
            {
                // Multiplies the percentage to fit your window width.
                // 100% * 8.5 = 850 pixels wide. 
                // You can tweak the 8.5 up or down if the bar doesn't quite fit your screen!
                return percentage * 8.5;
            }
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}