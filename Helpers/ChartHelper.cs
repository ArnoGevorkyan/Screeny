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
            if (chart == null)
            {
                return TimeSpan.Zero;
            }

            // Use unique-time calculation to avoid double-counting overlapping apps
            TimeSpan totalTime = CalculateUniqueTotalTime(usageRecords);
            
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

                    DateTime end;
                    if (record.EndTime.HasValue)
                    {
                        end = record.EndTime.Value;
                    }
                    else if (record.IsFocused)
                    {
                        end = DateTime.Now;
                    }
                    else
                    {
                        end = start + record.Duration;
                    }

                    if (end < start) end = start; // safety guard

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
            }
            else // Daily view
            {
                int daysToShow;
                DateTime rangeStartDate; // Use a specific variable for the range start

                if (selectedEndDate.HasValue && selectedDate.Date <= selectedEndDate.Value.Date)
                {
                    // Handle custom date range or "Last 7 Days" selection
                    rangeStartDate = selectedDate.Date;
                    daysToShow = (selectedEndDate.Value.Date - selectedDate.Date).Days + 1;
                }
                else // Single date or standard weekly view based on selectedDate
                {
                    // Determine days based on time period (defaulting to daily if unsure)
                    daysToShow = (timePeriod == TimePeriod.Weekly) ? 7 : 1;
                    // Calculate start date based on the END date (selectedDate) for standard views
                    rangeStartDate = selectedDate.Date.AddDays(-(daysToShow - 1));
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
                            }
                        }
                    }
                    catch (Exception)
                    {
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
                    }
                }

                // If all values are zero, add a tiny value to make the chart visible
                bool allZero = values.All(v => v < 0.0001);
                maxValue = values.Count > 0 ? values.Max() : 0;
                
                if (allZero || maxValue < 0.001)
                {
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
            }
            
            // Set additional chart properties for better appearance
            chart.LegendPosition = LiveChartsCore.Measure.LegendPosition.Hidden;
            
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
            // Manually clear and rebuild the chart
            if (chart != null)
            {
                // First clear the chart
                chart.Series = new ISeries[] { };
                
                // Then update it
                var totalTime = UpdateUsageChart(chart, usageRecords, viewMode, timePeriod, selectedDate, selectedEndDate);
                
                return totalTime;
            }
            else
            {
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
        internal static List<(DateTime Start, DateTime End)> MergeIntervals(List<(DateTime Start, DateTime End)> intervals)
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

        /// <summary>
        /// Calculates the total unique (non-overlapping) screen-time represented by the supplied records.
        /// </summary>
        /// <param name="records">Collection of <see cref="AppUsageRecord"/> instances.</param>
        /// <returns>Total time after merging all overlapping intervals.</returns>
        public static TimeSpan CalculateUniqueTotalTime(IEnumerable<AppUsageRecord> records)
        {
            if (records == null) return TimeSpan.Zero;

            var now = DateTime.Now;
            var intervals = records.Select(r =>
            {
                var start = r.StartTime;
                DateTime end;

                // Determine end timestamp
                if (r.EndTime.HasValue)
                {
                    end = r.EndTime.Value;
                }
                else
                {
                    end = r.IsFocused ? now : r.StartTime + r.Duration;
                }

                if (end < start) end = start; // Guard against corrupt data
                return (Start: start, End: end);
            }).ToList();

            var merged = MergeIntervals(intervals);
            TimeSpan total = TimeSpan.Zero;
            foreach (var iv in merged)
            {
                total += iv.End - iv.Start;
            }

            return total;
        }
    }
} 