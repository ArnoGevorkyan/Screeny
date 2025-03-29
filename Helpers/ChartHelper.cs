using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using ScreenTimeTracker.Models;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ScreenTimeTracker.Helpers
{
    /// <summary>
    /// Helper class for chart visualization and time formatting
    /// </summary>
    public class ChartHelper
    {
        /// <summary>
        /// Updates the usage chart with hourly data
        /// </summary>
        /// <param name="chart">The chart control to update</param>
        /// <param name="usageRecords">Collection of usage records to visualize</param>
        /// <param name="viewMode">The chart view mode (Hourly or Daily)</param>
        /// <param name="timePeriod">The time period (Daily, Weekly, etc.)</param>
        /// <param name="selectedDate">The selected date for filtering</param>
        /// <param name="selectedEndDate">Optional end date for range selection</param>
        /// <returns>The total time calculated from the records</returns>
        public static TimeSpan UpdateUsageChart(
            LiveChartsCore.SkiaSharpView.WinUI.CartesianChart chart, 
            ICollection<AppUsageRecord> usageRecords,
            ChartViewMode viewMode,
            TimePeriod timePeriod,
            DateTime selectedDate,
            DateTime? selectedEndDate = null)
        {
            System.Diagnostics.Debug.WriteLine("=== UpdateUsageChart CALLED ===");

            if (chart == null)
            {
                System.Diagnostics.Debug.WriteLine("Chart is null, exiting");
                return TimeSpan.Zero;
            }

            // Calculate total time for the chart title
            TimeSpan totalTime = TimeSpan.Zero;
            foreach (var record in usageRecords)
            {
                totalTime += record.Duration;
            }
            
            System.Diagnostics.Debug.WriteLine($"Total time for chart: {totalTime}");
            
            // Get system accent color for chart series
            SKColor seriesColor;
            
            // Try to get the system accent color
            try
            {
                if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object accentColorObj) && 
                    accentColorObj is Windows.UI.Color accentColor)
                {
                    seriesColor = new SKColor(accentColor.R, accentColor.G, accentColor.B);
                }
                else
                {
                    // Default fallback color if system accent color isn't available
                    seriesColor = SKColors.DodgerBlue;
                }
            }
            catch
            {
                // Fallback color if anything goes wrong
                seriesColor = SKColors.DodgerBlue;
            }
            
            // Create a darker color for axis text for better contrast
            SKColor axisColor = SKColors.Black;
            if (Application.Current.RequestedTheme == ApplicationTheme.Dark)
            {
                axisColor = SKColors.White;
            }
            
            if (viewMode == ChartViewMode.Hourly)
            {
                System.Diagnostics.Debug.WriteLine("Building HOURLY chart");
                
                // Create a dictionary to store hourly usage
                var hourlyUsage = new Dictionary<int, double>();
                
                // Initialize all hours to 0
                for (int i = 0; i < 24; i++)
                {
                    hourlyUsage[i] = 0;
                }

                // Process all usage records to distribute time by hour
                System.Diagnostics.Debug.WriteLine($"Processing {usageRecords.Count} records for hourly chart");
                foreach (var record in usageRecords)
                {
                    // Get the hour from the start time
                    int startHour = record.StartTime.Hour;
                    
                    // Add the duration to the appropriate hour (convert to hours)
                    hourlyUsage[startHour] += record.Duration.TotalHours;
                    
                    System.Diagnostics.Debug.WriteLine($"Record: {record.ProcessName}, Hour: {startHour}, Duration: {record.Duration.TotalHours:F4} hours");
                }

                // Check if all values are zero
                bool allZero = true;
                double maxValue = 0;
                foreach (var value in hourlyUsage.Values)
                {
                    if (value > 0.0001)
                    {
                        allZero = false;
                    }
                    maxValue = Math.Max(maxValue, value);
                }
                
                System.Diagnostics.Debug.WriteLine($"All values zero? {allZero}, Max value: {maxValue:F4}");
                
                // If all values are zero, add a tiny value to the current hour to make the chart visible
                if (allZero || maxValue < 0.001)
                {
                    int currentHour = DateTime.Now.Hour;
                    hourlyUsage[currentHour] = 0.001; // Add a tiny value
                    System.Diagnostics.Debug.WriteLine($"Added tiny value to hour {currentHour}");
                }

                // Set up series and labels for the chart
                var values = new List<double>();
                var labels = new List<string>();
                
                // Create a more concise label format for narrow windows
                bool useShortLabels = chart.ActualWidth < 500;
                
                // Add values and labels for each hour (with spacing logic)
                for (int i = 0; i < 24; i++)
                {
                    values.Add(hourlyUsage[i]);
                    
                    // Use spacing logic to prevent label crowding
                    // For narrow screens, we only show labels every 2 or 3 hours
                    if (useShortLabels)
                    {
                        // Very narrow: show label only for 12am, 6am, 12pm, 6pm
                        if (i % 6 == 0)
                        {
                            labels.Add($"{(i % 12 == 0 ? 12 : i % 12)}{(i >= 12 ? "p" : "a")}");
                        }
                        else
                        {
                            labels.Add(""); // Empty label for hours we're skipping
                        }
                    }
                    else if (chart.ActualWidth < 700)
                    {
                        // Narrow: show label every 3 hours (12am, 3am, 6am, etc.)
                        if (i % 3 == 0)
                        {
                            labels.Add($"{(i % 12 == 0 ? 12 : i % 12)}{(i >= 12 ? "PM" : "AM")}");
                        }
                        else
                        {
                            labels.Add(""); // Empty label for hours we're skipping
                        }
                    }
                    else if (chart.ActualWidth < 900)
                    {
                        // Medium: show label every 2 hours
                        if (i % 2 == 0)
                        {
                            labels.Add($"{(i % 12 == 0 ? 12 : i % 12)}{(i >= 12 ? "PM" : "AM")}");
                        }
                        else
                        {
                            labels.Add(""); // Empty label for hours we're skipping
                        }
                    }
                    else
                    {
                        // Wide: show all hour labels
                        labels.Add($"{(i % 12 == 0 ? 12 : i % 12)} {(i >= 12 ? "PM" : "AM")}");
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"Hour {i}: {hourlyUsage[i]:F4} hours -> {labels[i]}");
                }

                // Determine a good Y-axis maximum based on the actual data
                double yAxisMax = maxValue;
                if (yAxisMax < 0.005) yAxisMax = 0.01;  // Very small values
                else if (yAxisMax < 0.05) yAxisMax = 0.1;  // Small values
                else if (yAxisMax < 0.5) yAxisMax = 1;  // Medium values
                else yAxisMax = Math.Ceiling(yAxisMax * 1.2);  // Large values
                
                System.Diagnostics.Debug.WriteLine($"Setting Y-axis max to {yAxisMax:F4}");

                // Create the line series with system accent color
                var lineSeries = new LineSeries<double>
                {
                    Values = values,
                    Fill = null,
                    GeometrySize = 4, // Add small data points for better visibility
                    Stroke = new SolidColorPaint(seriesColor, 2.5f), // Use system accent color with slightly thicker line
                    GeometryStroke = new SolidColorPaint(seriesColor, 2), // Match stroke color
                    GeometryFill = new SolidColorPaint(SKColors.White), // White fill for points
                    Name = "Usage"
                };

                // Set up the axes with improved contrast
                chart.XAxes = new Axis[]
                {
                    new Axis
                    {
                        Labels = labels,
                        LabelsRotation = useShortLabels ? 0 : 45, // Rotate labels when space is limited
                        ForceStepToMin = true,
                        MinStep = 1,
                        TextSize = 11, // Slightly larger text
                        LabelsPaint = new SolidColorPaint(axisColor), // More contrast
                        SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100)) // Subtle grid lines
                    }
                };

                chart.YAxes = new Axis[]
                {
                    new Axis
                    {
                        Name = "Hours",
                        NamePaint = new SolidColorPaint(axisColor),
                        NameTextSize = 12,
                        LabelsPaint = new SolidColorPaint(axisColor),
                        TextSize = 11, // Slightly larger text
                        MinLimit = 0,
                        MaxLimit = yAxisMax,
                        ForceStepToMin = true,
                        MinStep = yAxisMax > 4 ? 2 : (yAxisMax < 0.1 ? 0.05 : 0.5),  // Use 2h steps for larger values
                        Labeler = FormatHoursForYAxis,
                        SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100)) // Subtle grid lines
                    }
                };

                // Update the chart with new series
                chart.Series = new ISeries[] { lineSeries };
                
                System.Diagnostics.Debug.WriteLine("Hourly chart updated with values");
            }
            else // Daily view
            {
                System.Diagnostics.Debug.WriteLine("Building DAILY chart");
                
                int daysToShow = 7;
                if (timePeriod == TimePeriod.Daily)
                    daysToShow = 1;
                else if (timePeriod == TimePeriod.Weekly)
                    daysToShow = 7;
                
                System.Diagnostics.Debug.WriteLine($"Days to show for daily chart: {daysToShow}");

                var values = new List<double>();
                var labels = new List<string>();
                
                DateTime currentDate = DateTime.Now.Date;
                DateTime startDate = selectedDate.Date.AddDays(-(daysToShow - 1));

                // Handle custom date range if specified
                if (selectedEndDate.HasValue && selectedDate < selectedEndDate.Value)
                {
                    startDate = selectedDate;
                    daysToShow = (selectedEndDate.Value - selectedDate).Days + 1;
                }

                // Create a dictionary to track the days and their data to prevent duplication
                var dayData = new Dictionary<DateTime, double>();
                var dayLabels = new Dictionary<DateTime, string>();

                // Get data for each day in the range
                for (int i = 0; i < daysToShow; i++)
                {
                    DateTime date = startDate.AddDays(i);
                    double totalHours = 0;
                    
                    // Calculate total hours for this date from records
                    if (date.Date == DateTime.Now.Date)
                    {
                        // For today, use all records with today's date
                        foreach (var record in usageRecords.Where(r => r.StartTime.Date == date.Date))
                        {
                            totalHours += record.Duration.TotalHours;
                        }
                    }
                    else
                    {
                        // For past days, check records with matching date
                        foreach (var record in usageRecords.Where(r => r.StartTime.Date == date.Date))
                        {
                            totalHours += record.Duration.TotalHours;
                        }
                    }
                    
                    // Store data in dictionary to prevent duplicate entries
                    dayData[date.Date] = totalHours;
                    
                    // Determine appropriate label
                    string label;
                    if (date.Date == currentDate)
                    {
                        label = "Today";
                    }
                    else if (date.Date == currentDate.AddDays(-1))
                    {
                        label = "Yesterday";
                    }
                    else
                    {
                        // Use abbreviated day name (Mon, Tue, etc.)
                        label = date.ToString("ddd");
                    }
                    
                    // Store the label
                    dayLabels[date.Date] = label;
                    
                    System.Diagnostics.Debug.WriteLine($"Day {i} ({date:M/d}): {totalHours:F2} hours with label '{label}'");
                }

                // Now add the data and labels to the chart in the correct order
                for (int i = 0; i < daysToShow; i++)
                {
                    DateTime date = startDate.AddDays(i);
                    values.Add(dayData[date.Date]);
                    labels.Add(dayLabels[date.Date]);
                    
                    System.Diagnostics.Debug.WriteLine($"Added to chart: Day {i} ({date:M/d}): {dayData[date.Date]:F2} hours -> {dayLabels[date.Date]}");
                }

                // If all values are zero, add a tiny value to make the chart visible
                bool allZero = values.All(v => v < 0.0001);
                double maxValue = values.Count > 0 ? values.Max() : 0;
                
                if (allZero || maxValue < 0.001)
                {
                    System.Diagnostics.Debug.WriteLine("All values are zero, adding tiny value to make chart visible");
                    values[values.Count - 1] = 0.001;  // Add tiny value to the most recent day
                    maxValue = 0.001;
                }

                // Determine a good Y-axis maximum based on the actual data
                double yAxisMax = maxValue;
                if (yAxisMax < 0.005) yAxisMax = 0.01;  // Very small values
                else if (yAxisMax < 0.05) yAxisMax = 0.1;  // Small values
                else if (yAxisMax < 0.5) yAxisMax = 1;  // Medium values
                else yAxisMax = Math.Ceiling(yAxisMax * 1.2);  // Large values
                
                System.Diagnostics.Debug.WriteLine($"Setting Y-axis max to {yAxisMax:F4}");

                // Create the column series with system accent color
                var columnSeries = new ColumnSeries<double>
                {
                    Values = values,
                    Fill = new SolidColorPaint(seriesColor), // Use system accent color
                    Stroke = null, // No border
                    MaxBarWidth = 30, // Limit maximum width for better appearance with few columns
                    Name = "Usage"
                };

                // Set up the axes with improved contrast
                chart.XAxes = new Axis[]
                {
                    new Axis
                    {
                        Labels = labels,
                        LabelsRotation = 0, // Flat labels for days
                        ForceStepToMin = true,
                        MinStep = 1,
                        TextSize = 11, // Slightly larger text
                        LabelsPaint = new SolidColorPaint(axisColor), // More contrast
                        SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100)) // Subtle grid lines
                    }
                };

                chart.YAxes = new Axis[]
                {
                    new Axis
                    {
                        Name = "Hours",
                        NamePaint = new SolidColorPaint(axisColor),
                        NameTextSize = 12,
                        LabelsPaint = new SolidColorPaint(axisColor),
                        TextSize = 11, // Slightly larger text
                        MinLimit = 0,
                        MaxLimit = yAxisMax,
                        ForceStepToMin = true,
                        MinStep = yAxisMax > 4 ? 2 : (yAxisMax < 0.1 ? 0.05 : 0.5),  // Use 2h steps for larger values
                        Labeler = FormatHoursForYAxis,
                        SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100)) // Subtle grid lines
                    }
                };

                // Update the chart with new series
                chart.Series = new ISeries[] { columnSeries };
                
                System.Diagnostics.Debug.WriteLine("Daily chart updated with values");
            }
            
            // Set additional chart properties for better appearance
            chart.LegendPosition = LiveChartsCore.Measure.LegendPosition.Hidden;
            chart.AnimationsSpeed = TimeSpan.FromMilliseconds(300);
            
            System.Diagnostics.Debug.WriteLine($"Chart updated with {chart.Series?.Count() ?? 0} series");
            System.Diagnostics.Debug.WriteLine("=== UpdateUsageChart COMPLETED ===");
            
            return totalTime;
        }

        /// <summary>
        /// Forces a refresh of the chart by clearing and rebuilding it
        /// </summary>
        /// <param name="chart">The chart to refresh</param>
        /// <param name="usageRecords">Collection of usage records</param>
        /// <param name="viewMode">Current chart view mode</param>
        /// <param name="timePeriod">Current time period</param>
        /// <param name="selectedDate">Selected date</param>
        /// <param name="selectedEndDate">Optional end date for range</param>
        /// <returns>The total time calculated from the records</returns>
        public static TimeSpan ForceChartRefresh(
            LiveChartsCore.SkiaSharpView.WinUI.CartesianChart chart, 
            ICollection<AppUsageRecord> usageRecords,
            ChartViewMode viewMode,
            TimePeriod timePeriod,
            DateTime selectedDate,
            DateTime? selectedEndDate = null)
        {
            System.Diagnostics.Debug.WriteLine("Force refreshing chart...");
            
            // Log the current state of data
            System.Diagnostics.Debug.WriteLine($"Current records in collection: {usageRecords.Count}");
            if (usageRecords.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine("First 3 records:");
                foreach (var record in usageRecords.Take(3))
                {
                    System.Diagnostics.Debug.WriteLine($"  - {record.ProcessName}: {record.Duration.TotalMinutes:F1}m, Start={record.StartTime}");
                }
            }
            
            // Manually clear and rebuild the chart
            if (chart != null)
            {
                // First clear the chart
                chart.Series = new ISeries[] { };
                
                // Then update it
                var totalTime = UpdateUsageChart(chart, usageRecords, viewMode, timePeriod, selectedDate, selectedEndDate);
                
                System.Diagnostics.Debug.WriteLine("Chart refresh completed");
                return totalTime;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("ERROR: Chart is null, cannot refresh chart");
                return TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Formats a TimeSpan for chart display
        /// </summary>
        /// <param name="time">The TimeSpan to format</param>
        /// <returns>Formatted time string</returns>
        public static string FormatTimeSpanForChart(TimeSpan time)
        {
            if (time.TotalDays >= 1)
            {
                return $"{(int)time.TotalDays}d {time.Hours}h {time.Minutes}m";
            }
            else if (time.TotalHours >= 1)
            {
                return $"{(int)time.TotalHours}h {time.Minutes}m";
            }
            else
            {
                return $"{(int)time.TotalMinutes}m";
            }
        }

        /// <summary>
        /// Formats hours for Y-axis labels
        /// </summary>
        /// <param name="value">The value in hours</param>
        /// <returns>Formatted time string</returns>
        public static string FormatHoursForYAxis(double value)
        {
            var time = TimeSpan.FromHours(value);
            
            if (time.TotalMinutes < 1)
            {
                // Show seconds for very small values
                return $"{time.TotalSeconds:F0}s";
            }
            else if (time.TotalHours < 1)
            {
                // Show only minutes for less than an hour
                return $"{time.TotalMinutes:F0}m";
            }
            else
            {
                // Just show hour value without minutes for cleaner display
                return $"{Math.Floor(time.TotalHours)}h";
            }
        }

        /// <summary>
        /// Formats a TimeSpan for general UI display
        /// </summary>
        /// <param name="time">The TimeSpan to format</param>
        /// <returns>Formatted time string</returns>
        public static string FormatTimeSpan(TimeSpan time)
        {
            if (time.TotalDays >= 1)
            {
                return $"{(int)time.TotalDays}d {time.Hours}h {time.Minutes}m";
            }
            else if (time.TotalHours >= 1)
            {
                return $"{(int)time.TotalHours}h {time.Minutes}m";
            }
            else if (time.TotalMinutes >= 1)
            {
                return $"{(int)time.TotalMinutes}m {time.Seconds}s";
            }
            else
            {
                return $"{time.Seconds}s";
            }
        }
    }
} 