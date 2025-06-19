using ScreenTimeTracker.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using ScreenTimeTracker.Helpers;

namespace ScreenTimeTracker.Services
{
    /// <summary>
    /// Provides aggregation helpers that merge database data and optional live tracking data
    /// into de-duplicated <see cref="AppUsageRecord"/> collections ready for presentation.
    /// </summary>
    public sealed class UsageAggregationService
    {
        private readonly DatabaseService _databaseService;
        private readonly WindowTrackingService _trackingService;

        public UsageAggregationService(DatabaseService databaseService, WindowTrackingService trackingService)
        {
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            _trackingService = trackingService ?? throw new ArgumentNullException(nameof(trackingService));
        }

        /// <summary>
        /// Returns a de-duplicated list of application records spanning <paramref name="startDate"/> → <paramref name="endDate"/>.
        /// Durations from the database are merged with any live records reported by <see cref="WindowTrackingService"/>,
        /// ensuring no application appears more than once.
        /// </summary>
        public List<AppUsageRecord> GetAggregatedRecordsForDateRange(DateTime startDate, DateTime endDate, bool includeLiveRecords = true)
        {
            // Normalise order
            if (startDate > endDate)
            {
                (startDate, endDate) = (endDate, startDate);
            }

            var unique = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);

            // --- ① database roll-up (already aggregated via SQL) ---
            var dbReport = _databaseService.GetUsageReportForDateRange(startDate, endDate);
            foreach (var (processNameRaw, totalDuration) in dbReport)
            {
                // Canonicalise name (e.g., strip ".Root", helper suffixes, etc.)
                var temp = new AppUsageRecord { ProcessName = processNameRaw };
                ApplicationProcessingHelper.ProcessApplicationRecord(temp);
                var processName = temp.ProcessName;

                unique[processName] = new AppUsageRecord
                {
                    ProcessName          = processName,
                    ApplicationName      = processName,
                    _accumulatedDuration = totalDuration,
                    Date                 = startDate,
                    StartTime            = startDate
                };
            }

            // --- ② merge live records (optional) ---
            if (includeLiveRecords && endDate.Date >= DateTime.Today)
            {
                var live = _trackingService.GetRecords()
                                             .Where(r => r.Date >= startDate && r.Date <= endDate)
                                             .ToList();

                foreach (var liveRec in live)
                {
                    if (liveRec.Duration.TotalSeconds <= 0) continue;

                    var canonicalLive = liveRec.ProcessName;
                    // Extra safety: normalise again in case historical record wasn't processed
                    var tmpLive = new AppUsageRecord { ProcessName = canonicalLive, WindowTitle = liveRec.WindowTitle };
                    ApplicationProcessingHelper.ProcessApplicationRecord(tmpLive);
                    canonicalLive = tmpLive.ProcessName;

                    if (unique.TryGetValue(canonicalLive, out var existing))
                    {
                        existing._accumulatedDuration += liveRec.Duration;
                        existing.WindowHandle          = liveRec.WindowHandle;
                        if (!string.IsNullOrEmpty(liveRec.WindowTitle)) existing.WindowTitle = liveRec.WindowTitle;
                        if (liveRec.StartTime < existing.StartTime)      existing.StartTime  = liveRec.StartTime;
                    }
                    else
                    {
                        // clone to decouple from tracking collection
                        var clone = new AppUsageRecord
                        {
                            ProcessName          = canonicalLive,
                            ApplicationName      = liveRec.ApplicationName,
                            WindowTitle          = liveRec.WindowTitle,
                            WindowHandle         = liveRec.WindowHandle,
                            _accumulatedDuration = liveRec.Duration,
                            Date                 = liveRec.Date,
                            StartTime            = liveRec.StartTime,
                            IsFocused            = liveRec.IsFocused
                        };
                        unique[clone.ProcessName] = clone;
                    }
                }
            }

            // --- ③ filter & sort ---
            var result = unique.Values
                                 .Where(r => !IsWindowsSystemProcess(r.ProcessName))
                                 .OrderByDescending(r => r.Duration.TotalSeconds)
                                 .ToList();

            // If everything was filtered out (all system processes), show top 5 anyway to avoid empty UI.
            if (result.Count == 0)
            {
                result = unique.Values
                                 .OrderByDescending(r => r.Duration.TotalSeconds)
                                 .Take(5)
                                 .ToList();
            }

            return result;
        }

        // Minimal copy of the helper previously in MainWindow
        private static bool IsWindowsSystemProcess(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return false;
            var n = processName.Trim().ToLowerInvariant();

            string[] highPriority =
            {
                "explorer","shellexperiencehost","searchhost","startmenuexperiencehost","applicationframehost",
                "systemsettings","dwm","winlogon","csrss","services","svchost","runtimebroker"
            };
            if (highPriority.Any(p => n.Contains(p))) return true;

            string[] others =
            {
                "textinputhost","windowsterminal","cmd","powershell","pwsh","conhost","winstore.app",
                "lockapp","logonui","fontdrvhost","taskhostw","ctfmon","rundll32","dllhost","sihost",
                "taskmgr","backgroundtaskhost","smartscreen","securityhealthservice","registry",
                "microsoftedgeupdate","wmiprvse","spoolsv","tabtip","tabtip32","searchui","searchapp",
                "settingssynchost","wudfhost"
            };
            return others.Contains(n);
        }

        public List<AppUsageRecord> GetDetailRecordsForDate(DateTime date)
        {
            var list = _databaseService.GetRecordsForDate(date) ?? new List<AppUsageRecord>();

            // Append live records when looking at today so the UI shows ongoing activity.
            if (date.Date == DateTime.Today)
            {
                var live = _trackingService.GetRecords()
                                             .Where(r => r.IsFromDate(date))
                                             .ToList();
                list.AddRange(live);
            }

            // ----  Deduplicate and filter unwanted rows  ----
            var byProcess = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);

            foreach (var rec in list)
            {
                // Canonicalise name first
                var tmp = new AppUsageRecord { ProcessName = rec.ProcessName, WindowTitle = rec.WindowTitle };
                ApplicationProcessingHelper.ProcessApplicationRecord(tmp);
                var canonical = tmp.ProcessName;

                // Skip Screeny itself and known system processes
                if (IsWindowsSystemProcess(canonical) || canonical.Equals("Screeny", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (byProcess.TryGetValue(canonical, out var existing))
                {
                    // Merge – accumulate duration, keep earliest start time and latest end time
                    existing._accumulatedDuration += rec.Duration;

                    if (rec.StartTime < existing.StartTime) existing.StartTime = rec.StartTime;
                    if (rec.EndTime.HasValue)
                    {
                        if (!existing.EndTime.HasValue || rec.EndTime > existing.EndTime) existing.EndTime = rec.EndTime;
                    }
                }
                else
                {
                    // Update record's canonical name before storing
                    rec.ProcessName = canonical;
                    byProcess[canonical] = rec;
                }
            }

            return byProcess.Values
                             .OrderByDescending(r => r.Duration.TotalSeconds)
                             .ToList();
        }

        public List<AppUsageRecord> GetDetailRecordsForDateRange(DateTime startDate, DateTime endDate)
        {
            if (startDate > endDate)
                (startDate, endDate) = (endDate, startDate);

            var source = _databaseService.GetRecordsForDateRange(startDate, endDate) ?? new List<AppUsageRecord>();

            if (endDate.Date >= DateTime.Today)
            {
                var live = _trackingService.GetRecords()
                    .Where(r => r.Date >= startDate && r.Date <= endDate)
                    .ToList();
                source.AddRange(live);
            }

            var merged = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);

            foreach (var rec in source)
            {
                // Canonicalise name first
                var tmp = new AppUsageRecord { ProcessName = rec.ProcessName, WindowTitle = rec.WindowTitle };
                ApplicationProcessingHelper.ProcessApplicationRecord(tmp);
                var canonical = tmp.ProcessName;

                // Skip Screeny itself and known system processes
                if (IsWindowsSystemProcess(canonical) || canonical.Equals("Screeny", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (merged.TryGetValue(canonical, out var existing))
                {
                    existing._accumulatedDuration += rec.Duration;
                    if (rec.StartTime < existing.StartTime) existing.StartTime = rec.StartTime;
                    if (rec.EndTime.HasValue)
                    {
                        if (!existing.EndTime.HasValue || rec.EndTime > existing.EndTime) existing.EndTime = rec.EndTime;
                    }
                }
                else
                {
                    rec.ProcessName = canonical;
                    merged[canonical] = rec;
                }
            }

            return merged.Values
                         .OrderByDescending(r => r.Duration.TotalSeconds)
                         .ToList();
        }
    }
} 