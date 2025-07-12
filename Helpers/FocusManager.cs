using ScreenTimeTracker.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ScreenTimeTracker.Helpers
{
    /// <summary>
    /// Centralizes focus management to ensure only one record is focused at a time
    /// and eliminates duplicate "clear all, set one" patterns.
    /// </summary>
    public static class FocusManager
    {
        /// <summary>
        /// Sets focus to the specified record, clearing focus from all others in the collection.
        /// Returns the previously focused record, if any.
        /// </summary>
        public static AppUsageRecord? SetFocusedRecord(IEnumerable<AppUsageRecord> records, AppUsageRecord? recordToFocus)
        {
            var recordsList = records.ToList();
            var previouslyFocused = recordsList.FirstOrDefault(r => r.IsFocused);

            // Clear all focus first
            foreach (var record in recordsList)
            {
                if (record.IsFocused)
                {
                    record.SetFocus(false);
                }
            }

            // Set focus on the target record
            recordToFocus?.SetFocus(true);

            return previouslyFocused;
        }

        /// <summary>
        /// Clears focus from all records in the collection.
        /// Returns the previously focused record, if any.
        /// </summary>
        public static AppUsageRecord? ClearAllFocus(IEnumerable<AppUsageRecord> records)
        {
            return SetFocusedRecord(records, null);
        }

        /// <summary>
        /// Finds the currently focused record in the collection.
        /// </summary>
        public static AppUsageRecord? GetFocusedRecord(IEnumerable<AppUsageRecord> records)
        {
            return records.FirstOrDefault(r => r.IsFocused);
        }

        /// <summary>
        /// Sets focus to a record by process name, clearing focus from all others.
        /// Returns true if a matching record was found and focused.
        /// </summary>
        public static bool SetFocusByProcessName(IEnumerable<AppUsageRecord> records, string processName)
        {
            var targetRecord = records.FirstOrDefault(r => 
                r.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));

            if (targetRecord != null)
            {
                SetFocusedRecord(records, targetRecord);
                return true;
            }

            return false;
        }
    }
}