using Microsoft.UI.Xaml.Data;
using System;
using ScreenTimeTracker.Models;

namespace ScreenTimeTracker
{
    /// <summary>
    /// Converts ChartViewMode enum to display label ("Hourly View" / "Daily View").
    /// </summary>
    public sealed class ChartViewModeToLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is ChartViewMode mode
                ? (mode == ChartViewMode.Hourly ? "Hourly View" : "Daily View")
                : string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
} 