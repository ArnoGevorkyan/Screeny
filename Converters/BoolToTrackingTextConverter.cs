using Microsoft.UI.Xaml.Data;
using System;

namespace ScreenTimeTracker
{
    public sealed class BoolToTrackingTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is bool b && b ? "Active" : "Paused";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
} 