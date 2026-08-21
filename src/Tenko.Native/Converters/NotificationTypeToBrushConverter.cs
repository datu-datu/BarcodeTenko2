using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Tenko.Native.Services;

namespace Tenko.Native
{
    public class NotificationTypeToBrushConverter : IValueConverter
    {
        // 通知種別に応じて表示色のブラシを返す。
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NotificationType type)
            {
                return type switch
                {
                    NotificationType.Success => Brushes.Green,
                    NotificationType.Warning => Brushes.Orange,
                    NotificationType.Error => Brushes.Red,
                    _ => Brushes.Gray
                };
            }
            return Brushes.Gray;
        }

        // 逆変換は使用しない。
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
