using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Tenko.Native.Services;

namespace Tenko.Native
{
    public class NotificationTypeToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NotificationType type)
            {
                return type switch
                {
                    NotificationType.Success => Brushes.Green,
                    NotificationType.Warning => Brushes.Orange,
                    NotificationType.Error => Brushes.Red,
                    _ => Brushes.Transparent
                };
            }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
