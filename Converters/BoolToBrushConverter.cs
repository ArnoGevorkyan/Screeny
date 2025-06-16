using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using Microsoft.UI;

namespace ScreenTimeTracker
{
    public sealed class BoolToBrushConverter : IValueConverter
    {
        public string? TrueBrushResourceKey { get; set; } = "AccentFillColorDefaultBrush";
        public string? FalseBrushResourceKey { get; set; } = "TextFillColorSecondaryBrush";

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool flag = value is bool b && b;
            var key = flag ? TrueBrushResourceKey : FalseBrushResourceKey;
            return Application.Current.Resources[key] as Brush ?? new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
} 