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
        /// <param name="liveFocusedRecord">The currently active/focused record from the tracking service (optional)</param>
        /// <returns>The total time calculated from the records</returns>
        public static TimeSpan UpdateUsageChart(
            LiveChartsCore.SkiaSharpView.WinUI.CartesianChart chart, 
            ICollection<AppUsageRecord> usageRecords,
            ChartViewMode viewMode,
            TimePeriod timePeriod,
            DateTime selectedDate,
            DateTime? selectedEndDate = null,
            AppUsageRecord? liveFocusedRecord = null)
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

                // MODIFICATION: Filter to only include hours with usage
                var nonZeroHours = hourlyUsage
                    .Where(h => h.Value > 0.0001)
                    .Select(h => h.Key)
                    .OrderBy(h => h)
                    .ToList();
                
                // If we have no non-zero hours, include the current hour
                if (nonZeroHours.Count == 0)
                {
                    nonZeroHours.Add(DateTime.Now.Hour);
                }
                
                // Add padding hours before and after to provide context
                int earliestHour = nonZeroHours.First();
                int latestHour = nonZeroHours.Last();
                
                // Ensure we have at least 6 hours visible or expand to include all non-zero hours plus padding
                int filteredStartHour, filteredEndHour;
                
                if (nonZeroHours.Count <= 3)
                {
                    // For very few data points, show a reasonable window centered on the data
                    int middleHour = (earliestHour + latestHour) / 2;
                    filteredStartHour = Math.Max(0, middleHour - 3);
                    filteredEndHour = Math.Min(23, middleHour + 3);
                }
                else
                {
                    // Add padding before and after
                    filteredStartHour = Math.Max(0, earliestHour - 1);  
                    filteredEndHour = Math.Min(23, latestHour + 1);
                }
                
                // Ensure minimum range of hours for context
                if (filteredEndHour - filteredStartHour < 5)
                {
                    // Expand the range to show at least 6 hours
                    while (filteredEndHour - filteredStartHour < 5 && (filteredStartHour > 0 || filteredEndHour < 23))
                    {
                        if (filteredStartHour > 0) filteredStartHour--;
                        if (filteredEndHour < 23 && filteredEndHour - filteredStartHour < 5) filteredEndHour++;
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"Filtered to hours {filteredStartHour}-{filteredEndHour} based on usage");

                // Set up series and labels for the chart
                var values = new List<double>();
                var labels = new List<string>();
                
                // Create a more concise label format for narrow windows
                bool useShortLabels = chart.ActualWidth < 500;
                
                // Add values and labels for each hour in our filtered range
                for (int i = filteredStartHour; i <= filteredEndHour; i++)
                {
                    values.Add(hourlyUsage[i]);
                    
                    // Use spacing logic to prevent label crowding
                    // For narrow screens, we only show labels every 2 or 3 hours
                    if (useShortLabels)
                    {
                        // Very narrow: show label only for specific hours
                        if ((i % 3 == 0) || i == filteredStartHour || i == filteredEndHour)
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
                        // Narrow: show label every 3 hours or start/end
                        if (i % 2 == 0 || i == filteredStartHour || i == filteredEndHour)
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
                    
                    System.Diagnostics.Debug.WriteLine($"Hour {i}: {hourlyUsage[i]:F4} hours -> {labels[values.Count-1]}");
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

                int daysToShow;
                DateTime rangeStartDate; // Use a specific variable for the range start

                if (selectedEndDate.HasValue && selectedDate.Date <= selectedEndDate.Value.Date)
                {
                    // Handle custom date range or "Last 7 Days" selection
                    rangeStartDate = selectedDate.Date;
                    daysToShow = (selectedEndDate.Value.Date - selectedDate.Date).Days + 1;
                    System.Diagnostics.Debug.WriteLine($"Date Range selected: {rangeStartDate:d} to {selectedEndDate.Value.Date:d} ({daysToShow} days)");
                }
                else // Single date or standard weekly view based on selectedDate
                {
                    // Determine days based on time period (defaulting to daily if unsure)
                    daysToShow = (timePeriod == TimePeriod.Weekly) ? 7 : 1;
                    // Calculate start date based on the END date (selectedDate) for standard views
                    rangeStartDate = selectedDate.Date.AddDays(-(daysToShow - 1));
                    System.Diagnostics.Debug.WriteLine($"Single Date/Weekly selected: {selectedDate:d}, TimePeriod: {timePeriod}, StartDate: {rangeStartDate:d} ({daysToShow} days)");
                }

                var values = new List<double>();
                var labels = new List<string>();
                DateTime currentDate = DateTime.Now.Date; // Use Today's date for labeling

                // First, prepare a dictionary with all dates in the range initialized to zero
                // This ensures every day has a value and is shown in the chart
                var dayData = new Dictionary<DateTime, double>();
                var dayLabels = new Dictionary<DateTime, string>();

                // Initialize all days in the range with zero values and proper labels
                for (int i = 0; i < daysToShow; i++)
                {
                    DateTime date = rangeStartDate.AddDays(i);
                    
                    // Set label for this date
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
                        // Use abbreviated month and day (e.g., "Apr 1")
                        label = date.ToString("MMM d");
                    }
                    
                    // Initialize with zero usage
                    dayData[date.Date] = 0;
                    dayLabels[date.Date] = label;
                    
                    System.Diagnostics.Debug.WriteLine($"Initialized day {i} ({date:M/d}) with zero usage");
                }

                // Now process all records and add their durations to the appropriate days
                foreach (var record in usageRecords)
                {
                    try
                    {
                        // Get the correct date for this record - use StartTime's date not the Date property
                        // This is more reliable for determining which day this record belongs to
                        DateTime recordDate = record.StartTime.Date;
                        
                        // Check if this record's date is within our range
                        if (recordDate >= rangeStartDate.Date && recordDate <= (rangeStartDate.AddDays(daysToShow - 1)).Date)
                        {
                            // Add this record's duration to the appropriate day
                            if (dayData.ContainsKey(recordDate))
                            {
                                double hours = record.Duration.TotalHours;
                                dayData[recordDate] += hours;
                                System.Diagnostics.Debug.WriteLine($"Added {hours:F2} hours for {record.ProcessName} on {recordDate:yyyy-MM-dd} (StartTime: {record.StartTime:HH:mm:ss}), Total: {dayData[recordDate]:F2}h");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"Warning: Day {recordDate:yyyy-MM-dd} not found in chart days dictionary");
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"Record for {record.ProcessName} on {recordDate:yyyy-MM-dd} (StartTime: {record.StartTime:HH:mm:ss}) is outside the chart range");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error processing record for chart: {ex.Message}");
                    }
                }

                // Now add the data and labels to the chart in chronological order
                for (int i = 0; i < daysToShow; i++)
                {
                    DateTime date = rangeStartDate.AddDays(i);
                    
                    // Get the data and label for this day
                    if (dayData.TryGetValue(date.Date, out double hours) && dayLabels.TryGetValue(date.Date, out string? lbl))
                    {
                        values.Add(hours);
                        labels.Add(lbl);
                        System.Diagnostics.Debug.WriteLine($"Added to chart: Day {i} ({date:M/d}): {hours:F2} hours -> {lbl}");
                    }
                    else
                    {
                        // This should never happen with our initialization, but just in case
                        values.Add(0);
                        labels.Add(date.ToString("MMM d"));
                        System.Diagnostics.Debug.WriteLine($"WARNING: Missing data or label for date {date:M/d}");
                    }
                }

                // Debug all values to check
                for (int i = 0; i < values.Count; i++)
                {
                    System.Diagnostics.Debug.WriteLine($"Chart value at index {i} ({labels[i]}): {values[i]:F4} hours");
                }

                // If all values are zero, add a tiny value to make the chart visible
                bool allZero = values.All(v => v < 0.0001);
                double maxValue = values.Count > 0 ? values.Max() : 0;
                
                if (allZero || maxValue < 0.001)
                {
                    System.Diagnostics.Debug.WriteLine("All values are zero, adding tiny value to make chart visible");
                    // Only add the tiny value to today or the last day if today isn't in range
                    int todayIndex = labels.IndexOf("Today");
                    if (todayIndex >= 0)
                    {
                        values[todayIndex] = 0.001;
                    }
                    else
                    {
                        values[values.Count - 1] = 0.001;
                    }
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