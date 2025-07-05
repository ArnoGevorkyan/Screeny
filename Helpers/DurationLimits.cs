namespace ScreenTimeTracker.Helpers
{
    /// <summary>
    /// Central location for all time-limit constants used across duration and tracking calculations.
    /// </summary>
    public static class DurationLimits
    {
        /// <summary>Maximum continuous focus time for a single record (e.g. auto-split after 8 h).</summary>
        public static readonly System.TimeSpan MaxContinuousSession = System.TimeSpan.FromHours(8);

        /// <summary>Maximum total duration per application within a single day (e.g. 16 h safety cap).</summary>
        public static readonly System.TimeSpan MaxDailyTotal = System.TimeSpan.FromHours(16);

        /// <summary>Records older than this become stale and are purged or archived.</summary>
        public const int DaysToKeepRawRecords = 30;

        /// <summary>Number of seconds of user inactivity after which live tracking pauses (generic idle guard).</summary>
        public const int IdlePauseSeconds = 300; // 5 minutes
    }
} 