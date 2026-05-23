using ScreenTimeTracker.Models;

namespace ScreenTimeTracker.Helpers
{
    public static class AppIconIdentity
    {
        public static string CreateProcessCacheKey(AppUsageRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);

            var stableProcessName = ApplicationNameNormalizer.NormalizeProcessName(record.ProcessName);
            return stableProcessName.Trim().ToLowerInvariant();
        }

        public static string CreateWindowHandleCacheKey(AppUsageRecord record)
        {
            return $"{CreateProcessCacheKey(record)}|{record.WindowHandle.ToInt64()}";
        }
    }
}
