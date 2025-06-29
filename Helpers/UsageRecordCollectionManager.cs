using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ScreenTimeTracker.Helpers;
using ScreenTimeTracker.Models;

namespace ScreenTimeTracker.Helpers
{
    /// <summary>
    /// Centralises thread-safe, duplicate-free updates to the UI-bound collection of <see cref="AppUsageRecord"/>s.
    /// </summary>
    internal sealed class UsageRecordCollectionManager
    {
        private readonly ObservableCollection<AppUsageRecord> _collection;
        private readonly Dictionary<string, AppUsageRecord>   _map;

        public UsageRecordCollectionManager(ObservableCollection<AppUsageRecord> backingCollection)
        {
            _collection = backingCollection ?? throw new ArgumentNullException(nameof(backingCollection));
            _map        = backingCollection.ToDictionary(GetKey, r => r, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Adds a record or merges it with the existing one (same canonical key).
        /// Must be called on the UI thread.
        /// </summary>
        public void AddOrUpdate(AppUsageRecord incoming)
        {
            // Ensure canonical name has been applied so all slices of the same app share a key
            ApplicationProcessingHelper.ProcessApplicationRecord(incoming);

            string key = GetKey(incoming);

            if (_map.TryGetValue(key, out var existing))
            {
                if (!ReferenceEquals(existing, incoming))
                {
                    existing.MergeWith(incoming);

                    // For live slices (no EndTime yet), keep the longer duration to avoid double-counting
                    if (!incoming.EndTime.HasValue)
                    {
                        if (incoming._accumulatedDuration > existing._accumulatedDuration)
                            existing._accumulatedDuration = incoming._accumulatedDuration;
                    }

                    if (incoming.StartTime < existing.StartTime)
                        existing.StartTime = incoming.StartTime;

                    existing.WindowHandle = incoming.WindowHandle;
                    existing.WindowTitle  = incoming.WindowTitle;
                    existing.ProcessId    = incoming.ProcessId;
                    if (incoming.IsFocused) existing.SetFocus(true);

                    // Sweep legacy entries keyed by PID (leftover from pre-1.4) and merge them
                    var dupKeys = _map.Where(kvp => kvp.Key != key && kvp.Value.ProcessName.Equals(existing.ProcessName, StringComparison.OrdinalIgnoreCase))
                                       .Select(kvp => kvp.Key)
                                       .ToList();
                    foreach (var dk in dupKeys)
                    {
                        var dup = _map[dk];
                        existing.MergeWith(dup);
                        existing._accumulatedDuration += dup._accumulatedDuration;
                        _collection.Remove(dup);
                        _map.Remove(dk);
                    }

                    existing.RaiseDurationChanged();
                }
                else
                {
                    existing.RaiseDurationChanged();
                }

                // The canonical name might have changed (e.g., javaw → Minecraft after enrichment).
                // Re-index the map if necessary so future look-ups hit the correct key.
                var newKey = GetKey(existing);
                if (!string.Equals(newKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    _map.Remove(key);
                    _map[newKey] = existing;
                }
            }
            else
            {
                _collection.Add(incoming);
                _map[key] = incoming;
            }
        }

        private static string GetKey(AppUsageRecord r)
        {
            return r.ProcessName?.Trim().ToLowerInvariant() ?? string.Empty;
        }
    }
} 