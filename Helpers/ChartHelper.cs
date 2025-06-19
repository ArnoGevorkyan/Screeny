using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.Defaults;
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
                
                // We keep two data structures:
                // 1) hourlyIntervals  : list of all [start, end] fragments for each hour
                // 2) hourlyUsage      : final merged, non-overlapping total in hours for each hour

                var hourlyIntervals = new Dictionary<int, List<(DateTime Start, DateTime End)>>();
                var hourlyUsage     = new Dictionary<int, double>();
                
                // Initialise
                for (int h = 0; h < 24; h++)
                {
                    hourlyIntervals[h] = new List<(DateTime, DateTime)>();
                    hourlyUsage[h]     = 0;
                }

                // Collect fragments per hour without summing yet (avoids double counting)
                // Diagnostics removed for performance
                foreach (var record in usageRecords)
                {
                    var start = record.StartTime;
                    var end   = start + record.Duration;

                    for (int hr = start.Hour; hr <= end.Hour; hr++)
                    {
                        var slotStart      = start.Date.AddHours(hr);
                        var slotEnd        = slotStart.AddHours(1);
                        var overlapStart   = start > slotStart ? start : slotStart;
                        var overlapEnd     = end   < slotEnd   ? end   : slotEnd;

                        if (overlapEnd > overlapStart)
                        {
                            hourlyIntervals[hr].Add((overlapStart, overlapEnd));
                        }
                    }
                }

                // Merge overlaps inside each hour and compute the total unique time
                for (int hr = 0; hr < 24; hr++)
                {
                    var merged = MergeIntervals(hourlyIntervals[hr]);
                    double totalHr = 0;
                    foreach (var iv in merged)
                    {
                        totalHr += (iv.End - iv.Start).TotalHours;
                    }

                    // Clamp to [0,1] – do not artificially floor small values.
                    totalHr = Math.Clamp(totalHr, 0, 1);
                    hourlyUsage[hr] = totalHr;
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
                
                // Determine if we should show a compact view (no empty bars)
                bool showCompact = selectedEndDate == null &&
                                   (selectedDate.Date == DateTime.Today || selectedDate.Date == DateTime.Today.AddDays(-1));

                List<int> displayHours;
                int filteredStartHour = 0;
                int filteredEndHour   = 23;

                if (showCompact)
                {
                    // Show only hours that actually have usage
                    displayHours = nonZeroHours;
                }
                else
                {
                    // Original padding/min-range logic ----------------------
                    int earliestHour = nonZeroHours.First();
                    int latestHour   = nonZeroHours.Last();

                    if (nonZeroHours.Count <= 3)
                    {
                        int middleHour       = (earliestHour + latestHour) / 2;
                        filteredStartHour    = Math.Max(0, middleHour - 3);
                        filteredEndHour      = Math.Min(23, middleHour + 3);
                    }
                    else
                    {
                        filteredStartHour    = Math.Max(0, earliestHour - 1);
                        filteredEndHour      = Math.Min(23, latestHour + 1);
                    }

                    // Ensure at least 6 visible hours
                    while (filteredEndHour - filteredStartHour < 5 && (filteredStartHour > 0 || filteredEndHour < 23))
                    {
                        if (filteredStartHour > 0) filteredStartHour--; else filteredEndHour++;
                    }

                    // For today, never show future hours
                    if (selectedDate.Date == DateTime.Today)
                    {
                        int nowHour = DateTime.Now.Hour;
                        if (filteredEndHour > nowHour) filteredEndHour = nowHour;
                    }

                    displayHours = Enumerable.Range(filteredStartHour, filteredEndHour - filteredStartHour + 1).ToList();
                }

                // Build values + labels ---------------------------------------------------
                var values = new List<double>();
                var labels = new List<string>();

                bool useShortLabels = chart.ActualWidth < 500;

                foreach (int hr in displayHours)
                {
                    values.Add(hourlyUsage[hr]);

                    string label = "";
                    if (useShortLabels)
                    {
                        label = $"{(hr % 12 == 0 ? 12 : hr % 12)}{(hr >= 12 ? "p" : "a")}";
                    }
                    else if (chart.ActualWidth < 700)
                    {
                        label = $"{(hr % 12 == 0 ? 12 : hr % 12)}{(hr >= 12 ? "PM" : "AM")}";
                    }
                    else
                    {
                        label = $"{(hr % 12 == 0 ? 12 : hr % 12)} {(hr >= 12 ? "PM" : "AM")}";
                    }
                    labels.Add(label);
                }

                maxValue = values.Count > 0 ? values.Max() : 0;
                
                // Determine a good Y-axis maximum based on the actual data
                double yAxisMax = maxValue;
                if (yAxisMax < 0.005) yAxisMax = 0.01;  // Very small values
                else if (yAxisMax < 0.05) yAxisMax = 0.1;  // Small values
                else if (yAxisMax < 0.5) yAxisMax = 1;  // Medium values
                else yAxisMax = Math.Ceiling(yAxisMax * 1.2);  // Large values
                
                System.Diagnostics.Debug.WriteLine($"Setting Y-axis max to {yAxisMax:F4}");

                // Create column series for hourly usage (values already clamped to max 1h)
                var columnSeries = new ColumnSeries<double>
                {
                    Values       = values,
                    Fill         = new SolidColorPaint(seriesColor),
                    Stroke       = null,
                    Padding      = 0,
                    Name         = "Usage",
                    AnimationsSpeed = TimeSpan.Zero,
                    EasingFunction  = null
                };

                // To avoid the visual "jump" each second, keep the same series instance if one already
                // exists; otherwise assign the new one.  This prevents LiveCharts from animating in a
                // fresh collection every tick.
                if (chart.Series?.FirstOrDefault() is ColumnSeries<double> existingSeries)
                {
                    existingSeries.Values = values;
                }
                else
                {
                chart.Series = new ISeries[] { columnSeries };
                }

                // Ensure the chart itself does not animate property changes.
                chart.AnimationsSpeed = TimeSpan.Zero;

                // Category X-axis using hour labels
                chart.XAxes = new Axis[]
                {
                    new Axis
                    {
                        Labels = labels,
                        LabelsRotation = useShortLabels ? 0 : 45,
                        ForceStepToMin = true,
                        MinStep        = 1,
                        TextSize       = 11,
                        LabelsPaint    = new SolidColorPaint(axisColor),
                        SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100))
                    }
                };

                // Set up Y-axis fixed from 0 to 1 hour
                chart.YAxes = new Axis[]
                {
                    new Axis
                    {
                        Name            = string.Empty,
                        NamePaint       = null,
                        NameTextSize    = 12,
                        LabelsPaint     = new SolidColorPaint(axisColor),
                        TextSize        = 11,
                        MinLimit        = 0,
                        MaxLimit        = 1,
                        ForceStepToMin  = true,
                        MinStep         = 0.25,
                        Labeler         = FormatHoursForYAxis,
                        SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100))
                    }
                };

                System.Diagnostics.Debug.WriteLine("Hourly chart updated with rectangle annotations");
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
                double maxValue = 0;
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
                maxValue = values.Count > 0 ? values.Max() : 0;
                
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
                
                // Для недельного режима: округляем максимум до ближайшего чётного часа
                if (timePeriod == TimePeriod.Weekly)
                {
                    yAxisMax = Math.Ceiling(yAxisMax / 2) * 2;
                }

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
                // --- Improve X-axis label readability for long ranges ---
                // Dynamically choose a step (how many labels to skip) based on the
                // number of points to display.  Also tilt the text to avoid overlap.

                int labelCount = labels.Count;
                // Aim to render ~ 10-12 labels maximum for readability
                double targetLabelCount = 12;
                double minStep = Math.Max(1, Math.Ceiling(labelCount / targetLabelCount));

                // Rotate labels only up to 45° for readability
                double rotation = labelCount > 12 ? 45 : 0;

                chart.XAxes = new Axis[]
                {
                    new Axis
                    {
                        Labels         = labels,
                        LabelsRotation = rotation,
                        ForceStepToMin = true,
                        MinStep        = minStep,
                        TextSize       = 11,
                        LabelsPaint    = new SolidColorPaint(axisColor),
                        SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100))
                    }
                };

                chart.YAxes = new Axis[]
                {
                    new Axis
                    {
                        Name            = string.Empty,
                        NamePaint       = null,
                        NameTextSize    = 12,
                        LabelsPaint     = new SolidColorPaint(axisColor),
                        TextSize        = 11,
                        MinLimit        = 0,
                        MaxLimit        = yAxisMax,
                        ForceStepToMin  = true,
                        MinStep         = timePeriod == TimePeriod.Weekly ? 2 : (yAxisMax > 4 ? 2 : (yAxisMax < 0.1 ? 0.05 : 0.5)),
                        Labeler         = FormatHoursForYAxis,
                        SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100)) // Subtle grid lines
                    }
                };

                // Update the chart with new series
                chart.Series = new ISeries[] { columnSeries };
                
                System.Diagnostics.Debug.WriteLine("Daily chart updated with values");
            }
            
            // Set additional chart properties for better appearance
            chart.LegendPosition = LiveChartsCore.Measure.LegendPosition.Hidden;
            
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
            // Cap extremely large durations to prevent unrealistic display values
            // 365 days as reasonable maximum (1 year)
            const int MaxReasonableDays = 365;
            
            if (time.TotalDays > MaxReasonableDays)
            {
                System.Diagnostics.Debug.WriteLine($"WARNING: Capping unrealistic duration of {time.TotalDays:F1} days to {MaxReasonableDays} days");
                // Create a new TimeSpan capped at the maximum
                time = TimeSpan.FromDays(MaxReasonableDays);
            }
            
            int days = (int)time.TotalDays;
            int hours = time.Hours;
            int minutes = time.Minutes;
            int seconds = time.Seconds;

            if (days > 0)
            {
                return $"{days}d {hours}h {minutes}m"; // Show d/h/m for multiple days
            }
            else if (hours > 0)
            {
                return $"{hours}h {minutes}m {seconds}s"; // Show h/m/s for multiple hours
            }
            else if (minutes > 0)
            {
                return $"{minutes}m {seconds}s"; // Show m/s for multiple minutes
            }
            else
            {
                return $"{seconds}s"; // Show s for seconds only
            }
        }

        /// <summary>
        /// Merges a list of time intervals and returns a new list without overlaps.
        /// </summary>
        private static List<(DateTime Start, DateTime End)> MergeIntervals(List<(DateTime Start, DateTime End)> intervals)
        {
            if (intervals == null || intervals.Count == 0) return new List<(DateTime, DateTime)>();

            var ordered = intervals.OrderBy(iv => iv.Start).ToList();
            var merged  = new List<(DateTime Start, DateTime End)> { ordered[0] };

            for (int i = 1; i < ordered.Count; i++)
            {
                var current = ordered[i];
                var last    = merged[^1];

                if (current.Start <= last.End) // overlap
                {
                    merged[^1] = (last.Start, current.End > last.End ? current.End : last.End);
                }
                else
                {
                    merged.Add(current);
                }
            }

            return merged;
        }
    }
} 