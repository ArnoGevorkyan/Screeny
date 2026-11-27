using System;
using System.Collections.Generic;
using ScreenTimeTracker.Models;

namespace ScreenTimeTracker.Helpers;

internal sealed record DashboardUsage(List<AppUsageRecord> Visits, TimeSpan Total, TimeSpan DailyAverage)
{
    internal static DashboardUsage Calculate(IEnumerable<AppUsageRecord> records, DateTime start, DateTime end, DateTime now)
    {
        start = start.Date;
        end = end.Date;
        var maxTotalTicks = ((end - start).Days + 1L) * TimeSpan.TicksPerDay;
        var visits = new List<AppUsageRecord>();
        var activeDays = new HashSet<DateTime>();
        long totalTicks = 0;

        foreach (var record in records)
        {
            var day = (record.Date == default ? record.StartTime : record.Date).Date;
            if (day < start || day > end || day > now.Date ||
                record.EndTime is not { } endTime || endTime <= record.StartTime ||
                (record.Id != 0 ? record.IsFocused : endTime > now) ||
                ProcessFilter.ShouldIgnoreProcess(ApplicationNameNormalizer.NormalizeProcessName(record.ProcessName)))
                continue;

            var duration = record.Duration;
            if (duration <= TimeSpan.Zero) continue;

            visits.Add(record);
            activeDays.Add(day);
            totalTicks = Math.Min(totalTicks + duration.Ticks, maxTotalTicks);
        }

        return new DashboardUsage(visits, TimeSpan.FromTicks(totalTicks),
            TimeSpan.FromTicks(totalTicks / Math.Max(1, activeDays.Count)));
    }
}
