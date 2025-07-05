using System;
using System.Collections.Generic;
using System.Linq;
using ScreenTimeTracker.Models;

namespace ScreenTimeTracker.Helpers
{
    /// <summary>
    /// Miscellaneous time/interval utilities used across the app. Extracted from ChartHelper to
    /// keep that class focused on chart rendering only.
    /// </summary>
    public static class TimeUtil
    {
        /// <summary>
        /// Formats a <see cref="TimeSpan"/> for general UI display.
        /// </summary>
        public static string FormatTimeSpan(TimeSpan time)
        {
            const int MaxReasonableDays = 365;
            if (time.TotalDays > MaxReasonableDays)
            {
                time = TimeSpan.FromDays(MaxReasonableDays);
            }

            int days    = (int)time.TotalDays;
            int hours   = time.Hours;
            int minutes = time.Minutes;
            int seconds = time.Seconds;

            if (days > 0)
                return $"{days}d {hours}h {minutes}m";
            if (hours > 0)
                return $"{hours}h {minutes}m {seconds}s";
            if (minutes > 0)
                return $"{minutes}m {seconds}s";
            return $"{seconds}s";
        }

        /// <summary>
        /// Label formatter for Y-axis values expressed in hours.
        /// </summary>
        public static string FormatHoursForYAxis(double value)
        {
            var time = TimeSpan.FromHours(value);
            if (time.TotalMinutes < 1)
                return $"{time.TotalSeconds:F0}s";
            if (time.TotalHours < 1)
                return $"{time.TotalMinutes:F0}m";
            return $"{Math.Floor(time.TotalHours)}h";
        }

        /// <summary>
        /// Merges potentially overlapping intervals into a non-overlapping list.
        /// </summary>
        internal static List<(DateTime Start, DateTime End)> MergeIntervals(List<(DateTime Start, DateTime End)> intervals)
        {
            if (intervals == null || intervals.Count == 0) return new List<(DateTime Start, DateTime End)>();

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
        /// Sums durations of <see cref="AppUsageRecord"/> collection (already de-duplicated upstream).
        /// </summary>
        public static TimeSpan CalculateUniqueTotalTime(IEnumerable<AppUsageRecord> records)
        {
            return records?.Aggregate(TimeSpan.Zero, (sum, r) => sum + r.Duration) ?? TimeSpan.Zero;
        }
    }
} 