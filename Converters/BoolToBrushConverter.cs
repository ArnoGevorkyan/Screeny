using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace ScreenTimeTracker
{
    public sealed class BoolToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isTracking && isTracking)
            {
                var accentColor = (Windows.UI.Color)Application.Current.Resources["SystemAccentColorLight1"];
                return new SolidColorBrush(accentColor);
            }
            else
            {
                // Paused state: Use a neutral gray color.
                return new SolidColorBrush(Colors.Gray);
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
} 