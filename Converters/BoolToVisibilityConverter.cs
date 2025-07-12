using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace ScreenTimeTracker
{
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool isVisible = (value is bool boolValue) && boolValue;
            if (parameter is string param && param == "Invert")
            {
                isVisible = !isVisible;
            }
            return isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
} 