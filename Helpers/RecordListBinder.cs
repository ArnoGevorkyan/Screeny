using System;
using System.Collections.ObjectModel;
using System.Linq;
using ScreenTimeTracker.Models;

namespace ScreenTimeTracker.Helpers
{
    /// <summary>
    /// Provides unified helpers for updating the live <see cref="ObservableCollection{AppUsageRecord}"/>
    /// that backs the ListView and chart.  Consolidating these operations avoids duplicated loops
    /// scattered across the three MainWindow partial classes (T27).
    /// </summary>
    internal static class RecordListBinder
    {
        /// <summary>
        /// Replaces the contents of <paramref name="target"/> with <paramref name="source"/>,
        /// preserving the collection instance so existing UI bindings stay intact.
        /// </summary>
        public static void ReplaceWith(ObservableCollection<AppUsageRecord> target, System.Collections.Generic.IEnumerable<AppUsageRecord> source)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (source == null) source = Array.Empty<AppUsageRecord>();

            target.Clear();
            foreach (var rec in source)
            {
                target.Add(rec);
            }
        }

        /// <summary>
        /// Adds or updates <paramref name="record"/> inside <paramref name="target"/>, mirroring the logic
        /// previously implemented in <c>MainWindow.UpdateOrAddLiveRecord</c>.
        /// </summary>
        public static void UpdateOrAdd(ObservableCollection<AppUsageRecord> target, AppUsageRecord record)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (record == null) return;

            if (record.ProcessName.StartsWith("Idle", StringComparison.OrdinalIgnoreCase))
                return; // Skip synthetic idle rows in list

            ApplicationProcessingHelper.ProcessApplicationRecord(record);

            var existing = target.FirstOrDefault(r => r.ProcessName.Equals(record.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                record.LoadAppIconIfNeeded();
                target.Add(record);
            }
            else
            {
                if (!ReferenceEquals(existing, record) && record.Duration > existing.Duration)
                    existing._accumulatedDuration = record.Duration;

                if (record.IsFocused)
                {
                    foreach (var r in target) r.SetFocus(false);
                    existing.SetFocus(true);
                }

                existing.RaiseDurationChanged();
            }
        }
    }
} 