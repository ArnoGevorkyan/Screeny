namespace ScreenTimeTracker.Helpers
{
    public static class TimeFormatter
    {
        public static string FormatTimeSpan(TimeSpan time)
        {
            const int MaxReasonableDays = 365;
            if (time.TotalDays > MaxReasonableDays)
            {
                time = TimeSpan.FromDays(MaxReasonableDays);
            }

            int days = (int)time.TotalDays;
            int hours = time.Hours;
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
    }
}
