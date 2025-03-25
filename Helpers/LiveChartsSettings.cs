using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using SkiaSharp;

namespace ScreenTimeTracker
{
    /// <summary>
    /// Static class to configure LiveCharts global settings
    /// </summary>
    public static class LiveChartsSettings
    {
        /// <summary>
        /// Configures the default theme and settings for LiveCharts
        /// </summary>
        public static void ConfigureTheme()
        {
            // Configure LiveCharts global settings
            LiveCharts.Configure(config =>
                config
                    // Register types for the view
                    .AddSkiaSharp()
                    
                    // Basic setup - default colors and settings
                    .AddDefaultMappers()
            );
        }
    }
} 